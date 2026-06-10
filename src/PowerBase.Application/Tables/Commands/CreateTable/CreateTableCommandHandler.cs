using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
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
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly IFormRepository _formRepo;
    private readonly IAppRolePermissionRepository _permRepo;

    public CreateTableCommandHandler(
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        ISchemaEngineService schemaEngine,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IAppFieldRepository fieldRepo,
        IReportRepository reportRepo,
        IFieldTypeRepository fieldTypeRepo,
        IFormRepository formRepo,
        IAppRolePermissionRepository permRepo)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _schemaEngine = schemaEngine;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _fieldRepo = fieldRepo;
        _reportRepo = reportRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _formRepo = formRepo;
        _permRepo = permRepo;
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

        // Seed system fields (Quickbase FID equivalents)
        var userTypeId = await _fieldTypeRepo.GetIdByCodeAsync("User", ct);
        var numberTypeId = await _fieldTypeRepo.GetIdByCodeAsync("Number", ct);
        var dateTimeTypeId = await _fieldTypeRepo.GetIdByCodeAsync("DateTime", ct);

        // Fid values match Quickbase conventions: Record ID# = 3, system fields occupy 1–5.
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

        // Seed default reports — sort/filter use FIDs, not internal Ids
        var dateModifiedFid = seededFids["Date Modified"];  // FID 2

        await _reportRepo.CreateAsync(new Report
        {
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
            AppTableId = table.Id,
            OwnerId = _queryContext.UserId,
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

        // Auto-create "Main Form" with all seeded system fields in a default section
        var mainForm = new Form
        {
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

        var defaultBlock = new FormSectionBlock
        {
            Width        = 1,
            DisplayOrder = 1,
            Elements     = seededFids.Select((kvp, i) => new FormElement
            {
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
        var defaultSection = new FormSection
        {
            FormId       = formId,
            Name         = "Section 1",
            DisplayOrder = 1,
            Blocks       = [defaultBlock],
        };
        await _formRepo.SaveLayoutAsync(formId, [defaultSection], ct);

        // Seed default table-permission rows for every existing role in the app
        await _permRepo.SeedDefaultsForTableAsync(id, app.Id, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppTable, publicId.ToString(), $"Table added: {table.Name}", appId: app.Id, ct: ct);

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
