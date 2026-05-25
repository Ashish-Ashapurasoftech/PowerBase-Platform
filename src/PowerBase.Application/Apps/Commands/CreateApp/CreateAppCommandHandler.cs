using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.CreateApp;

public class CreateAppResult
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? Color { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedOn { get; init; }
}

public class CreateAppCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IUnitOfWork _uow;
    private readonly IQueryContext _queryContext;
    private readonly IAppTableRepository _tableRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IAuditRepository _auditRepo;

    public CreateAppCommandHandler(
        IAppRepository appRepo,
        IAppRoleRepository appRoleRepo,
        IAppUserRepository appUserRepo,
        IUnitOfWork uow,
        IQueryContext queryContext,
        IAppTableRepository tableRepo,
        ISchemaEngineService schemaEngine,
        IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _uow = uow;
        _queryContext = queryContext;
        _tableRepo = tableRepo;
        _schemaEngine = schemaEngine;
        _auditRepo = auditRepo;
    }

    public async Task<CreateAppResult> HandleAsync(CreateAppCommand command, CancellationToken ct = default)
    {
        var validator = new CreateAppCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        if (await _appRepo.NameExistsAsync(command.Name, ct))
            throw new DuplicateException("App", "name", command.Name);

        var now = DateTime.UtcNow;
        var app = new App
        {
            TenantId = _queryContext.TenantId,
            OwnerId = _queryContext.UserId,
            Name = command.Name,
            Description = command.Description,
            Icon = command.Icon,
            Color = command.Color,
            Status = "Active",
            CreatedOn = now,
            CreatedBy = _queryContext.UserId,
        };

        await _uow.BeginAsync(ct);
        try
        {
            var (publicId, appId) = await _appRepo.CreateAsync(app, _uow.Transaction, ct);

            var (adminRoleId, _) = await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                TenantId = _queryContext.TenantId,
                Name = "Administrator",
                IsSystem = true,
                IsDefault = false,
            }, _uow.Transaction, ct);

            await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                TenantId = _queryContext.TenantId,
                Name = "Participant",
                IsSystem = true,
                IsDefault = false,
            }, _uow.Transaction, ct);

            var (viewerRoleId, _) = await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                TenantId = _queryContext.TenantId,
                Name = "Viewer",
                IsSystem = true,
                IsDefault = true,
            }, _uow.Transaction, ct);

            await _appRepo.SetDefaultRoleAsync(appId, viewerRoleId, _uow.Transaction, ct);

            await _appUserRepo.CreateAsync(new AppUser
            {
                AppId = appId,
                TenantId = _queryContext.TenantId,
                UserId = _queryContext.UserId,
                AppRoleId = adminRoleId,
                Status = "Active",
            }, _uow.Transaction, ct);

            await _uow.CommitAsync(ct);

            // Create the first table associated with the new app
            if (await _tableRepo.NameExistsInAppAsync(appId, command.TableName, ct))
                throw new DuplicateException("Table", "name", command.TableName);

            var table = new AppTable
            {
                TenantId = _queryContext.TenantId,
                AppId = appId,
                Name = command.TableName,
                SingularLabel = command.TableSingularLabel,
                PluralLabel = command.TablePluralLabel,
                Description = command.TableDescription,
                Icon = command.TableIcon,
                CreatedBy = _queryContext.UserId,
            };

            var (tableId, tablePublicId) = await _tableRepo.CreateAsync(table, ct);
            table.Id = tableId;
            table.PublicId = tablePublicId;

            var physicalName = PhysicalNaming.TableName(tableId);
            await _tableRepo.UpdatePhysicalNameAsync(tableId, physicalName, ct);
            table.PhysicalTableName = physicalName;

            await _schemaEngine.CreateTableAsync(table, ct);

            await _auditRepo.LogActivityAsync(
                AuditActions.Created, AuditEntityTypes.App, publicId.ToString(), appId: appId, ct: ct);

            return new CreateAppResult
            {
                PublicId = publicId,
                Name = app.Name,
                Description = app.Description,
                Icon = app.Icon,
                Color = app.Color,
                Status = app.Status,
                CreatedOn = now,
            };
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }
    }
}
