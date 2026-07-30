using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.CreateApp;

using PowerBase.Application.Fields.Commands.BulkCreateFields;

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
    private readonly ITenantUnitOfWork _uow;
    private readonly IQueryContext _queryContext;
    private readonly IAppTableRepository _tableRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IAuditRepository _auditRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly IFormRepository _formRepo;
    private readonly IUserRepository _userRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly BulkCreateFieldsCommandHandler _bulkCreateHandler;

    public CreateAppCommandHandler(
        IAppRepository appRepo,
        IAppRoleRepository appRoleRepo,
        IAppUserRepository appUserRepo,
        ITenantUnitOfWork uow,
        IQueryContext queryContext,
        IAppTableRepository tableRepo,
        ISchemaEngineService schemaEngine,
        IAuditRepository auditRepo,
        IAppFieldRepository fieldRepo,
        IReportRepository reportRepo,
        IFieldTypeRepository fieldTypeRepo,
        IFormRepository formRepo,
        IUserRepository userRepo,
        IAppRolePermissionRepository permRepo,
        BulkCreateFieldsCommandHandler bulkCreateHandler)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _uow = uow;
        _queryContext = queryContext;
        _tableRepo = tableRepo;
        _schemaEngine = schemaEngine;
        _auditRepo = auditRepo;
        _fieldRepo = fieldRepo;
        _reportRepo = reportRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _formRepo = formRepo;
        _userRepo = userRepo;
        _permRepo = permRepo;
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
                Name = "Administrator",
                IsSystem = true,
                IsDefault = false,
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
                PermissionCodes.AuditLogsRead,PermissionCodes.AuditLogsReadOfStream,
            }, _uow.Transaction, ct);

            var (participantRoleId, _) = await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                Name = "Participant",
                IsSystem = true,
                IsDefault = false,
            }, _uow.Transaction, ct);

            await _appRoleRepo.SetPermissionsAsync(participantRoleId, new[]
            {
                PermissionCodes.TablesCreate, PermissionCodes.TablesRead,
                PermissionCodes.FieldsCreate, PermissionCodes.FieldsRead,
                PermissionCodes.RecordsCreate, PermissionCodes.RecordsRead,
                PermissionCodes.ReportsCreate, PermissionCodes.ReportsRead, PermissionCodes.ReportsRun,
                PermissionCodes.FormsRead,
                PermissionCodes.PagesRead,
            }, _uow.Transaction, ct);

            var (viewerRoleId, _) = await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                Name = "Viewer",
                IsSystem = true,
                IsDefault = true,
            }, _uow.Transaction, ct);

            await _appRoleRepo.SetPermissionsAsync(viewerRoleId, new[]
            {
                PermissionCodes.TablesRead, PermissionCodes.FieldsRead,
                PermissionCodes.RecordsRead,
                PermissionCodes.ReportsRead, PermissionCodes.ReportsRun,
                PermissionCodes.FormsRead,
                PermissionCodes.PagesRead,
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

        var (tableId, tablePublicId) = await _tableRepo.CreateAsync(table, ct);
        table.Id = tableId;
        table.PublicId = tablePublicId;

        var physicalName = PhysicalNaming.TableName(tableId);
        await _tableRepo.UpdatePhysicalNameAsync(tableId, physicalName, ct);
        table.PhysicalTableName = physicalName;

        await _schemaEngine.CreateTableAsync(table, ct);

        // Seed system fields
        var userTypeId     = await _fieldTypeRepo.GetIdByCodeAsync("User", ct);
        var numberTypeId   = await _fieldTypeRepo.GetIdByCodeAsync("Number", ct);
        var dateTimeTypeId = await _fieldTypeRepo.GetIdByCodeAsync("DateTime", ct);

        (string Name, int TypeId, string PhysCol, bool Sortable, bool Filterable, int Order, int Fid)[] systemFieldDefs =
        [
            ("Record ID#",       numberTypeId,   "Id",         true,  false, 1, 3),
            ("Date Created",     dateTimeTypeId, "CreatedOn",  true,  true,  2, 1),
            ("Date Modified",    dateTimeTypeId, "ModifiedOn", true,  true,  3, 2),
            ("Record Owner",     userTypeId,     "CreatedBy",  false, false, 4, 4),
            ("Last Modified By", userTypeId,     "ModifiedBy", false, false, 5, 5),
        ];

        var seededFids = new Dictionary<string, int>();
        foreach (var (name, typeId, physCol, sortable, filterable, order, fid) in systemFieldDefs)
        {
            var f = new AppField
            {
                AppTableId = table.Id,
                FieldTypeId = typeId,
                Name = name,
                PhysicalColumnName = physCol,
                IsSystem = true,
                IsReportable = true,
                IsSortable = sortable,
                IsFilterable = filterable,
                IsSearchable = false,
                DisplayOrder = order,
                Fid = fid,
            };
            await _fieldRepo.CreateAsync(f, ct);
            seededFids[name] = fid;
        }

        // Seed default reports
        var dateModifiedFid = seededFids["Date Modified"];

        await _reportRepo.CreateAsync(new Report
        {
            AppTableId = table.Id,
            OwnerId = userId,
            Name = "List All",
            ReportType = "Table",
            Visibility = "Shared",
            Definition = JsonSerializer.Serialize(new ReportDefinition()),
            IsDefault = true,
            DisplayOrder = 1,
        }, ct);

        await _reportRepo.CreateAsync(new Report
        {
            AppTableId = table.Id,
            OwnerId = userId,
            Name = "List Changes",
            ReportType = "Table",
            Visibility = "Shared",
            Definition = JsonSerializer.Serialize(new ReportDefinition
            {
                SortFields = [new SortSpec { FieldId = dateModifiedFid, Desc = true }],
            }),
            IsDefault = false,
            DisplayOrder = 2,
        }, ct);

        // Auto-create "Main Form"
        var mainForm = new Form
        {
            AppTableId        = table.Id,
            Name              = "Main Form",
            IsDefault         = true,
            AutoAddNewFields  = true,
            ShowBuiltInFields = false,
            SaveOptions       = "SaveKeepWorking,SaveNew,SaveNext,SaveView",
            DisplayOrder      = 1,
            CreatedBy         = userId,
        };
        var (formId, _) = await _formRepo.CreateAsync(mainForm, ct);

        var defaultBlock = new FormSectionBlock
        {
            Width        = 1,
            DisplayOrder = 1,
            Elements     = [],
        };

        var defaultSection = new FormSection
        {
            FormId      = formId,
            Name        = "Section 1",
            ColumnCount = 2,
            DisplayOrder = 1,
            Blocks      = [defaultBlock],
        };
        await _formRepo.SaveLayoutAsync(formId, [defaultSection], ct);

        // Process Custom Fields
        if (spec.Fields != null && spec.Fields.Any())
        {
            var items = spec.Fields.Select(f => new BulkCreateFieldItem(f.TypeCode, f.Name)).ToList();
            await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(tablePublicId, items), ct);
        }

        await _permRepo.SeedDefaultsForTableAsync(tableId, appId, ct);
    }
}
