using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Services;

public class AppSeeder : IAppSeeder
{
    private readonly IAppTableRepository _tableRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly IFormRepository _formRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IFieldNameResolver _fieldNameResolver;

    public AppSeeder(
        IAppTableRepository tableRepo,
        ISchemaEngineService schemaEngine,
        IAppFieldRepository fieldRepo,
        IReportRepository reportRepo,
        IFieldTypeRepository fieldTypeRepo,
        IFormRepository formRepo,
        IAppRolePermissionRepository permRepo,
        IFieldNameResolver fieldNameResolver)
    {
        _tableRepo = tableRepo;
        _schemaEngine = schemaEngine;
        _fieldRepo = fieldRepo;
        _reportRepo = reportRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _formRepo = formRepo;
        _permRepo = permRepo;
        _fieldNameResolver = fieldNameResolver;
    }

    public async Task<AppTable> CreateTableWithDefaultsAsync(AppTable table, long userId, bool seedDefaultViews = true, CancellationToken ct = default)
    {
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
        // "Label" here is the human-readable display value; Name is auto-generated from it
        // (S_<slug>, since these are system fields) and never shown to users.
        (string Label, int TypeId, string PhysCol, bool Sortable, bool Filterable, int Order, int Fid)[] systemFieldDefs =
        [
            ("Record ID#",       numberTypeId,   "Id",         true,  false, 1, 3),
            ("Date Created",     dateTimeTypeId, "CreatedOn",  true,  true,  2, 1),
            ("Date Modified",    dateTimeTypeId, "ModifiedOn", true,  true,  3, 2),
            ("Record Owner",     userTypeId,     "CreatedBy",  false, false, 4, 4),
            ("Last Modified By", userTypeId,     "ModifiedBy", false, false, 5, 5),
        ];

        var seededFids = new Dictionary<string, int>();
        foreach (var (label, typeId, physCol, sortable, filterable, order, fid) in systemFieldDefs)
        {
            var f = new AppField
            {
                AppTableId = table.Id,
                FieldTypeId = typeId,
                Name = await _fieldNameResolver.GenerateUniqueNameAsync(table.Id, label, isSystem: true, ct),
                Label = label,
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
            seededFids[label] = fid;
        }

        // Default reports and Main Form are skipped when the caller is supplying its own (import) —
        // see IAppSeeder.CreateTableWithDefaultsAsync. Everything above and below this block is
        // structural and always seeded.
        if (!seedDefaultViews)
        {
            await _permRepo.SeedDefaultsForTableAsync(table.Id, table.AppId, ct);
            return table;
        }

        // Seed default reports — sort/filter use FIDs, not internal Ids
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
            FormId       = formId,
            Name         = "Section 1",
            DisplayOrder = 1,
            Blocks       = [defaultBlock],
        };
        await _formRepo.SaveLayoutAsync(formId, [defaultSection], ct: ct);

        // Seed default table-permission rows for every existing role in the app
        await _permRepo.SeedDefaultsForTableAsync(table.Id, table.AppId, ct);

        return table;
    }
}
