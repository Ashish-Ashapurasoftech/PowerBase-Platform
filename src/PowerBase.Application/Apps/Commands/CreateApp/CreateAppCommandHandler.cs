using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
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
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly IFormRepository _formRepo;

    public CreateAppCommandHandler(
        IAppRepository appRepo,
        IAppRoleRepository appRoleRepo,
        IAppUserRepository appUserRepo,
        IUnitOfWork uow,
        IQueryContext queryContext,
        IAppTableRepository tableRepo,
        ISchemaEngineService schemaEngine,
        IAuditRepository auditRepo,
        IAppFieldRepository fieldRepo,
        IReportRepository reportRepo,
        IFieldTypeRepository fieldTypeRepo,
        IFormRepository formRepo)
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

            await _appRoleRepo.SetPermissionsAsync(adminRoleId, new[]
            {
                PermissionCodes.AppsUpdate, PermissionCodes.AppsDelete,
                PermissionCodes.TablesCreate, PermissionCodes.TablesRead, PermissionCodes.TablesUpdate, PermissionCodes.TablesDelete,
                PermissionCodes.FieldsCreate, PermissionCodes.FieldsRead, PermissionCodes.FieldsUpdate, PermissionCodes.FieldsDelete,
                PermissionCodes.RecordsCreate, PermissionCodes.RecordsRead, PermissionCodes.RecordsUpdate, PermissionCodes.RecordsDelete,
                PermissionCodes.ReportsCreate, PermissionCodes.ReportsRead, PermissionCodes.ReportsUpdate, PermissionCodes.ReportsDelete, PermissionCodes.ReportsRun,
                PermissionCodes.FormsCreate, PermissionCodes.FormsRead, PermissionCodes.FormsUpdate, PermissionCodes.FormsDelete, PermissionCodes.FormsRulesManage,
                PermissionCodes.UsersInvite, PermissionCodes.UsersManage, PermissionCodes.RolesManage
            }, _uow.Transaction, ct);

            var (participantRoleId, _) = await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                TenantId = _queryContext.TenantId,
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
                PermissionCodes.FormsRead
            }, _uow.Transaction, ct);

            var (viewerRoleId, _) = await _appRoleRepo.CreateAsync(new AppRole
            {
                AppId = appId,
                TenantId = _queryContext.TenantId,
                Name = "Viewer",
                IsSystem = true,
                IsDefault = true,
            }, _uow.Transaction, ct);

            await _appRoleRepo.SetPermissionsAsync(viewerRoleId, new[]
            {
                PermissionCodes.TablesRead, PermissionCodes.FieldsRead,
                PermissionCodes.RecordsRead,
                PermissionCodes.ReportsRead, PermissionCodes.ReportsRun,
                PermissionCodes.FormsRead
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

            // Seed system fields
            var userTypeId = await _fieldTypeRepo.GetIdByCodeAsync("User", ct);
            const int numberTypeId = 2;   // Number
            const int dateTimeTypeId = 5; // DateTime

            (string Name, int TypeId, string PhysCol, bool Sortable, bool Filterable, int Order)[] systemFieldDefs =
            [
                ("Record ID#",       numberTypeId,   "Id",         true,  false, 1),
                ("Date Created",     dateTimeTypeId, "CreatedOn",  true,  true,  2),
                ("Date Modified",    dateTimeTypeId, "ModifiedOn", true,  true,  3),
                ("Record Owner",     userTypeId,     "CreatedBy",  false, false, 4),
                ("Last Modified By", userTypeId,     "ModifiedBy", false, false, 5),
            ];

            var seededIds = new Dictionary<string, long>();
            foreach (var (name, typeId, physCol, sortable, filterable, order) in systemFieldDefs)
            {
                var f = new AppField
                {
                    TenantId = table.TenantId,
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
                };
                var (fieldId, _) = await _fieldRepo.CreateAsync(f, ct);
                seededIds[name] = fieldId;
            }

            // Seed default reports
            var dateModifiedFieldId = seededIds["Date Modified"];

            await _reportRepo.CreateAsync(new Report
            {
                TenantId = table.TenantId,
                AppTableId = table.Id,
                OwnerId = _queryContext.UserId,
                Name = "List All",
                ReportType = "Table",
                Visibility = "Shared",
                Definition = JsonSerializer.Serialize(new ReportDefinition()),
                IsDefault = true,
                DisplayOrder = 1,
            }, ct);

            await _reportRepo.CreateAsync(new Report
            {
                TenantId = table.TenantId,
                AppTableId = table.Id,
                OwnerId = _queryContext.UserId,
                Name = "List Changes",
                ReportType = "Table",
                Visibility = "Shared",
                Definition = JsonSerializer.Serialize(new ReportDefinition
                {
                    SortFields = [new SortSpec { FieldId = dateModifiedFieldId, Desc = true }],
                }),
                IsDefault = false,
                DisplayOrder = 2,
            }, ct);

            // Auto-create "Main Form" with all seeded system fields in a default section
            var mainForm = new Form
            {
                TenantId          = table.TenantId,
                AppTableId        = table.Id,
                Name              = "Main Form",
                IsDefault         = true,
                AutoAddNewFields  = true,
                ShowBuiltInFields = false,
                SaveOptions       = "SaveKeepWorking,SaveNew,SaveNext,SaveView",
                DisplayOrder      = 1,
                CreatedBy         = _queryContext.UserId,
            };
            var (formId, _) = await _formRepo.CreateAsync(mainForm, ct);

            var defaultSection = new FormSection
            {
                TenantId    = table.TenantId,
                FormId      = formId,
                Name        = "Section 1",
                ColumnCount = 2,
                DisplayOrder = 1,
                Elements    = seededIds.Select((kvp, i) => new FormElement
                {
                    TenantId     = table.TenantId,
                    AppFieldId   = kvp.Value,
                    LabelMode    = "Default",
                    ShowOnAdd    = true,
                    ShowOnEdit   = true,
                    ShowOnView   = true,
                    WidthMode    = "Auto",
                    IsReadOnly   = kvp.Key is "Record ID#" or "Date Created" or "Date Modified"
                                       or "Record Owner" or "Last Modified By",
                    IsRequired   = false,
                    DisplayOrder = i + 1,
                }).ToList(),
            };
            await _formRepo.SaveLayoutAsync(formId, table.TenantId, [defaultSection], ct);

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
