using System.Text.Json;
using PowerBase.Application.Apps.Commands.CreateApp;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.BulkCreateFields;
using PowerBase.Application.Import.FormulaTranslation;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Commands.ImportAppFromPbl;

/// <summary>
/// Orchestrates a Create-New-App import from a validated PBL document.
///
/// Field creation happens in two passes because a formula's expression can only be validated
/// once the fields it references exist and have Fids assigned:
///   Pass 1 — scalar fields, via the existing <see cref="CreateAppCommandHandler"/> (which
///            creates the app, tables, and non-formula fields in one go, as it already did).
///   Pass 2 — formula fields, translated/validated against each table's now-real schema via
///            <see cref="FormulaTranslator"/>, then created via <see cref="BulkCreateFieldsCommandHandler"/>.
///            Reports are created last (via <see cref="CreateReportCommandHandler"/>), once every
///            field a report might reference has a Fid.
///
/// Fields whose type isn't supported by this import phase, or whose formula fails to compile,
/// are skipped and reported — never silently dropped.
/// </summary>
public class ImportAppFromPblCommandHandler
{
    private readonly PblValidator _validator;
    private readonly CreateAppCommandHandler _createAppHandler;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly BulkCreateFieldsCommandHandler _bulkCreateHandler;
    private readonly FormulaTranslator _formulaTranslator;
    private readonly CreateReportCommandHandler _createReportHandler;

    public ImportAppFromPblCommandHandler(
        PblValidator validator,
        CreateAppCommandHandler createAppHandler,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        BulkCreateFieldsCommandHandler bulkCreateHandler,
        FormulaTranslator formulaTranslator,
        CreateReportCommandHandler createReportHandler)
    {
        _validator = validator;
        _createAppHandler = createAppHandler;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _bulkCreateHandler = bulkCreateHandler;
        _formulaTranslator = formulaTranslator;
        _createReportHandler = createReportHandler;
    }

    public async Task<ImportReport> HandleAsync(ImportAppFromPblCommand command, CancellationToken ct = default)
    {
        if (command.Mode != ImportMode.CreateNewApp)
            throw new BadRequestException("UNSUPPORTED_IMPORT_MODE", $"Import mode '{command.Mode}' is not supported yet.");

        PblDocument document;
        try
        {
            document = PblSerializer.Deserialize(command.PblJson);
        }
        catch (JsonException ex)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Pbl"] = [$"Could not parse PBL document: {ex.Message}"],
            });
        }

        var validation = _validator.Validate(document);
        if (!validation.IsValid)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Pbl"] = validation.Errors.Select(e => e.Message).ToArray(),
            });

        var skipped = new List<ImportSkippedItem>();
        var tableSpecs = new List<TableSpec>();

        // Pass 1 input: scalar fields only. Formula fields are deferred to pass 2.
        foreach (var table in document.Tables ?? [])
        {
            var fieldSpecs = new List<AppFieldSpec>();

            foreach (var field in table.Fields ?? [])
            {
                if (IsFormulaField(field))
                    continue;

                if (!PblValidator.SupportedFieldTypeCodes.Contains(field.TypeCode))
                {
                    skipped.Add(new ImportSkippedItem
                    {
                        LogicalRef = field.LogicalRef,
                        Name = field.Name,
                        Reason = $"Unsupported field type '{field.TypeCode}' for this import phase.",
                    });
                    continue;
                }

                fieldSpecs.Add(new AppFieldSpec(field.Name, field.TypeCode, field.Settings));
            }

            tableSpecs.Add(new TableSpec(
                table.Name,
                table.SingularLabel,
                table.PluralLabel,
                table.Icon,
                table.Description,
                Config: null,
                Fields: fieldSpecs));
        }

        var createCommand = new CreateAppCommand(
            document.App.Name,
            document.App.Description,
            document.App.Icon,
            document.App.Color,
            tableSpecs);

        var createResult = await _createAppHandler.HandleAsync(createCommand, ct);
        var fieldsCreated = tableSpecs.Sum(t => t.Fields?.Count ?? 0);

        var formulaTranslations = new List<FormulaTranslationReportItem>();
        var reportsCreated = 0;

        var hasFormulasOrReports = (document.Tables ?? []).Any(t =>
            (t.Fields ?? []).Any(IsFormulaField) || (t.Reports ?? []).Count > 0);

        if (hasFormulasOrReports)
        {
            var appId = await _appRepo.GetIdByPublicIdAsync(createResult.PublicId, ct);
            var createdTables = await _tableRepo.ListByAppAsync(appId, ct);
            var tableByName = createdTables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var table in document.Tables ?? [])
            {
                if (!tableByName.TryGetValue(table.Name, out var createdTable))
                    continue; // defensive: every PBL table was just created by CreateAppCommandHandler

                var formulaFields = (table.Fields ?? []).Where(IsFormulaField).ToList();
                if (formulaFields.Count > 0)
                {
                    var currentFields = await _fieldRepo.ListByTableAsync(createdTable.Id, ct);
                    var formulaItems = new List<BulkCreateFieldItem>();

                    foreach (var field in formulaFields)
                    {
                        var translation = _formulaTranslator.Translate(field.ResultType, field.FormulaExpression, currentFields);

                        formulaTranslations.Add(new FormulaTranslationReportItem
                        {
                            LogicalRef = field.LogicalRef,
                            Name = field.Name,
                            Status = translation.Status.ToString(),
                            Diagnostics = translation.Diagnostics.ToList(),
                        });

                        if (translation.Status == FormulaTranslationStatus.NeedsManualReview)
                        {
                            skipped.Add(new ImportSkippedItem
                            {
                                LogicalRef = field.LogicalRef,
                                Name = field.Name,
                                Reason = $"Formula needs manual review: {string.Join("; ", translation.Diagnostics)}",
                            });
                            continue;
                        }

                        formulaItems.Add(new BulkCreateFieldItem("Formula", field.Name, Settings: translation.SettingsJson));
                    }

                    if (formulaItems.Count > 0)
                    {
                        await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(createdTable.PublicId, formulaItems), ct);
                        fieldsCreated += formulaItems.Count;
                    }
                }

                var pblReports = table.Reports ?? [];
                if (pblReports.Count == 0)
                    continue;

                var allFields = await _fieldRepo.ListByTableAsync(createdTable.Id, ct);
                var fidByName = allFields
                    .Where(f => f.Fid.HasValue)
                    .ToDictionary(f => f.Name, f => (long)f.Fid!.Value, StringComparer.OrdinalIgnoreCase);

                foreach (var report in pblReports)
                {
                    var unresolved = report.Columns.Where(c => !fidByName.ContainsKey(c))
                        .Concat(report.SortFields.Select(s => s.FieldName).Where(n => !fidByName.ContainsKey(n)))
                        .ToList();

                    if (unresolved.Count > 0)
                    {
                        skipped.Add(new ImportSkippedItem
                        {
                            LogicalRef = report.LogicalRef,
                            Name = report.Name,
                            Reason = $"Report references field(s) that were not created: {string.Join(", ", unresolved.Distinct())}.",
                        });
                        continue;
                    }

                    var columns = report.Columns.Select(c => fidByName[c]).ToList();
                    var sortFields = report.SortFields.Select(s => new SortSpec { FieldId = fidByName[s.FieldName], Desc = s.Desc }).ToList();

                    await _createReportHandler.HandleAsync(new CreateReportCommand(
                        createdTable.PublicId,
                        report.Name,
                        Description: null,
                        Visibility: "Shared",
                        ReportType: "Table",
                        Columns: columns,
                        SortFields: sortFields,
                        FilterTree: null,
                        GroupByFieldId: null,
                        GroupByMode: "EqualValues",
                        HideTotals: false,
                        GroupDefaultCollapsed: false,
                        GroupByDescending: false,
                        Aggregations: [],
                        DynamicFilterType: "Default",
                        CustomDynamicFilterFields: [],
                        CustomDynamicFilterItems: null,
                        AllowQuickSearch: true,
                        VisibleToRoleIds: null), ct);

                    reportsCreated++;
                }
            }
        }

        return new ImportReport
        {
            AppPublicId = createResult.PublicId,
            AppName = createResult.Name,
            TablesCreated = tableSpecs.Count,
            FieldsCreated = fieldsCreated,
            ReportsCreated = reportsCreated,
            Skipped = skipped,
            FormulaTranslations = formulaTranslations,
        };
    }

    private static bool IsFormulaField(PblField field) =>
        string.Equals(field.TypeCode, PblValidator.FormulaTypeCode, StringComparison.OrdinalIgnoreCase);
}
