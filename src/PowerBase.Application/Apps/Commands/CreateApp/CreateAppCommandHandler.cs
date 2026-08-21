using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.CreateApp;

using PowerBase.Application.Fields.Commands.BulkCreateFields;
using PowerBase.Domain.ValueObjects;
using System.Text.Json;

public class CreateAppResult
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? Color { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedOn { get; init; }
    public bool IsEncrypted { get; init; }
    public string? OwnerName { get; init; }
}

public class CreateAppCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly ITenantUnitOfWork _uow;
    private readonly IQueryContext _queryContext;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IUserRepository _userRepo;
    private readonly IAppSeeder _appSeeder;
    private readonly BulkCreateFieldsCommandHandler _bulkCreateHandler;

    public CreateAppCommandHandler(
        IAppRepository appRepo,
        IAppRoleRepository appRoleRepo,
        IAppUserRepository appUserRepo,
        ITenantUnitOfWork uow,
        IQueryContext queryContext,
        IAppTableRepository tableRepo,
        IAuditRepository auditRepo,
        IUserRepository userRepo,
        IAppSeeder appSeeder,
        BulkCreateFieldsCommandHandler bulkCreateHandler)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _uow = uow;
        _queryContext = queryContext;
        _tableRepo = tableRepo;
        _auditRepo = auditRepo;
        _userRepo = userRepo;
        _appSeeder = appSeeder;
        _bulkCreateHandler = bulkCreateHandler;
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

        var owner = await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
        var now = DateTime.UtcNow;
        var app = new App
        {
            OwnerId = _queryContext.UserId,
            OwnerName = owner.Name,
            Name = command.Name,
            Description = command.Description,
            Icon = command.Icon,
            Color = command.Color,
            Status = "Active",
            Formatting = JsonSerializer.Serialize(new AppFormattingSettings()),
            SecurityOptions = JsonSerializer.Serialize(new AppSecurityOptionsSettings()),
            CreatedOn = now,
            CreatedBy = _queryContext.UserId,
            IsEncrypted = command.IsEncrypted
        };

        await _uow.BeginAsync(ct);
        try
        {
            var (publicId, appId) = await _appRepo.CreateAsync(app, _uow.Transaction, ct);

            var (adminRoleId, _) = await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                Name = "Administrator",
                IsSystem = true,
                IsDefault = false,
                Rank = 1,
                ManageableRolesType = "Below",
            }, _uow.Transaction, ct);

            await _appRoleRepo.SetPermissionsAsync(adminRoleId, new[]
            {
                PermissionCodes.AppsUpdate, PermissionCodes.AppsDelete,
                PermissionCodes.TablesCreate, PermissionCodes.TablesRead, PermissionCodes.TablesUpdate, PermissionCodes.TablesDelete,
                PermissionCodes.FieldsCreate, PermissionCodes.FieldsRead, PermissionCodes.FieldsUpdate, PermissionCodes.FieldsDelete,
                PermissionCodes.RecordsCreate, PermissionCodes.RecordsRead, PermissionCodes.RecordsUpdate, PermissionCodes.RecordsDelete,
                PermissionCodes.ReportsCreate, PermissionCodes.ReportsRead, PermissionCodes.ReportsUpdate, PermissionCodes.ReportsDelete, PermissionCodes.ReportsRun,
                PermissionCodes.FormsCreate, PermissionCodes.FormsRead, PermissionCodes.FormsUpdate, PermissionCodes.FormsDelete, PermissionCodes.FormsRulesManage,
                PermissionCodes.PagesCreate, PermissionCodes.PagesRead, PermissionCodes.PagesUpdate, PermissionCodes.PagesDelete, PermissionCodes.PagesPublish, PermissionCodes.PagesCode,
                PermissionCodes.UsersInvite, PermissionCodes.UsersManage, PermissionCodes.RolesManage,
                PermissionCodes.AuditLogsRead, PermissionCodes.AuditLogsReadOfStream,
                PermissionCodes.PowerFlowsCreate, PermissionCodes.PowerFlowsRead, PermissionCodes.PowerFlowsUpdate, PermissionCodes.PowerFlowsDelete, PermissionCodes.PowerFlowsCopy,
            }, _uow.Transaction, ct);

            var (participantRoleId, _) = await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                Name = "Participant",
                IsSystem = true,
                IsDefault = false,
                Rank = 2,
            }, _uow.Transaction, ct);

            await _appRoleRepo.SetPermissionsAsync(participantRoleId, new[]
            {
                PermissionCodes.TablesCreate, PermissionCodes.TablesRead,
                PermissionCodes.FieldsCreate, PermissionCodes.FieldsRead,
                PermissionCodes.RecordsCreate, PermissionCodes.RecordsRead,
                PermissionCodes.ReportsCreate, PermissionCodes.ReportsRead, PermissionCodes.ReportsRun,
                PermissionCodes.FormsRead,
                PermissionCodes.PagesRead,
                PermissionCodes.PowerFlowsRead
            }, _uow.Transaction, ct);

            var (viewerRoleId, _) = await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                Name = "Viewer",
                IsSystem = true,
                IsDefault = true,
                Rank = 3,
            }, _uow.Transaction, ct);

            await _appRoleRepo.SetPermissionsAsync(viewerRoleId, new[]
            {
                PermissionCodes.TablesRead, PermissionCodes.FieldsRead,
                PermissionCodes.RecordsRead,
                PermissionCodes.ReportsRead, PermissionCodes.ReportsRun,
                PermissionCodes.FormsRead,
                PermissionCodes.PagesRead,
                PermissionCodes.PowerFlowsRead
            }, _uow.Transaction, ct);

            await _appRepo.SetDefaultRoleAsync(appId, viewerRoleId, _uow.Transaction, ct);

            await _appUserRepo.CreateAsync(new AppUser
            {
                AppId        = appId,
                UserId       = _queryContext.UserId,
                UserPublicId = owner.PublicId,
                UserName     = owner.Name,
                UserEmail    = owner.Email,
                AppRoleId    = adminRoleId,
                Status       = "Active",
            }, _uow.Transaction, ct);

            await _uow.CommitAsync(ct);

            if (command.Tables != null && command.Tables.Any())
            {
                foreach (var spec in command.Tables)
                {
                    await SeedTableAsync(appId, _queryContext.UserId, spec, ct);
                }
            }

            await _auditRepo.LogActivityAsync(
                AuditActions.Created, AuditEntityTypes.App, publicId.ToString(), $"Application added: {command.Name}", appId: appId, ct: ct);

            return new CreateAppResult
            {
                PublicId = publicId,
                Name = app.Name,
                Description = app.Description,
                Icon = app.Icon,
                Color = app.Color,
                Status = app.Status,
                CreatedOn = now,
                IsEncrypted = app.IsEncrypted,
                OwnerName = owner.Name
            };
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }
    }

    private async Task SeedTableAsync(long appId, long userId, TableSpec spec, CancellationToken ct)
    {
        if (await _tableRepo.NameExistsInAppAsync(appId, spec.Name, ct))
            throw new DuplicateException("Table", "name", spec.Name);

        var table = new AppTable
        {
            AppId = appId,
            Name = spec.Name,
            SingularLabel = spec.SingularLabel,
            PluralLabel = spec.PluralLabel,
            Description = spec.Description,
            Icon = spec.Icon,
            CreatedBy = userId,
        };

        table = await _appSeeder.CreateTableWithDefaultsAsync(table, userId, spec.SeedDefaultViews, ct);

        // Process Custom Fields
        if (spec.Fields != null && spec.Fields.Any())
        {
            var items = spec.Fields.Select(f => new BulkCreateFieldItem(f.TypeCode, f.Label, Settings: f.Settings, IsEncrypted: f.IsEncrypted, Name: f.Name)).ToList();
            await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(table.PublicId, items), ct);
        }
    }
}
