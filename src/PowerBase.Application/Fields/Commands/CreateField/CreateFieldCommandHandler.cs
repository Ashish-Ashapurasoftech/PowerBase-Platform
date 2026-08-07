using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Settings;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.CreateField;

public class CreateFieldResult
{
    public long Id { get; init; }
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    public string TypeCode { get; init; } = string.Empty;
    public string PhysicalColumnName { get; init; } = string.Empty;
    public bool IsRequired { get; init; }
    public bool IsAuditable { get; init; }
    public int? Fid { get; init; }
    public string? Settings { get; init; }
    public DateTime CreatedOn { get; init; }
}

public class CreateFieldCommandHandler
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

    public CreateFieldCommandHandler(
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

    public async Task<CreateFieldResult> HandleAsync(CreateFieldCommand command, CancellationToken ct = default)
    {
        var validator = new CreateFieldCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        // Validate per-type Settings JSON before touching the database.
        CreateFieldCommandValidator.ValidateSettings(command.TypeCode, command.Settings, _settingsRegistry);

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        if (await _fieldRepo.LabelExistsInTableAsync(table.Id, command.Label, ct: ct))
            throw new DuplicateException("Field", "label", command.Label);

        var fieldType = await _fieldTypeRepo.GetByCodeAsync(command.TypeCode, ct)
            ?? throw new NotFoundException("FieldType", command.TypeCode);

        // Note: the required+None+default invariant is enforced on update and on field-permission
        // changes. A brand-new field has no field-permission rows yet, so there is nothing to check here.

        var nextFid = await _fieldRepo.GetNextFidAsync(table.Id, ct);
        var generatedName = await _fieldNameResolver.GenerateUniqueNameAsync(table.Id, command.Label, isSystem: false, ct);

        var field = new AppField
        {
            AppTableId = table.Id,
            FieldTypeId = fieldType.Id,
            TypeCode = fieldType.Code,
            Name = generatedName,
            Label = command.Label,
            Description = command.Description,
            IsRequired = command.IsRequired,
            DefaultValue = command.DefaultValue,
            Fid = nextFid,
            Settings = command.Settings,
            CreatedBy = _queryContext.UserId,
            IsSearchable = false,
            IsSortable = true,
            IsFilterable = true,
            IsReportable = true,
            IsAuditable = command.IsAuditable,
        };

        var (id, publicId) = await _fieldRepo.CreateAsync(field, ct);
        field.Id = id;
        field.PublicId = publicId;

        // Computed fields (Formula) have no physical column — skip column naming and DDL.
        var physicalColumn = string.Empty;
        if (!PhysicalNaming.IsComputedTypeCode(field.TypeCode))
        {
            physicalColumn = PhysicalNaming.ColumnName(field.Fid!.Value);
            await _fieldRepo.UpdatePhysicalColumnNameAsync(id, physicalColumn, ct);
            field.PhysicalColumnName = physicalColumn;
            await _schemaEngine.AddColumnAsync(table, field, ct);
        }

        // Auto-append the new field to all forms on this table where AutoAddNewFields=true
        var formsForTable = await _formRepo.ListByTableAsync(table.PublicId, ct);
        foreach (var form in formsForTable.Where(f => f.AutoAddNewFields))
        {
            await _formRepo.AppendFieldToLastSectionAsync(form.Id, field.Fid!.Value, ct);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, id.ToString(), $"Field added: {command.Label} To TableName : {table.Name}", appId: table.AppId, ct: ct);

        return new CreateFieldResult
        {
            Id = id,
            PublicId = publicId,
            Name = field.Name,
            Label = field.Label,
            Description = field.Description,
            TypeCode = command.TypeCode,
            PhysicalColumnName = physicalColumn,
            IsRequired = field.IsRequired,
            IsAuditable = field.IsAuditable,
            Fid = field.Fid,
            Settings = field.Settings,
            CreatedOn = DateTime.UtcNow,
        };
    }
}
