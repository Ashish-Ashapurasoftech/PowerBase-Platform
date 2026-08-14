using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Settings;
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

    public BulkCreateFieldsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IFieldTypeRepository fieldTypeRepo,
        ISchemaEngineService schemaEngine,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IFormRepository formRepo,
        FieldSettingsValidatorRegistry settingsRegistry)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _schemaEngine = schemaEngine;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _formRepo = formRepo;
        _settingsRegistry = settingsRegistry;
    }

    public async Task<IReadOnlyList<CreateFieldResult>> HandleAsync(BulkCreateFieldsCommand command, CancellationToken ct = default)
    {
        if (command.Fields == null || command.Fields.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                { "Fields", ["At least one field is required."] }
            });

        // Validate all items up-front before touching the DB.
        var validator = new CreateFieldCommandValidator();
        var allErrors = new Dictionary<string, string[]>();

        for (var i = 0; i < command.Fields.Count; i++)
        {
            var item = command.Fields[i];
            var singleCmd = new CreateFieldCommand(
                command.TablePublicId, item.TypeCode, item.Name, item.Label,
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
        }

        if (allErrors.Count > 0)
            throw new ValidationException(allErrors);

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var formsForTable = await _formRepo.ListByTableAsync(table.PublicId, ct);

        var results = new List<CreateFieldResult>();

        foreach (var item in command.Fields)
        {
            // Check for duplicate name within the table (includes fields added in this batch
            // because each CreateAsync commits immediately).
            if (await _fieldRepo.NameExistsInTableAsync(table.Id, item.Name, ct))
                throw new DuplicateException("Field", "name", item.Name);

            var fieldType = await _fieldTypeRepo.GetByCodeAsync(item.TypeCode, ct)
                ?? throw new NotFoundException("FieldType", item.TypeCode);

            var nextFid = await _fieldRepo.GetNextFidAsync(table.Id, ct);

            var field = new AppField
            {
                AppTableId = table.Id,
                FieldTypeId = fieldType.Id,
                TypeCode = fieldType.Code,
                Name = item.Name,
                Label = item.Label,
                Description = item.Description,
                IsRequired = item.IsRequired,
                DefaultValue = item.DefaultValue,
                Fid = nextFid,
                Settings = item.Settings,
                CreatedBy = _queryContext.UserId,
                IsSearchable = true,
                IsSortable = true,
                IsFilterable = true,
                IsReportable = true,
                IsAuditable = item.IsAuditable,
                IsEncrypted = item.IsEncrypted,
            };

            var (id, publicId) = await _fieldRepo.CreateAsync(field, ct);
            field.Id = id;
            field.PublicId = publicId;

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
                $"Field added: {item.Name} To TableName : {table.Name}", appId: table.AppId, ct: ct);

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
            });
        }

        return results;
    }
}
