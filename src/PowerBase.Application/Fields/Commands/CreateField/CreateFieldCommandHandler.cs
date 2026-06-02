using PowerBase.Application.Common.Interfaces;
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

    public CreateFieldCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IFieldTypeRepository fieldTypeRepo,
        ISchemaEngineService schemaEngine,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IFormRepository formRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _schemaEngine = schemaEngine;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _formRepo = formRepo;
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

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        if (await _fieldRepo.NameExistsInTableAsync(table.Id, command.Name, ct))
            throw new DuplicateException("Field", "name", command.Name);

        var fieldType = await _fieldTypeRepo.GetByCodeAsync(command.TypeCode, ct)
            ?? throw new NotFoundException("FieldType", command.TypeCode);

        var field = new AppField
        {
            TenantId = _queryContext.TenantId,
            AppTableId = table.Id,
            FieldTypeId = fieldType.Id,
            Name = command.Name,
            Label = command.Label,
            Description = command.Description,
            IsRequired = command.IsRequired,
            Settings = command.Settings,
            CreatedBy = _queryContext.UserId,
        };

        var (id, publicId) = await _fieldRepo.CreateAsync(field, ct);
        field.Id = id;
        field.PublicId = publicId;

        var physicalColumn = PhysicalNaming.ColumnName(id);
        await _fieldRepo.UpdatePhysicalColumnNameAsync(id, physicalColumn, ct);
        field.PhysicalColumnName = physicalColumn;

        await _schemaEngine.AddColumnAsync(table, field, ct);

        // Auto-append the new field to all forms on this table where AutoAddNewFields=true
        var formsForTable = await _formRepo.ListByTableAsync(table.PublicId, ct);
        foreach (var form in formsForTable.Where(f => f.AutoAddNewFields))
        {
            await _formRepo.AppendFieldToLastSectionAsync(form.Id, id, _queryContext.TenantId, ct);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, id.ToString(), appId: table.AppId, ct: ct);

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
            Settings = field.Settings,
            CreatedOn = DateTime.UtcNow,
        };
    }
}
