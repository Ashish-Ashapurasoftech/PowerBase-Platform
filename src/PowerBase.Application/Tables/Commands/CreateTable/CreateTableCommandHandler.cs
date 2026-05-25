using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Tables.Commands.CreateTable;

public class CreateTableResult
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? SingularLabel { get; init; }
    public string? PluralLabel { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string PhysicalTableName { get; init; } = string.Empty;
    public int RecordCount { get; init; }
    public DateTime CreatedOn { get; init; }
}

public class CreateTableCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public CreateTableCommandHandler(
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        ISchemaEngineService schemaEngine,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _schemaEngine = schemaEngine;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task<CreateTableResult> HandleAsync(CreateTableCommand command, CancellationToken ct = default)
    {
        var validator = new CreateTableCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);

        if (await _tableRepo.NameExistsInAppAsync(app.Id, command.Name, ct))
            throw new DuplicateException("Table", "name", command.Name);

        var table = new AppTable
        {
            TenantId = _queryContext.TenantId,
            AppId = app.Id,
            Name = command.Name,
            SingularLabel = command.SingularLabel,
            PluralLabel = command.PluralLabel,
            Description = command.Description,
            Icon = command.Icon,
            CreatedBy = _queryContext.UserId,
        };

        var (id, publicId) = await _tableRepo.CreateAsync(table, ct);
        table.Id = id;
        table.PublicId = publicId;

        var physicalName = PhysicalNaming.TableName(id);
        await _tableRepo.UpdatePhysicalNameAsync(id, physicalName, ct);
        table.PhysicalTableName = physicalName;

        await _schemaEngine.CreateTableAsync(table, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppTable, publicId.ToString(), appId: app.Id, ct: ct);

        return new CreateTableResult
        {
            PublicId = publicId,
            Name = table.Name,
            SingularLabel = table.SingularLabel,
            PluralLabel = table.PluralLabel,
            Description = table.Description,
            Icon = table.Icon,
            PhysicalTableName = physicalName,
            RecordCount = 0,
            CreatedOn = DateTime.UtcNow,
        };
    }
}
