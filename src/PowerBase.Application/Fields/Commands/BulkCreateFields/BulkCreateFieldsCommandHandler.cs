using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Fields.Commands;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.BulkCreateFields;

public class BulkCreateFieldsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly IFormRepository _formRepo;
    private readonly FieldSettingsValidatorRegistry _settingsRegistry;
    private readonly IFieldNameResolver _fieldNameResolver;

    public BulkCreateFieldsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IFieldTypeRepository fieldTypeRepo,
        ISchemaEngineService schemaEngine,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IFormRepository formRepo,
        FieldSettingsValidatorRegistry settingsRegistry,
        IFieldNameResolver fieldNameResolver)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _schemaEngine = schemaEngine;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _formRepo = formRepo;
        _settingsRegistry = settingsRegistry;
        _fieldNameResolver = fieldNameResolver;
    }

    public async Task<IReadOnlyList<CreateFieldResult>> HandleAsync(BulkCreateFieldsCommand command, CancellationToken ct = default)
    {
        if (command.Fields == null || command.Fields.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                { "Fields", ["At least one field is required."] }
            });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        // Snapshot of fields already on the table — also doubles as the base for the duplicate-label
        // check below, and grows as each item in this batch is created, so e.g. a first-ever bulk
        // add of 3 fields fills Primary/Secondary/Tertiary in order just like adding them one at a
        // time would (see RecordPickerAutoAdvancer).
        var fieldsSoFar = (await _fieldRepo.ListByTableAsync(table.Id, ct) ?? Array.Empty<AppField>()).ToList();

        // Validate all items up-front before touching the DB.
        var validator = new CreateFieldCommandValidator();
        var allErrors = new Dictionary<string, string[]>();

        for (var i = 0; i < command.Fields.Count; i++)
        {
            var item = command.Fields[i];
            var singleCmd = new CreateFieldCommand(
                command.TablePublicId, item.TypeCode, item.Label,
                item.Description, item.IsRequired, item.IsAuditable, item.Settings, item.DefaultValue);

            var validation = await validator.ValidateAsync(singleCmd, ct);
            if (!validation.IsValid)
            {
                foreach (var g in validation.Errors.GroupBy(e => e.PropertyName))
                    allErrors[$"Fields[{i}].{g.Key}"] = g.Select(e => e.ErrorMessage).ToArray();
            }

            // Validate per-type settings
            var settingsErrors = _settingsRegistry.Validate(item.TypeCode, item.Settings);
            if (settingsErrors.Count > 0)
                allErrors[$"Fields[{i}].Settings"] = settingsErrors.Values.SelectMany(x => x).ToArray();

            // Reject Required/DefaultValue values the field's type doesn't support (IsUnique isn't
            // settable at creation time).
            var capErrors = FieldGeneralSettingsCapability.Validate(
                item.TypeCode, item.Settings, item.Label, item.IsRequired, null, item.DefaultValue);
            if (capErrors.Count > 0)
                foreach (var (key, msgs) in capErrors)
                    allErrors[$"Fields[{i}].{key}"] = msgs;
        }

        // Duplicate-label check — within this batch AND against fields that already exist on the
        // table — runs BEFORE any field is created. Without this, the loop below creates rows one
        // at a time and only discovers a duplicate once it reaches it (see LabelExistsInTableAsync
        // further down), so a batch like [f1, f1, f1] silently created the first "f1" before the
        // second one's DuplicateException aborted the request — a stray row a client wouldn't see
        // until they refreshed the page. Comparison is trim + case-insensitive, matching what
        // AppFieldRepository.LabelExistsInTableAsync itself does.
        var existingLabels = fieldsSoFar
            .Select(f => NormalizeLabel(f.Label))
            .Where(l => l.Length > 0)
            .ToHashSet();
        var seenInBatch = new HashSet<string>();
        for (var i = 0; i < command.Fields.Count; i++)
        {
            var normalized = NormalizeLabel(command.Fields[i].Label);
            if (normalized.Length == 0) continue; // NotEmpty already reported above

            if (existingLabels.Contains(normalized))
                allErrors[$"Fields[{i}].Label"] = [$"Field with label '{command.Fields[i].Label.Trim()}' already exists."];
            else if (!seenInBatch.Add(normalized))
                allErrors[$"Fields[{i}].Label"] = [$"Duplicate label '{command.Fields[i].Label.Trim()}' in this request."];
        }

        if (allErrors.Count > 0)
            throw new ValidationException(allErrors);

        var formsForTable = await _formRepo.ListByTableAsync(table.PublicId, ct);

        var results = new List<CreateFieldResult>();

        foreach (var item in command.Fields)
        {
            // Leading/trailing whitespace is never meaningful here — trim server-side so a client
            // that bypasses the UI (a direct API call) can't persist " Full Name " verbatim.
            // NullIfBlank also collapses a whitespace-only Description down to "unset", matching
            // what omitting it altogether means. item.Label is already validated NotEmpty above.
            var label = item.Label.Trim();
            var description = NullIfBlank(item.Description);

            // item.Name is only ever set by trusted internal callers (PBL/QBL import) preserving a
            // field's original stable identifier; the public API never supplies it, so the normal
            // path always auto-generates Name from Label (and enforces per-table Label uniqueness —
            // includes fields added earlier in this batch, since each CreateAsync commits immediately).
            string name;
            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                if (await _fieldRepo.NameExistsInTableAsync(table.Id, item.Name, ct))
                    throw new DuplicateException("Field", "name", item.Name);
                name = item.Name;
            }
            else
            {
                if (await _fieldRepo.LabelExistsInTableAsync(table.Id, label, ct: ct))
                    throw new DuplicateException("Field", "label", label);
                name = await _fieldNameResolver.GenerateUniqueNameAsync(table.Id, label, isSystem: false, ct);
            }

            var fieldType = await _fieldTypeRepo.GetByCodeAsync(item.TypeCode, ct)
                ?? throw new NotFoundException("FieldType", item.TypeCode);

            var nextFid = await _fieldRepo.GetNextFidAsync(table.Id, ct);

            // Searchable/Sortable/Reportable/Filterable start from the field type's own defaults
            // (see FieldAdvancedSettingsCapability) rather than one fixed set for every type. Types
            // outside the matrix keep the old fixed defaults, unchanged.
            var advancedDefaults = FieldAdvancedSettingsCapability.Resolve(fieldType.Code, item.Settings)
                ?? new FieldAdvancedSettingsCapability.Defaults(Searchable: false, Sortable: true, Reportable: true, Filterable: true, Auditable: false);

            var field = new AppField
            {
                AppTableId = table.Id,
                FieldTypeId = fieldType.Id,
                TypeCode = fieldType.Code,
                Name = name,
                Label = label,
                Description = description,
                IsRequired = item.IsRequired,
                DefaultValue = item.DefaultValue,
                Fid = nextFid,
                Settings = item.Settings,
                CreatedBy = _queryContext.UserId,
                IsSearchable = advancedDefaults.Searchable,
                IsSortable = advancedDefaults.Sortable,
                IsFilterable = advancedDefaults.Filterable,
                IsReportable = advancedDefaults.Reportable,
                IsAuditable = item.IsAuditable,
                IsEncrypted = item.IsEncrypted,
            };

            var (id, publicId) = await _fieldRepo.CreateAsync(field, ct);
            field.Id = id;
            field.PublicId = publicId;

            var nextPickerSlots = RecordPickerAutoAdvancer.NextSlots(table, fieldsSoFar, field.Id);
            if (nextPickerSlots is not null)
            {
                var (f1, f2, f3) = nextPickerSlots.Value;
                await _tableRepo.SetDefaultRecordPickerFieldsAsync(table.Id, f1, f2, f3, ct);
                table.DefaultRecordPickerField1Id = f1;
                table.DefaultRecordPickerField2Id = f2;
                table.DefaultRecordPickerField3Id = f3;
            }
            fieldsSoFar.Add(field);

            // Physical column for non-computed types
            var physicalColumn = string.Empty;
            if (!PhysicalNaming.IsComputedTypeCode(field.TypeCode))
            {
                physicalColumn = PhysicalNaming.ColumnName(field.Fid!.Value);
                await _fieldRepo.UpdatePhysicalColumnNameAsync(id, physicalColumn, ct);
                field.PhysicalColumnName = physicalColumn;
                await _schemaEngine.AddColumnAsync(table, field, ct);
            }

            // Auto-append to forms where AutoAddNewFields = true
            foreach (var form in formsForTable.Where(f => f.AutoAddNewFields))
            {
                await _formRepo.AppendFieldToLastSectionAsync(form.Id, field.Fid!.Value, ct);
            }

            await _auditRepo.LogActivityAsync(
                AuditActions.SchemaChanged, AuditEntityTypes.AppField, id.ToString(),
                $"Field added: {label} To TableName : {table.Name}", appId: table.AppId, ct: ct);

            results.Add(new CreateFieldResult
            {
                Id = id,
                PublicId = publicId,
                Name = field.Name,
                Label = field.Label,
                Description = field.Description,
                TypeCode = item.TypeCode,
                PhysicalColumnName = physicalColumn,
                IsRequired = field.IsRequired,
                IsAuditable = field.IsAuditable,
                Fid = field.Fid,
                Settings = field.Settings,
                CreatedOn = DateTime.UtcNow,
                IsEncrypted = field.IsEncrypted,
            });
        }

        return results;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeLabel(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
