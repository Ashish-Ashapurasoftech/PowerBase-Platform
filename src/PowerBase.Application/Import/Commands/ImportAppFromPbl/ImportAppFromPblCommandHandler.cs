using PowerBase.Application.Common.Services;
using PowerBase.Application.Import.Jobs;
using PowerBase.Application.Formulas;
using System.Text.Json;
using PowerBase.Application.Apps.Commands.CreateApp;
using PowerBase.Application.Apps.Commands.CreateAppRole;
using PowerBase.Application.Apps.Commands.DeleteApp;
using PowerBase.Application.Apps.Commands.UpdateTablePermissions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.BulkCreateFields;
using PowerBase.Application.Forms.Commands.CreateForm;
using PowerBase.Application.Forms.Commands.CreateFormRule;
using PowerBase.Application.Forms.Commands.SaveFormLayout;
using PowerBase.Application.Forms.Commands.SaveFormRule;
using PowerBase.Application.Forms.Queries.GetFormLayout;
using PowerBase.Application.Import.FormulaTranslation;
using PowerBase.Application.Import.Mapping;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Reporting;
using PowerBase.Application.Import.Services;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;
using YamlDotNet.Core;

namespace PowerBase.Application.Import.Commands.ImportAppFromPbl;

/// <summary>Counts from <see cref="ImportAppFromPblCommandHandler.ApplyDocumentAsync"/>.
/// <c>FieldsCreated</c> covers only the formula-field pass; scalar fields are created with the tables.</summary>
public sealed record PblApplyResult(
    int FieldsCreated, int ReportsCreated, int RelationshipsCreated, int FormsCreated,
    int FormRulesCreated, int RolesCreated, int VariablesCreated, int PagesCreated);

/// <summary>
/// Orchestrates a Create-New-App import from a validated PBL document.
///
/// Field creation happens in passes because later constructs can only be validated/created once
/// what they reference exists and has real Fids assigned:
///   Pass 1 — scalar fields, via the existing <see cref="CreateAppCommandHandler"/> (which
///            creates the app, tables, and non-formula fields in one go, as it already did).
///   Pass 2 — roles + table permissions, via <see cref="CreateAppRoleCommandHandler"/> then
///            <see cref="UpdateTablePermissionsCommandHandler"/>. Independent of everything
///            below — only needs Pass 1's tables.
///   Pass 3 — relationships, via <see cref="CreateRelationshipCommandHandler"/> (creates the
///            Reference field, every Lookup/Summary field, and an auto ReportLink, per
///            relationship — see that handler for the full sequence). Runs before
///            formulas/forms/reports since all three can reference a relationship-created field.
///   Pass 4 — formula fields, translated/validated against each table's now-real schema via
///            <see cref="FormulaTranslator"/>, then created via <see cref="BulkCreateFieldsCommandHandler"/>.
///            Must precede forms and reports: an element/column referencing a formula field can
///            only resolve once that field has a Fid.
///   Pass 5 — forms + layout, via <see cref="CreateFormCommandHandler"/> then
///            <see cref="SaveFormLayoutCommandHandler"/>, once every field a form element might
///            reference has a Fid.
///   Pass 6 — form rules, via <see cref="GetFormLayoutQueryHandler"/> (resolving the real
///            DbIds Pass 5's saved layout doesn't hand back) then
///            <see cref="CreateFormRuleCommandHandler"/>/<see cref="SaveFormRuleCommandHandler"/>.
///   Pass 7 — reports (via <see cref="CreateReportCommandHandler"/>), once every field a report
///            might reference — relationship-derived and formula alike — has a Fid.
///
/// Fields/relationships/forms/roles whose construct isn't supported by this import phase, or
/// whose formula fails to compile, are skipped and reported — never silently dropped.
/// </summary>
public class ImportAppFromPblCommandHandler
{
    /// <summary>Upper bound on lookup/summary/formula dependency rounds; each round must make progress, so it usually ends sooner.</summary>
    private const int MAX_DEPENDENCY_ROUNDS = 8;

    private readonly PblValidator _validator;
    private readonly CreateAppCommandHandler _createAppHandler;
    private readonly DeleteAppCommandHandler _deleteAppHandler;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFormRepository _formRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly BulkCreateFieldsCommandHandler _bulkCreateHandler;
    private readonly FormulaTranslator _formulaTranslator;
    private readonly CreateReportCommandHandler _createReportHandler;
    private readonly CreateRelationshipCommandHandler _createRelationshipHandler;
    private readonly CreateFormCommandHandler _createFormHandler;
    private readonly SaveFormLayoutCommandHandler _saveFormLayoutHandler;
    private readonly GetFormLayoutQueryHandler _getFormLayoutHandler;
    private readonly CreateFormRuleCommandHandler _createFormRuleHandler;
    private readonly SaveFormRuleCommandHandler _saveFormRuleHandler;
    private readonly CreateAppRoleCommandHandler _createAppRoleHandler;
    private readonly UpdateTablePermissionsCommandHandler _updateTablePermissionsHandler;
    private readonly PblExtrasImporter _extrasImporter;
    private readonly ImportSourceRefRecorder _sourceRefRecorder;
    private readonly PblDeferredResolver _deferredResolver;
    private readonly ImportFieldSanitizer _fieldSanitizer;
    private readonly PblSettingsApplier _settingsApplier;
    private readonly PblPermissionsApplier _permissionsApplier;
    private readonly PblConnectedTablesApplier? _connectedTablesApplier;
    private readonly PendingConnectedTableResolver? _pendingConnectedTableResolver;
    private readonly RelationshipFieldFactory? _reportLinkFactory;
    private readonly PowerBase.Application.FieldReferences.IFieldReferenceIndexer _refIndexer;
    private readonly PblPipelinesImporter? _pipelinesImporter;
    private readonly PipelineOnlyImportService? _pipelineOnlyImport;
    private readonly IAuditRepository? _auditRepo;
    private readonly ImportFieldNoteRecorder? _fieldNoteRecorder;

    public ImportAppFromPblCommandHandler(
        PblValidator validator,
        CreateAppCommandHandler createAppHandler,
        DeleteAppCommandHandler deleteAppHandler,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IFormRepository formRepo,
        IReportRepository reportRepo,
        IAppRoleRepository appRoleRepo,
        BulkCreateFieldsCommandHandler bulkCreateHandler,
        FormulaTranslator formulaTranslator,
        CreateReportCommandHandler createReportHandler,
        CreateRelationshipCommandHandler createRelationshipHandler,
        CreateFormCommandHandler createFormHandler,
        SaveFormLayoutCommandHandler saveFormLayoutHandler,
        GetFormLayoutQueryHandler getFormLayoutHandler,
        CreateFormRuleCommandHandler createFormRuleHandler,
        SaveFormRuleCommandHandler saveFormRuleHandler,
        CreateAppRoleCommandHandler createAppRoleHandler,
        UpdateTablePermissionsCommandHandler updateTablePermissionsHandler,
        PblExtrasImporter extrasImporter,
        ImportSourceRefRecorder sourceRefRecorder,
        PblDeferredResolver deferredResolver,
        ImportFieldSanitizer fieldSanitizer,
        PblSettingsApplier settingsApplier,
        PblPermissionsApplier permissionsApplier,
        PblConnectedTablesApplier? connectedTablesApplier = null,
        RelationshipFieldFactory? reportLinkFactory = null,
        PowerBase.Application.FieldReferences.IFieldReferenceIndexer? refIndexer = null,
        PblPipelinesImporter? pipelinesImporter = null,
        PipelineOnlyImportService? pipelineOnlyImport = null,
        PendingConnectedTableResolver? pendingConnectedTableResolver = null,
        IAuditRepository? auditRepo = null,
        ImportFieldNoteRecorder? fieldNoteRecorder = null)
    {
        _pipelineOnlyImport = pipelineOnlyImport;
        _fieldNoteRecorder = fieldNoteRecorder;
        _auditRepo = auditRepo;
        _connectedTablesApplier = connectedTablesApplier;
        _pendingConnectedTableResolver = pendingConnectedTableResolver;
        _reportLinkFactory = reportLinkFactory;
        _refIndexer = refIndexer ?? PowerBase.Application.FieldReferences.NullFieldReferenceIndexer.Instance;
        _pipelinesImporter = pipelinesImporter;
        _validator = validator;
        _createAppHandler = createAppHandler;
        _deleteAppHandler = deleteAppHandler;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _formRepo = formRepo;
        _reportRepo = reportRepo;
        _appRoleRepo = appRoleRepo;
        _bulkCreateHandler = bulkCreateHandler;
        _formulaTranslator = formulaTranslator;
        _createReportHandler = createReportHandler;
        _createRelationshipHandler = createRelationshipHandler;
        _createFormHandler = createFormHandler;
        _saveFormLayoutHandler = saveFormLayoutHandler;
        _getFormLayoutHandler = getFormLayoutHandler;
        _createFormRuleHandler = createFormRuleHandler;
        _saveFormRuleHandler = saveFormRuleHandler;
        _createAppRoleHandler = createAppRoleHandler;
        _updateTablePermissionsHandler = updateTablePermissionsHandler;
        _extrasImporter = extrasImporter;
        _sourceRefRecorder = sourceRefRecorder;
        _deferredResolver = deferredResolver;
        _fieldSanitizer = fieldSanitizer;
        _settingsApplier = settingsApplier;
        _permissionsApplier = permissionsApplier;
    }

    private static void RequireCreateMode(ImportAppFromPblCommand command)
    {
        if (command.Mode != ImportMode.CreateNewApp)
            throw new BadRequestException("UNSUPPORTED_IMPORT_MODE", command.Mode == ImportMode.UpdateExistingApp
                ? "Use POST /apps/import/update to update an existing app."
                : $"Import mode '{command.Mode}' is not supported yet.");
    }

    private static IReadOnlyList<ParsedApp> ParseUploaded(string content)
    {
        try
        {
            ImportProgress.Report("Reading the file", 3);
            return ImportDocumentParser.ParseAll(content);
        }
        catch (JsonException ex)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Pbl"] = [$"Could not parse PBL document: {ex.Message}"],
            });
        }
        catch (YamlException ex)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Qbl"] = [$"Could not parse QBL document: {ex.Message}"],
            });
        }
    }

    /// <summary>Retries every connected table left waiting on an application or table that did not exist yet (see
    /// <see cref="PblConnectedTablesApplier"/>) — this import, or an earlier one, may have just supplied it. Covers the
    /// whole tenant, not only the app(s) this call imported, so a table added later by any import keeps resolving the
    /// same way. Never fails the import that triggers it.</summary>
    private async Task ResolvePendingConnectedTablesAsync(CancellationToken ct)
    {
        if (_pendingConnectedTableResolver is null) return;
        try { await _pendingConnectedTableResolver.ResolveAllAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: each still-pending row keeps its own reason and will be retried on the next import.
        }
    }

    /// <summary>Imports a file that holds exactly one application. A QBL file with several goes through <see cref="HandleBatchAsync"/>.</summary>
    public async Task<ImportReport> HandleAsync(ImportAppFromPblCommand command, CancellationToken ct = default)
    {
        RequireCreateMode(command);
        using var importScope = ImportRuntime.Begin();
        if (_pipelineOnlyImport is not null && QblPipelineOnlyBinder.IsPipelinesOnly(command.PblJson))
            return (await _pipelineOnlyImport.ImportAsync(command.PblJson, command.Mapping?.ActivatePipelines == true, ct)).Apps[0];
        var apps = ParseUploaded(command.PblJson);
        if (apps.Count > 1)
            throw new BadRequestException("IMPORT_MULTIPLE_APPS", "This file contains several applications; import it with the multi-application import.");
        var single = await ImportOneAsync(apps[0], command.Mapping?.ExcludedTableRefs, null, ct);
        if (_connectedTablesApplier is not null)
            await _connectedTablesApplier.ApplyAsync([(apps[0].Document, single)], ct); // resolved against every app the tenant has, this one included
        await ResolvePendingConnectedTablesAsync(ct);
        await ImportPipelinesAsync(apps, [(apps[0], single)], command.Mapping?.ActivatePipelines == true, ct);

        single.Issues = ImportIssueReportBuilder.BuildFromImportReport(single.AppName, single, apps[0].Document);
        await LogImportAuditAsync(single, ct);
        return single;
    }

    /// <summary>
    /// Imports every application in the file (minus the ones the mapping excludes), each as its own app, in file order.
    /// Applications are independent: one that fails is removed again (see <see cref="ImportOneAsync"/>) and reported,
    /// and the rest still import. A file with a single included application behaves exactly like <see cref="HandleAsync"/>
    /// — its failure is thrown, not reported.
    /// </summary>
    public async Task<ImportBatchReport> HandleBatchAsync(ImportAppFromPblCommand command, CancellationToken ct = default)
    {
        RequireCreateMode(command);
        using var importScope = ImportRuntime.Begin();
        var mapping = command.Mapping ?? new ImportMapping();
        // A file of pipelines only (no application): its PowerFlows go into the existing apps whose tables it uses.
        if (_pipelineOnlyImport is not null && QblPipelineOnlyBinder.IsPipelinesOnly(command.PblJson))
            return await _pipelineOnlyImport.ImportAsync(command.PblJson, mapping.ActivatePipelines, ct);
        var apps = ParseUploaded(command.PblJson);

        foreach (var entry in mapping.Apps)
            if (!apps.Any(a => string.Equals(a.AppRef, entry.AppRef, StringComparison.Ordinal)))
                throw new BadRequestException("MAPPING_UNKNOWN_APP", $"The mapping refers to an application that is not in the file ('{entry.AppRef}').");

        var included = apps.Where(a => mapping.ForApp(a.AppRef)?.Include != false).ToList();
        if (included.Count == 0)
            throw new BadRequestException("IMPORT_NO_APPS_SELECTED", "No application was selected for import.");

        var overrides = included.Select(a => mapping.ForApp(a.AppRef)?.NameOverride?.Trim()).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (overrides.Count != overrides.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new BadRequestException("APP_NAME_DUPLICATED_IN_FILE", "Two applications were given the same name.");

        var batch = new ImportBatchReport();
        var importedDocuments = new List<(PblDocument Document, ImportReport Report)>();
        var importedApps = new List<(ParsedApp App, ImportReport Report)>();
        for (var i = 0; i < included.Count; i++)
        {
            var app = included[i];
            var entry = mapping.ForApp(app.AppRef);
            // A file with one application keeps the legacy flat exclusion list.
            var excluded = entry?.ExcludedTableRefs.Count > 0 ? entry.ExcludedTableRefs : (apps.Count == 1 ? mapping.ExcludedTableRefs : []);
            var nameOverride = string.IsNullOrWhiteSpace(entry?.NameOverride) ? null : entry!.NameOverride!.Trim();

            ImportProgress.BeginApp(i, included.Count, nameOverride ?? app.Document.App?.Name ?? app.AppRef);
            try
            {
                var report = await ImportOneAsync(app, excluded, nameOverride, ct);
                batch.Apps.Add(report);
                importedDocuments.Add((app.Document, report));
                importedApps.Add((app, report));
            }
            catch (Exception ex) when (included.Count > 1 && ex is not OperationCanceledException)
            {
                batch.Failed.Add(new ImportAppFailure
                {
                    AppRef = app.AppRef,
                    AppName = nameOverride ?? app.Document.App?.Name ?? app.AppRef,
                    Code = ex is DomainException de ? de.ErrorCode : "IMPORT_FAILED",
                    Message = DescribeFailure(ex),
                });
            }
            finally
            {
                ImportProgress.EndApp();
            }
        }

        // Connected tables can only be set up once the application they read from exists.
        if (_connectedTablesApplier is not null && importedDocuments.Count > 0)
        {
            ImportProgress.Report("Connecting tables", 97);
            try { await _connectedTablesApplier.ApplyAsync(importedDocuments, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var (_, report) in importedDocuments)
                    report.Skipped.Add(new ImportSkippedItem { LogicalRef = "", Name = "Connected tables", Reason = $"Connected tables were not set up ({ex.Message})." });
            }
        }
        await ResolvePendingConnectedTablesAsync(ct);

        await ImportPipelinesAsync(apps, importedApps, command.Mapping?.ActivatePipelines == true, ct);

        // Structured issue report + audit record, built last so they see every skipped item/pipeline note
        // (connected tables and pipelines are both attached to the report above).
        foreach (var (document, report) in importedDocuments)
        {
            report.Issues = ImportIssueReportBuilder.BuildFromImportReport(report.AppName, report, document);
            await LogImportAuditAsync(report, ct);
        }

        return batch;
    }

    /// <summary>Preserves the full technical detail of what the import skipped/flagged — same mechanism
    /// already used for app/table/field/role changes elsewhere (<see cref="IAuditRepository.LogActivityAsync"/>)
    /// — so it stays available for support/debugging even though the API response now leads with the
    /// friendlier <see cref="ImportReport.Issues"/> restatement. Never fails the import itself.</summary>
    private async Task LogImportAuditAsync(ImportReport report, CancellationToken ct)
    {
        if (_auditRepo is null) return;
        try
        {
            var technicalDetail = JsonSerializer.Serialize(new
            {
                report.Skipped,
                report.FormulaTranslations,
                Pipelines = report.Pipelines.Select(p => new { p.LogicalRef, p.Name, p.Status, p.Issues }),
            });
            await _auditRepo.LogActivityAsync(
                AuditActions.Imported,
                AuditEntityTypes.App,
                report.AppPublicId.ToString(),
                entityTitle: report.AppName,
                newValues: technicalDetail,
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort — the import already succeeded; losing the audit trail must not undo it.
        }
    }

    /// <summary>The file's Quickbase pipelines, as PowerFlows of the apps just created. Like connected tables they come last — their
    /// steps read the apps' tables and fields — and a failure is reported, never thrown: the apps are already imported.</summary>
    private async Task ImportPipelinesAsync(IReadOnlyList<ParsedApp> apps, IReadOnlyList<(ParsedApp App, ImportReport Report)> imported, bool activate, CancellationToken ct)
    {
        if (_pipelinesImporter is null || imported.Count == 0 || apps.All(a => (a.Pipelines?.Count ?? 0) == 0))
            return;

        ImportProgress.Report("Creating PowerFlows", 98);
        try { await _pipelinesImporter.ImportAsync(apps, imported, ct, activate); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            imported[0].Report.Skipped.Add(new ImportSkippedItem { LogicalRef = "", Name = "PowerFlows", Reason = $"The file's pipelines were not imported ({ex.Message})." });
        }
    }

    /// <summary>Why a summary is still waiting after every round: the target field, or a field its filter uses, was never created.
    /// (A Count/Exists summary has no target — saying "target '' was not created" hid the real blocker.)</summary>
    private async Task<string> DescribeBlockedSummaryAsync(DeferredSummary item, CancellationToken ct)
    {
        var childFields = await _fieldRepo.ListByTableAsync(item.ChildTable.Id, ct);
        var map = FieldFids.ByNameOrLabel(childFields);

        if (item.Summary.TargetFieldName is { Length: > 0 } target && !map.ContainsKey(target))
            return $"Summary target field '{target}' was not created on '{item.ChildTable.Name}'.";

        var missing = new List<string>();
        void Walk(PblFilterGroup? group)
        {
            if (group is null) return;
            foreach (var c in group.Conditions)
            {
                if (!map.ContainsKey(c.FieldName)) missing.Add(c.FieldName);
                if (c.ValueFieldName is { Length: > 0 } other && !map.ContainsKey(other)) missing.Add(other);
            }
            foreach (var g in group.Groups) Walk(g);
        }
        Walk(item.Summary.Filter);

        return missing.Count > 0
            ? $"Summary not created: its filter uses field(s) {string.Join(", ", missing.Distinct().Select(m => $"'{m}'"))} that were not created on '{item.ChildTable.Name}'."
            : $"Summary could not be created on '{item.ChildTable.Name}' (waiting for a field that never appeared).";
    }

    private static readonly JsonSerializerOptions NoteJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record PlaceholderWanted(
        string TableName, string Ref, string Name, string? Label, int? Fid, string TypeCode, string? Description, string? HelpText,
        string Definition, string Metadata, string? SourceType, string Code);

    /// <summary>A lookup, summary or formula that still could not be created after every retry gets a plain field with the same name
    /// and Fid, so the field exists, forms/reports/rules that refer to it resolve, and it can be repaired later. The field is typed after
    /// the source's result type where that is certain (Text otherwise). The original definition — complete, never truncated — and the
    /// reason go to the field's import note, not into its description. One placeholder that cannot be created never stops the others.
    /// New-app imports only (the update flow does not renumber).</summary>
    private async Task<int> CreatePlaceholderFieldsAsync(
        PblDocument document,
        List<PblRelationship> relationships,
        Dictionary<string, string> tableNameByRef,
        Dictionary<string, AppTable> tableByName,
        List<ImportSkippedItem> skipped,
        List<FormulaTranslationReportItem> formulaTranslations,
        List<ImportFieldNoteDraft> notes,
        CancellationToken ct)
    {
        if (ImportRuntime.Fids is null) return 0;

        var reasonByRef = new Dictionary<string, ImportSkippedItem>(StringComparer.Ordinal);
        foreach (var s in skipped.Where(s => !string.IsNullOrEmpty(s.LogicalRef)))
            reasonByRef.TryAdd(s.LogicalRef, s);

        var fieldCache = new Dictionary<string, IReadOnlyList<AppField>>(StringComparer.OrdinalIgnoreCase);
        async Task<string?> TypeOfAsync(string? tableName, string? fieldName)
        {
            if (tableName is null || fieldName is null || !tableByName.TryGetValue(tableName, out var t)) return null;
            if (!fieldCache.TryGetValue(tableName, out var fields))
                fieldCache[tableName] = fields = await _fieldRepo.ListByTableAsync(t.Id, ct);
            return fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase)
                || string.Equals((f.Label ?? "").Trim(), fieldName.Trim(), StringComparison.OrdinalIgnoreCase))?.TypeCode;
        }

        static string Capped(string? text) => text is { Length: > ImportFieldSanitizer.DESCRIPTION_MAX } ? text[..(ImportFieldSanitizer.DESCRIPTION_MAX - 1)] + "…" : text ?? "";

        var wanted = new List<PlaceholderWanted>();
        foreach (var rel in relationships)
        {
            var relationshipInfo = new { rel.LogicalRef, rel.ParentTableRef, rel.ChildTableRef, rel.ReferenceFieldName };

            // A relationship that could not be created takes its reference field with it.
            var relationshipFailed = skipped.Any(s => s.LogicalRef == rel.LogicalRef
                && (s.Code == "REFERENCE_NOT_CREATED" || s.Reason.StartsWith("Relationship references a table that was not created", StringComparison.Ordinal)));
            if (relationshipFailed && tableNameByRef.TryGetValue(rel.ChildTableRef, out var referenceTable))
            {
                tableNameByRef.TryGetValue(rel.ParentTableRef, out var referenceParent);
                wanted.Add(new PlaceholderWanted(referenceTable, rel.LogicalRef, rel.ReferenceFieldName, rel.ReferenceFieldLabel, rel.ReferenceFieldSourceId, "Number",
                    rel.ReferenceFieldDescription is null ? null : Capped(rel.ReferenceFieldDescription), null,
                    $"Reference field to the records of '{referenceParent ?? rel.ParentTableRef}'",
                    JsonSerializer.Serialize(new { kind = "Reference", relationship = relationshipInfo, isRequired = rel.IsReferenceRequired, description = rel.ReferenceFieldDescription }, NoteJson),
                    "QB::Field::Reference", "REFERENCE_NOT_CREATED"));
            }
            if (rel.Lookups.Count > 0 && tableNameByRef.TryGetValue(rel.ChildTableRef, out var childName))
            {
                tableNameByRef.TryGetValue(rel.ParentTableRef, out var parentOfLookup);
                foreach (var l in rel.Lookups.Where(l => reasonByRef.ContainsKey(l.LogicalRef)))
                {
                    var type = QblPlaceholders.TypeForExistingField(await TypeOfAsync(parentOfLookup, l.SourceFieldName));
                    wanted.Add(new PlaceholderWanted(childName, l.LogicalRef, l.Name, l.Label, l.SourceId, type,
                        l.Description is null ? null : Capped(l.Description), null,
                        $"Lookup of '{l.SourceFieldName}' from the related '{rel.ReferenceFieldName}' record",
                        JsonSerializer.Serialize(new { kind = "Lookup", lookup = l, relationship = relationshipInfo }, NoteJson),
                        "QB::Field::Lookup", "LOOKUP_NOT_CREATED"));
                }
            }
            if (rel.Summaries.Count > 0 && tableNameByRef.TryGetValue(rel.ParentTableRef, out var parentName))
            {
                tableNameByRef.TryGetValue(rel.ChildTableRef, out var childOfSummary);
                foreach (var sm in rel.Summaries.Where(sm => reasonByRef.ContainsKey(sm.LogicalRef)))
                {
                    var type = SummaryPlaceholderType(sm.Function, await TypeOfAsync(childOfSummary, sm.TargetFieldName));
                    wanted.Add(new PlaceholderWanted(parentName, sm.LogicalRef, sm.Name, sm.Label, sm.SourceId, type,
                        sm.Description is null ? null : Capped(sm.Description), null,
                        $"Summary: {sm.Function}{(sm.TargetFieldName is { Length: > 0 } t ? $" of '{t}'" : "")}{(string.IsNullOrWhiteSpace(sm.QueryCriteria) ? "" : $" where {sm.QueryCriteria}")}",
                        JsonSerializer.Serialize(new { kind = "Summary", summary = sm, relationship = relationshipInfo }, NoteJson),
                        "QB::Field::Summary", "SUMMARY_NOT_CREATED"));
                }
            }
        }
        foreach (var table in document.Tables ?? [])
            foreach (var f in (table.Fields ?? []).Where(IsFormulaField).Where(f => reasonByRef.ContainsKey(f.LogicalRef)))
            {
                var diagnostics = formulaTranslations.FirstOrDefault(t => t.LogicalRef == f.LogicalRef)?.Diagnostics;
                wanted.Add(new PlaceholderWanted(table.Name, f.LogicalRef, f.Name, f.Label, KeptFid(f),
                    QblPlaceholders.TypeForFormula(f.ResultType, f.FormulaVariant, f.SourceType),
                    f.Description is null ? null : Capped(f.Description), f.HelpText,
                    f.OriginalFormula ?? f.FormulaExpression ?? string.Empty,
                    JsonSerializer.Serialize(new
                    {
                        kind = "Formula", sourceType = f.SourceType, resultType = f.ResultType, variant = f.FormulaVariant, sourceId = f.SourceId,
                        settings = f.Settings, convertedExpression = f.FormulaExpression, translationDiagnostics = diagnostics,
                        description = f.Description, helpText = f.HelpText,
                    }, NoteJson),
                    f.SourceType ?? "QB::Field::Formula", "FORMULA_NEEDS_MANUAL_REVIEW"));
            }

        var created = 0;
        foreach (var group in wanted.GroupBy(w => w.TableName, StringComparer.OrdinalIgnoreCase))
        {
            if (!tableByName.TryGetValue(group.Key, out var table)) continue;

            var existing = await _fieldRepo.ListByTableAsync(table.Id, ct);
            var usedLabels = existing.Select(f => (f.Label ?? "").Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usedNames = existing.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var items = new List<(PlaceholderWanted W, BulkCreateFieldItem Item)>();
            foreach (var w in group)
            {
                // The field exists already (it was created after all - an earlier warning about it does not mean it failed): nothing to stand in for.
                if (usedNames.Contains(w.Name))
                    continue;
                // Same for a reference field whose relationship was created after all (only part of it failed).
                if (w.Code == "REFERENCE_NOT_CREATED" && usedLabels.Contains((w.Label ?? w.Name).Trim()))
                    continue;
                var label = UniqueLabel((w.Label ?? w.Name).Trim(), usedLabels);
                items.Add((w, new BulkCreateFieldItem(w.TypeCode, label, Description: string.IsNullOrWhiteSpace(w.Description) ? null : w.Description,
                    IsEncrypted: true, Name: w.Name, HelpText: w.HelpText, Fid: w.Fid)));
            }
            if (items.Count == 0) continue;

            foreach (var (w, ok, typeUsed, error) in await CreatePlaceholdersAsync(table, items, ct))
            {
                if (!ok)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = w.Ref, Name = w.Name, Code = "PLACEHOLDER_NOT_CREATED",
                        Reason = $"The placeholder for '{w.Name}' on '{group.Key}' could not be created: {error}" });
                    continue;
                }

                created++;
                var reason = reasonByRef.TryGetValue(w.Ref, out var s) ? s.Reason : "The field could not be created as itself.";
                if (reasonByRef.TryGetValue(w.Ref, out s) && skipped.IndexOf(s) is var at and >= 0)
                    skipped[at] = new ImportSkippedItem { LogicalRef = s.LogicalRef, Name = s.Name, Code = s.Code, Reason = s.Reason + $" A repairable placeholder {typeUsed} field was created in its place." };
                formulaTranslations.FirstOrDefault(t => t.LogicalRef == w.Ref)?.Diagnostics.Add($"A placeholder {typeUsed} field was created in its place.");

                notes.Add(new ImportFieldNoteDraft
                {
                    TableName = group.Key,
                    FieldName = w.Name,
                    FieldLabel = w.Label,
                    SourceFid = w.Fid,
                    Severity = ImportNoteSeverities.Warning,
                    Code = w.Code,
                    Message = $"{reason} A placeholder {typeUsed} field was created in its place.",
                    RecommendedAction = ImportFieldNoteBuilder.PlaceholderAction(w.Definition, w.SourceType),
                    SourceLogicalRef = w.Ref,
                    SourceType = w.SourceType,
                    IsPlaceholder = true,
                    OriginalDefinition = w.Definition,
                    OriginalMetadata = w.Metadata,
                });
            }
        }
        return created;
    }

    /// <summary>Creates the stand-in fields of one table. The whole batch first; if anything in it fails, one at a time, so a single field
    /// that cannot be created costs only itself (a field that cannot be created in its own type is retried as plain Text).</summary>
    private async Task<List<(PlaceholderWanted W, bool Ok, string TypeUsed, string? Error)>> CreatePlaceholdersAsync(
        AppTable table, List<(PlaceholderWanted W, BulkCreateFieldItem Item)> items, CancellationToken ct)
    {
        var results = new List<(PlaceholderWanted, bool, string, string?)>();
        try
        {
            await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(table.PublicId, items.Select(i => i.Item).ToList()), ct);
            results.AddRange(items.Select(i => (i.W, true, i.Item.TypeCode, (string?)null)));
            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // fall through: some of the batch may exist already
        }

        foreach (var (w, item) in items)
        {
            string? lastError = null;
            foreach (var candidate in item.TypeCode == "Text" ? new[] { item } : new[] { item, item with { TypeCode = "Text" } })
            {
                try
                {
                    var current = await _fieldRepo.ListByTableAsync(table.Id, ct);
                    if (current.Any(f => string.Equals(f.Name, w.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        results.Add((w, true, current.First(f => string.Equals(f.Name, w.Name, StringComparison.OrdinalIgnoreCase)).TypeCode, null));
                        lastError = null;
                        break;
                    }
                    await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(table.PublicId, [candidate]), ct);
                    results.Add((w, true, candidate.TypeCode, null));
                    lastError = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex is DomainException de ? Describe(de) : ex.Message;
                }
            }
            if (lastError is not null)
                results.Add((w, false, item.TypeCode, lastError));
        }
        return results;
    }

    /// <summary>The plain type that holds a summary's place: counts and totals are numbers, "any related records" a Boolean, minimum/maximum
    /// the summarized field's own type, combined text Text.</summary>
    private static string SummaryPlaceholderType(string function, string? targetTypeCode)
    {
        var target = QblPlaceholders.TypeForExistingField(targetTypeCode);
        return function switch
        {
            "Count" or "DistinctCount" => "Number",
            "Exists" => "Boolean",
            "Sum" or "Avg" => target is "Currency" or "Percent" or "Duration" ? target : "Number",
            "Min" or "Max" => target,
            _ => QblPlaceholders.TEXT,
        };
    }

    /// <summary>The app's variables, for the formula translator. The caller assigns <see cref="ImportRuntime.Variables"/> itself.</summary>
    private async Task<PowerBase.Formula.Binding.IVariableSchema> LoadVariableSchemaAsync(Guid appPublicId, CancellationToken ct)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        return await _extrasImporter.BuildVariableSchemaAsync(appId, ct);
    }

    /// <summary>The Quickbase id to keep as the Fid of a field created by this import. Only a new-app import keeps ids (the update
    /// flow adds fields to an app that already has its own numbering).</summary>
    private static int? KeptFid(PblField field) => ImportRuntime.Fids is null ? null : field.SourceId;

    /// <summary>Tells the Fid policy what the source fields are numbered: for every table the highest source id (so fields PowerBase
    /// creates by itself stay above it) and the numbers of the reference, lookup and summary fields the relationship handlers create.</summary>
    /// <summary>powerflowextraRd: the labels a table's system fields will carry — PowerBase's defaults, each replaced by the
    /// source's own label when it renamed the built-in field (Quickbase "Last Modified By" → "LastModifiedBy").</summary>
    private static IEnumerable<string> SystemLabelsOf(PblTable table) =>
        AppSeeder.SystemFieldLabels.Select(label => table.SystemFieldLabels?.GetValueOrDefault(label) ?? label);

    /// <summary>The default system-field labels (Record ID#, Date Created, …) that the source renamed on this table. Until
    /// <see cref="ApplySystemFieldLabelsAsync"/> runs, PowerBase's seeded system field still holds each of these, so no other field may be
    /// created with one of them.</summary>
    public static HashSet<string> SystemLabelCollisions(PblTable table) =>
        new(AppSeeder.SystemFieldLabels.Where(label => table.SystemFieldLabels is not null
                && table.SystemFieldLabels.TryGetValue(label, out var renamedTo)
                && !string.Equals(renamedTo, label, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Gives back its real label to every field that was created under a temporary one (see <see cref="SystemLabelCollisions"/>).
    /// If the system field could not be renamed (so the real label is still taken) the temporary label stays and the import says so.</summary>
    private async Task RestoreLabelsAfterSystemRenameAsync(
        List<(string TableName, string TemporaryLabel, string FinalLabel)> restores, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        if (restores.Count == 0) return;

        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var tableByName = (await _tableRepo.ListByAppAsync(appId, ct)).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var group in restores.GroupBy(r => r.TableName, StringComparer.OrdinalIgnoreCase))
        {
            if (!tableByName.TryGetValue(group.Key, out var table)) continue;
            var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
            foreach (var (_, temporary, final) in group)
            {
                var field = fields.FirstOrDefault(f => !f.IsSystem && string.Equals((f.Label ?? "").Trim(), temporary, StringComparison.OrdinalIgnoreCase));
                if (field is null) continue;

                if (fields.Any(f => f.Id != field.Id && string.Equals((f.Label ?? "").Trim(), final, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = field.PublicId.ToString(), Name = temporary,
                        Reason = $"Kept as '{temporary}': the label '{final}' is still used by a built-in field of '{group.Key}'." });
                    continue;
                }

                await _fieldRepo.UpdateAsync(field.PublicId, table.Id, final, field.Description, field.IsRequired, field.DefaultValue,
                    field.IsSearchable, field.IsSortable, field.IsFilterable, field.IsReportable, field.IsAuditable, field.IsUnique,
                    field.IsEncrypted, field.IsAutoFill, field.Settings, ct);
                field.Label = final;
            }
        }
    }

    /// <summary>powerflowextraRd: gives the seeded system fields (Record ID#, Date Created, …, Last Modified By) the labels
    /// the source app uses for them. Runs before formula fields are created, so a formula that carries the default label
    /// (Quickbase's FID 112 "Last Modified By") keeps it, and a PowerFlow reading that field shows the same label as in
    /// Quickbase. Only the label changes; the field's Name, FID and settings stay as seeded.</summary>
    private async Task ApplySystemFieldLabelsAsync(PblDocument document, Guid appPublicId, CancellationToken ct)
    {
        var renamed = (document.Tables ?? []).Where(t => t.SystemFieldLabels is { Count: > 0 }).ToList();
        if (renamed.Count == 0) return;

        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var tableByName = (await _tableRepo.ListByAppAsync(appId, ct)).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var source in renamed)
        {
            if (!tableByName.TryGetValue(source.Name, out var table)) continue;
            var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
            foreach (var (defaultLabel, label) in source.SystemFieldLabels!)
            {
                var field = fields.FirstOrDefault(f => f.IsSystem && string.Equals((f.Label ?? "").Trim(), defaultLabel, StringComparison.OrdinalIgnoreCase));
                // Never take a label another field of the table already holds.
                if (field is null || fields.Any(f => f.Id != field.Id && string.Equals((f.Label ?? "").Trim(), label, StringComparison.OrdinalIgnoreCase)))
                    continue;
                await _fieldRepo.UpdateAsync(field.PublicId, table.Id, label, field.Description, field.IsRequired, field.DefaultValue,
                    field.IsSearchable, field.IsSortable, field.IsFilterable, field.IsReportable, field.IsAuditable, field.IsUnique,
                    field.IsEncrypted, field.IsAutoFill, field.Settings, ct);
                field.Label = label;
            }
        }
    }

    private async Task RegisterSourceFidsAsync(PblDocument document, Guid appPublicId, CancellationToken ct)
    {
        var plan = ImportRuntime.Fids;
        if (plan is null) return;

        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var tables = await _tableRepo.ListByAppAsync(appId, ct);
        var tableByName = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var nameByRef = (document.Tables ?? []).ToDictionary(t => t.LogicalRef, t => t.Name, StringComparer.Ordinal);

        void Floor(string tableRef, int? id)
        {
            if (id is { } fid && nameByRef.TryGetValue(tableRef, out var name) && tableByName.TryGetValue(name, out var t))
                plan.SetFloor(t.Id, fid);
        }
        void Expect(string tableRef, string label, int? id)
        {
            if (id is { } fid && nameByRef.TryGetValue(tableRef, out var name) && tableByName.TryGetValue(name, out var t))
                plan.Expect(t.Id, label, fid);
        }

        foreach (var table in document.Tables ?? [])
            foreach (var f in table.Fields ?? [])
                Floor(table.LogicalRef, f.SourceId);

        foreach (var rel in document.Relationships ?? [])
        {
            Floor(rel.ChildTableRef, rel.ReferenceFieldSourceId);
            Expect(rel.ChildTableRef, rel.ReferenceFieldLabel ?? rel.ReferenceFieldName, rel.ReferenceFieldSourceId);
            foreach (var l in rel.Lookups)
            {
                Floor(rel.ChildTableRef, l.SourceId);
                Expect(rel.ChildTableRef, l.Label ?? l.Name, l.SourceId);
            }
            foreach (var sm in rel.Summaries)
            {
                Floor(rel.ParentTableRef, sm.SourceId);
                Expect(rel.ParentTableRef, sm.Label ?? sm.Name, sm.SourceId);
            }
            // The parent's report link is created on its own number (see CreateRelationshipCommand.ReportLinkFid); the floor keeps
            // fields PowerBase numbers by itself from taking it first.
            if (rel.ExplicitLinks)
                Floor(rel.ParentTableRef, rel.ReportLink?.SourceId);
        }

        foreach (var table in document.Tables ?? [])
            foreach (var link in table.ReportLinks ?? [])
                Floor(table.LogicalRef, link.SourceId);
    }

    /// <summary>Message a person importing may see. Internal detail only outside production-like settings.</summary>
    private static string DescribeFailure(Exception ex) => ex switch
    {
        ValidationException ve => string.Join(" ", ve.Errors.SelectMany(e => e.Value)),
        DomainException de => de.Message,
        AggregateException ag when ag.InnerExceptions.Count > 0 && ag.Message.Contains("must be deleted manually") => ag.Message,
        _ when string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase)
            => $"Unexpected error ({ex.GetType().Name}): {ex.Message}",
        _ => "The application could not be imported because of an unexpected error.",
    };

    private async Task<ImportReport> ImportOneAsync(ParsedApp parsed, IReadOnlyCollection<string>? excludedTableRefs, string? nameOverride, CancellationToken ct)
    {
        var document = parsed.Document;
        var conversionIssues = parsed.Issues;
        if (nameOverride is not null)
            document.App.Name = nameOverride;

        var conversionErrors = conversionIssues.Where(i => i.Severity == PblIssueSeverity.Error).ToList();
        if (conversionErrors.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Qbl"] = conversionErrors.Select(e => e.Message).ToArray(),
            });

        PblDocumentTools.ExcludeTables(document, excludedTableRefs);

        var validation = _validator.Validate(document);
        if (!validation.IsValid)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Pbl"] = validation.Errors.Select(e => e.Message).ToArray(),
            });

        var skipped = new List<ImportSkippedItem>();
        skipped.AddRange(conversionIssues
            .Where(i => i.Severity == PblIssueSeverity.Warning)
            .Select(i => new ImportSkippedItem { LogicalRef = i.ElementRef ?? "", Name = i.ElementRef ?? "", Reason = i.Message, Code = i.Code }));

        var tableSpecs = new List<TableSpec>();

        // Fields created under a temporary label because the source gave a field the label of one of PowerBase's system fields while renaming
        // that system field itself (see SystemLabelCollisions); their real label is put back once the system fields are renamed.
        var labelRestores = new List<(string TableName, string TemporaryLabel, string FinalLabel)>();

        // Field-level notes collected while this app is built (stand-ins made late, rewritten formulas, shortened text); stored at the end.
        var fieldNotes = new List<ImportFieldNoteDraft>();

        // Pass 1 input: scalar fields only. Formula fields are deferred to pass 2.
        foreach (var table in document.Tables ?? [])
        {
            var fieldSpecs = new List<AppFieldSpec>();

            // Labels must be unique per table, and PowerBase's own system fields (Record ID#, Date Created, …) already
            // hold theirs. A source field that reuses one of those labels, or repeats another field's, gets a numbered label.
            // powerflowextraRd: the system fields hold the source's labels for them (applied after the app is created, see
            // ApplySystemFieldLabelsAsync), so those — not PowerBase's defaults — are the labels already taken.
            var usedLabels = new HashSet<string>(SystemLabelsOf(table), StringComparer.OrdinalIgnoreCase);

            foreach (var sourceField in table.Fields ?? [])
            {
                if (IsFormulaField(sourceField))
                    continue;

                var field = sourceField;
                if (!PblValidator.SupportedFieldTypeCodes.Contains(field.TypeCode))
                {
                    // A field of a type this import cannot create is never dropped: a plain Text field keeps its place and the import
                    // note keeps what the document said about it.
                    var reason = $"Unsupported field type '{field.TypeCode}' for this import phase";
                    skipped.Add(new ImportSkippedItem
                    {
                        LogicalRef = field.LogicalRef,
                        Name = field.Name,
                        Reason = $"{reason}. A placeholder Text field was created in its place.",
                        Code = "UNSUPPORTED_FIELD_TYPE",
                    });
                    fieldNotes.Add(new ImportFieldNoteDraft
                    {
                        TableName = table.Name,
                        FieldName = field.Name,
                        FieldLabel = field.Label,
                        SourceFid = field.SourceId,
                        Code = "UNSUPPORTED_FIELD_TYPE",
                        Message = $"{reason}. A placeholder Text field was created in its place.",
                        RecommendedAction = ImportFieldNoteBuilder.PLACEHOLDER_ACTION,
                        SourceLogicalRef = field.LogicalRef,
                        SourceType = field.TypeCode,
                        IsPlaceholder = true,
                        OriginalMetadata = JsonSerializer.Serialize(field),
                    });
                    field = new PblField
                    {
                        LogicalRef = field.LogicalRef, Name = field.Name, Label = field.Label, TypeCode = "Text", Description = field.Description,
                        HelpText = field.HelpText, SourceId = field.SourceId, SourceType = field.TypeCode,
                        Placeholder = new PblPlaceholderInfo { Code = "UNSUPPORTED_FIELD_TYPE", Reason = reason },
                    };
                }

                // Preserve the imported file's exact Name (its stable third-party identifier) rather
                // than regenerating one — see AppFieldSpec.Name.
                var item = BuildScalarItem(field, skipped, fieldNotes, table.Name);
                var uniqueLabel = UniqueLabel(item.Label.Trim(), usedLabels);
                if (!string.Equals(uniqueLabel, item.Label.Trim(), StringComparison.Ordinal))
                    skipped.Add(new ImportSkippedItem
                    {
                        LogicalRef = field.LogicalRef,
                        Name = field.Name,
                        Reason = $"Imported as '{uniqueLabel}': the label '{item.Label.Trim()}' is already used by another field on '{table.Name}'.",
                    });
                // The system fields are renamed only after the app exists, so until then PowerBase's seeded one still holds the default label.
                // A source field that uses that default label (while renaming its own system field, e.g. "Date Created" → "DateCreated") would
                // be rejected as a duplicate when the table is created: it is created under a temporary label and given its own label back
                // right after the system field is renamed.
                var createLabel = uniqueLabel;
                if (SystemLabelCollisions(table).Contains(uniqueLabel))
                {
                    createLabel = UniqueLabel($"{uniqueLabel} (imported)", usedLabels);
                    labelRestores.Add((table.Name, createLabel, uniqueLabel));
                }

                fieldSpecs.Add(new AppFieldSpec(
                    createLabel, item.TypeCode, item.Settings, Name: item.Name, Description: item.Description, HelpText: item.HelpText,
                    IsRequired: item.IsRequired, DefaultValue: item.DefaultValue, IsAuditable: item.IsAuditable,
                    IsSearchable: item.IsSearchable, IsReportable: item.IsReportable, Fid: KeptFid(field)));
            }

            // Only let the seeder add its stock "Main Form"/"List All"/"List Changes" when this
            // document doesn't bring its own — real exports overwhelmingly do (every table in a
            // real Quickbase export carries both a "Main Form" and its own "List All"), and
            // seeding alongside them leaves two of each with the *empty* seeded copy marked
            // default, so that's the one users would open.
            // The stock views are seeded per kind: a table that brings reports but no form (legacy-only Quickbase tables) still gets
            // PowerBase's default "Main Form", and a table that brings forms but no report still gets "List All".
            var suppliesReports = (table.Reports ?? []).Count > 0;
            var suppliesForms = (document.Forms ?? []).Any(f => f.TableRef == table.LogicalRef);

            tableSpecs.Add(new TableSpec(
                table.Name,
                table.SingularLabel,
                table.PluralLabel,
                table.Icon,
                table.Description,
                Config: null,
                Fields: fieldSpecs,
                SeedDefaultViews: true,
                SeedDefaultReports: !suppliesReports,
                SeedDefaultForm: !suppliesForms,
                Alias: table.AliasHint));
        }

        var createCommand = new CreateAppCommand(
            document.App.Name,
            document.App.Description,
            document.App.Icon,
            document.App.Color,
            tableSpecs,
            IsEncrypted: true); // imported apps are encrypted by default, like the create-app wizard

        ImportProgress.Report("Creating tables and fields", 8);
        var createResult = await _createAppHandler.HandleAsync(createCommand, ct);
        var fieldsCreated = tableSpecs.Sum(t => t.Fields?.Count ?? 0);

        var formulaTranslations = new List<FormulaTranslationReportItem>();
        PblApplyResult applied;

        // Everything past this point builds onto the app just created. A single transaction around
        // it isn't viable — these handlers each open their own connection, and holding one open
        // across ~46 CREATE TABLEs and hundreds of ADD COLUMNs would keep schema locks on a shared
        // tenant database for the whole run. Because CreateNewApp only ever builds a brand-new app,
        // deleting it on failure gives the same all-or-nothing result the caller cares about
        // without blocking every other user of that tenant.
        try
        {
            await RegisterSourceFidsAsync(document, createResult.PublicId, ct);
            await ApplySystemFieldLabelsAsync(document, createResult.PublicId, ct); // powerflowextraRd
            await RestoreLabelsAfterSystemRenameAsync(labelRestores, createResult.PublicId, skipped, ct);
            await _settingsApplier.ApplyAppSettingsAsync(document.App, createResult.PublicId, skipped, ct);
            await _settingsApplier.ApplyTableKeysAsync(document, createResult.PublicId, skipped, ct);
            ImportProgress.Report("Creating roles, relationships and formulas", 30);
            applied = await ApplyDocumentAsync(document, createResult.PublicId, null, skipped, formulaTranslations, ct, fieldNotes);
            ImportProgress.Report("Applying table settings", 92);
            await _settingsApplier.ApplyTableSettingsAsync(document, createResult.PublicId, skipped, ct);
            await _settingsApplier.ApplyReferenceFiltersAsync(document, createResult.PublicId, skipped, ct);
            await _settingsApplier.ApplyRelationshipFieldDescriptionsAsync(document, createResult.PublicId, skipped, ct);
            await _settingsApplier.ApplyRelationshipFieldFlagsAsync(document, createResult.PublicId, skipped, ct);
            await _settingsApplier.ApplyParentLinksAsync(document, createResult.PublicId, skipped, ct);
            ImportRuntime.Variables = await LoadVariableSchemaAsync(createResult.PublicId, ct); // the value set inside ApplyDocumentAsync does not reach this method
            await _settingsApplier.ApplyCustomDataRulesAsync(document, createResult.PublicId, skipped, ct);
        }
        catch (Exception ex)
        {
            try
            {
                await _deleteAppHandler.HandleAsync(new DeleteAppCommand(createResult.PublicId), CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                // Surface both: the caller needs to know the import failed *and* that a
                // half-built app is still sitting there needing manual removal.
                throw new AggregateException(
                    $"Import failed and the partially-created app '{createResult.Name}' could not be removed — it must be deleted manually.",
                    ex, cleanupEx);
            }

            throw;
        }

        // Remembering where each element came from only helps a later "Update Existing App"; the
        // import itself already succeeded, so a failure here must not undo it.
        try
        {
            await _sourceRefRecorder.RecordAsync(document, createResult.PublicId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            skipped.Add(new ImportSkippedItem { LogicalRef = "", Name = "Source references", Reason = $"The app was imported, but its source references could not be saved ({ex.Message}); a later update will match by name instead." });
        }

        foreach (var note in ImportRuntime.Fids?.DrainFallbacks() ?? [])
            skipped.Add(new ImportSkippedItem { LogicalRef = "", Name = "Field number", Reason = note });

        await StoreFieldNotesAsync(document, createResult.PublicId, skipped, fieldNotes, ct);

        return new ImportReport
        {
            AppPublicId = createResult.PublicId,
            AppName = createResult.Name,
            TablesCreated = tableSpecs.Count,
            FieldsCreated = fieldsCreated + applied.FieldsCreated,
            ReportsCreated = applied.ReportsCreated,
            RelationshipsCreated = applied.RelationshipsCreated,
            FormsCreated = applied.FormsCreated,
            FormRulesCreated = applied.FormRulesCreated,
            RolesCreated = applied.RolesCreated,
            VariablesCreated = applied.VariablesCreated,
            PagesCreated = applied.PagesCreated,
            Skipped = skipped,
            FormulaTranslations = formulaTranslations,
        };
    }

    /// <summary>Stores the field-level import notes (stand-in fields with their complete original definition, rewritten formulas, anything else
    /// that was reported about one field) next to the fields they are about. Never fails the import: the app is already built, and every note
    /// is also in the report.</summary>
    private async Task StoreFieldNotesAsync(PblDocument document, Guid appPublicId, List<ImportSkippedItem> skipped, List<ImportFieldNoteDraft> lateNotes, CancellationToken ct)
    {
        if (_fieldNoteRecorder is null) return;
        try
        {
            var drafts = ImportFieldNoteBuilder.Build(document, skipped, lateNotes);
            var result = await _fieldNoteRecorder.RecordAsync(appPublicId, drafts, ct);
            foreach (var lost in result.Unmatched.Where(d => d.IsPlaceholder))
                skipped.Add(new ImportSkippedItem
                {
                    LogicalRef = lost.SourceLogicalRef ?? "", Name = lost.FieldName, Code = "FIELD_NOTE_NOT_STORED",
                    Reason = $"The import note for '{lost.FieldName}' on '{lost.TableName}' could not be attached because the field was not found. Original definition: {lost.OriginalDefinition}",
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            skipped.Add(new ImportSkippedItem { LogicalRef = "", Name = "Field import notes", Code = "FIELD_NOTES_NOT_STORED", Reason = $"The app was imported, but its field-level import notes could not be saved ({ex.Message}); the same warnings are in this report." });
        }
    }

    /// <summary>
    /// Runs passes 2–7 plus variables and code pages for <paramref name="document"/> against an app
    /// that already exists with the document's tables and scalar fields in place. Shared by Create
    /// (right after the app is built) and Update (against the subset of the document the user
    /// approved). Tables named in <paramref name="tablesKeepingDefaults"/> already have their own
    /// default form/report, which must not be displaced by an imported one.
    /// </summary>
    public async Task<PblApplyResult> ApplyDocumentAsync(
        PblDocument document,
        Guid appPublicId,
        ISet<string>? tablesKeepingDefaults,
        List<ImportSkippedItem> skipped,
        List<FormulaTranslationReportItem> formulaTranslations,
        CancellationToken ct,
        List<ImportFieldNoteDraft>? notes = null)
    {
        notes ??= [];
        var fieldsCreated = 0;
        var reportsCreated = 0;
        var relationshipsCreated = 0;
        var formsCreated = 0;
        var formRulesCreated = 0;
        var rolesCreated = 0;
        var dashboardsCreated = 0;
        var relationships = document.Relationships ?? [];
        var forms = document.Forms ?? [];
        var roles = document.Roles ?? [];

        // Variables first: a formula that reads one ([1226], [Tax]) only compiles when the translator can see it.
        ImportProgress.Report("Creating variables", 33);
        var variablesCreated = await _extrasImporter.CreateVariablesAsync(document.Variables ?? [], appPublicId, skipped, ct);
        // Assigned here, not inside a helper: an AsyncLocal set in an awaited callee does not reach its caller.
        ImportRuntime.Variables = await LoadVariableSchemaAsync(appPublicId, ct);

        var hasFormulasReportsRelationshipsFormsOrRoles = relationships.Count > 0 || forms.Count > 0 || roles.Count > 0 || (document.Tables ?? []).Any(t =>
            (t.Fields ?? []).Any(IsFormulaField) || (t.Reports ?? []).Count > 0 || (t.ReportLinks ?? []).Count > 0);

        if (hasFormulasReportsRelationshipsFormsOrRoles)
        {
            var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
            var createdTables = await _tableRepo.ListByAppAsync(appId, ct);
            var tableByName = createdTables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            var tableNameByRef = (document.Tables ?? []).ToDictionary(t => t.LogicalRef, t => t.Name, StringComparer.Ordinal);

            var deferredLookups = new List<DeferredLookup>();
            var deferredSummaries = new List<DeferredSummary>();

            if (roles.Count > 0)
                ImportProgress.Report("Creating roles", 35);
                rolesCreated = await CreateRolesAsync(roles, tableNameByRef, tableByName, appPublicId, skipped, ct);

            if (relationships.Count > 0)
                ImportProgress.Report("Creating relationships", 42);
                relationshipsCreated = await CreateRelationshipsAsync(relationships, tableNameByRef, tableByName, appPublicId, deferredLookups, deferredSummaries, skipped, ct);

            // Source report links that are not a relationship's own, once the reference fields they match on exist.
            // A link that opens (or matches on) a formula or lookup field cannot be made yet: those are created below, so it is retried afterwards.
            var deferredReportLinks = new List<string>();
            if (ImportRuntime.Fids is not null && (document.Tables ?? []).Any(t => (t.ReportLinks ?? []).Count > 0))
            {
                var (linksCreated, linksDeferred) = await CreateStandaloneReportLinksAsync(document, tableNameByRef, tableByName, appPublicId, skipped, ct, final: false);
                fieldsCreated += linksCreated;
                deferredReportLinks = linksDeferred;
            }

            // Formula fields must exist before forms and reports are built: a form element or
            // report column referencing a formula field can only resolve once that field has a
            // real Fid. Running this after CreateFormsAsync silently dropped every such element
            // (confirmed against a real export — 20 elements referencing successfully-created
            // formula fields went missing), so this pass stays ahead of both.
            var formulaCandidates = (document.Tables ?? [])
                .SelectMany(t => (t.Fields ?? []).Where(IsFormulaField).Select(f => (Table: t, Field: f)))
                .ToList();
            ImportProgress.Report("Creating formula fields", 55);
            var (formulasCreated, pendingFormulas) = await CreateFormulaFieldsAsync(formulaCandidates, tableByName, formulaTranslations, notes, ct);
            fieldsCreated += formulasCreated;

            // Lookups, summaries and formulas can depend on each other in a cycle, so keep going
            // while a round still makes progress: each lookup/summary added may unlock formulas,
            // and each formula created may unlock more lookups/summaries.
            for (var round = 0; round < MAX_DEPENDENCY_ROUNDS && (deferredLookups.Count + deferredSummaries.Count + pendingFormulas.Count) > 0; round++)
            {
                var progress = await _deferredResolver.ResolveLookupsAsync(deferredLookups, skipped, ct);
                progress += await _deferredResolver.ResolveSummariesAsync(deferredSummaries, skipped, ct);

                if (pendingFormulas.Count > 0)
                {
                    var (created, stillPending) = await CreateFormulaFieldsAsync(pendingFormulas, tableByName, formulaTranslations, notes, ct);
                    fieldsCreated += created;
                    progress += created;
                    pendingFormulas = stillPending;
                }

                if (progress == 0)
                    break;
            }

            // A formula that still does not compile after every retry is saved anyway - as a formula field, exactly as written in the source,
            // like Quickbase does (it shows the error when the field is opened). Only one PowerBase cannot save at all gets a stand-in below.
            if (pendingFormulas.Count > 0)
            {
                ImportProgress.Report("Saving formulas that do not compile yet", 62);
                var (savedBroken, notSaved) = await CreateBrokenFormulaFieldsAsync(pendingFormulas, tableByName, formulaTranslations, skipped, notes, ct);
                fieldsCreated += savedBroken;
                pendingFormulas = notSaved;
            }

            // Formulas, lookups and summaries exist now: the report links held back earlier can be made (whatever still fails is reported).
            if (deferredReportLinks.Count > 0)
                fieldsCreated += (await CreateStandaloneReportLinksAsync(document, tableNameByRef, tableByName, appPublicId, skipped, ct, onlyRefs: deferredReportLinks, final: true)).Created;

            foreach (var l in deferredLookups)
                skipped.Add(new ImportSkippedItem { LogicalRef = l.Lookup.LogicalRef, Name = l.Lookup.Name, Reason = $"Lookup source field '{l.Lookup.SourceFieldName}' was not created on '{l.ParentTable.Name}'." });
            foreach (var sm in deferredSummaries)
                skipped.Add(new ImportSkippedItem { LogicalRef = sm.Summary.LogicalRef, Name = sm.Summary.Name, Reason = await DescribeBlockedSummaryAsync(sm, ct) });
            foreach (var (_, field) in pendingFormulas)
            {
                var diagnostics = formulaTranslations.FirstOrDefault(t => t.LogicalRef == field.LogicalRef)?.Diagnostics ?? [];
                skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Reason = $"Formula needs manual review: {string.Join("; ", diagnostics)}" });
            }

            fieldsCreated += await CreatePlaceholderFieldsAsync(document, relationships, tableNameByRef, tableByName, skipped, formulaTranslations, notes, ct);

            // Every field now exists, so the table's default columns, dynamic filters and sort can name their real Fids.
            await ApplyDefaultReportSettingsAsync(document, tableByName, skipped, ct);

            // Select fields whose choices come from another field (ChoiceSourceSettings) need the OTHER field's real
            // TableId/Fid, which also only exist now.
            await ApplyChoiceSourcesAsync(document, tableByName, skipped, ct);

            ImportProgress.Report("Creating reports", 68);
            // Role refs → the roles this app has now, for report visibility.
            var roleIdsByRef = await ResolveRoleIdsByRefAsync(document, appPublicId, ct);

            // Reports go in before forms: an embedded related-records grid on a form points at a report that must already exist.
            var createdReports = new Dictionary<string, CreatedReport>(StringComparer.Ordinal);
            AppTableAliasSchema? reportFormulaAliasSchema = null;

            foreach (var table in document.Tables ?? [])
            {
                if (!tableByName.TryGetValue(table.Name, out var createdTable))
                    continue; // defensive: every PBL table was just created by CreateAppCommandHandler

                var pblReports = table.Reports ?? [];
                if (pblReports.Count == 0)
                    continue;

                var allFields = await _fieldRepo.ListByTableAsync(createdTable.Id, ct);
                var fidByName = FidByNameOrLabel(allFields);

                var tableHasDefaultReport = tablesKeepingDefaults?.Contains(table.Name) == true;

                foreach (var report in pblReports)
                {
                    // Report-level formula columns (source: QB::Report::Formula::*) are scoped to this one report —
                    // translate them now (needs the table's real fields, like any Formula field) and give each a
                    // reserved Fid (ReportFormulaFields.FirstId..+2) so the Columns/sort resolution below picks them
                    // up exactly like a real field. A formula that fails to translate or compiles to a type report
                    // formulas can't hold is left out of the name map — it then falls through to the same
                    // "column whose field was not created" handling as any other missing column.
                    var reportFormulas = new List<ReportFormulaCommand>();
                    var reportFidByName = fidByName;
                    if (report.ReportFormulas.Count > 0)
                    {
                        reportFidByName = new Dictionary<string, long>(fidByName, StringComparer.Ordinal);
                        reportFormulaAliasSchema ??= await AppTableAliasSchema.BuildAsync(_tableRepo, createdTable.AppId, ct);

                        foreach (var formula in report.ReportFormulas.Take(ReportFormulaFields.MaxFormulas))
                        {
                            FormulaTranslationResult translation;
                            try
                            {
                                translation = _formulaTranslator.Translate(formula.ResultType, formula.Expression, allFields, reportFormulaAliasSchema);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                translation = new FormulaTranslationResult { Status = FormulaTranslationStatus.NeedsManualReview, Diagnostics = [$"The formula could not be analysed: {ex.Message}"] };
                            }

                            if (translation.Status == FormulaTranslationStatus.NeedsManualReview ||
                                !ReportFormulaFields.IsAllowedTypeCode(translation.TypeCode) ||
                                translation.TypeCode == "Formula_Number" && formula.DisplayAs is not null && !ReportFormulaFields.IsAllowedDisplayAs(formula.DisplayAs))
                            {
                                skipped.Add(new ImportSkippedItem
                                {
                                    LogicalRef = report.LogicalRef,
                                    Name = formula.Name,
                                    Reason = $"Report formula column '{formula.Name}' on report '{report.Name}' was not carried: {(translation.Diagnostics.Count > 0 ? string.Join(" ", translation.Diagnostics) : "its result type has no PowerBase report-formula equivalent")}.",
                                });
                                continue;
                            }

                            var id = ReportFormulaFields.FirstId + reportFormulas.Count;
                            reportFormulas.Add(new ReportFormulaCommand(id, formula.Name, translation.TypeCode!, formula.DisplayAs, TranslatedExpression(translation) ?? formula.Expression));
                            reportFidByName[formula.Name] = id;
                        }
                    }

                    // A column pointing at a field that didn't make it (a formula that failed
                    // to translate, an unsupported type) costs that column, not the whole
                    // report. Dropping the report outright would be badly disproportionate:
                    // "List All" means "every field in the table", so a single failed formula
                    // anywhere would take the table's main view down with it — and with the
                    // seeded default views suppressed for imported tables, that can leave a
                    // table with no reports at all.
                    var unresolved = report.Columns.Where(c => !reportFidByName.ContainsKey(c))
                        .Concat(report.SortFields.Select(s => s.FieldName).Where(n => !reportFidByName.ContainsKey(n)))
                        .Distinct()
                        .ToList();

                    var columns = report.Columns.Where(reportFidByName.ContainsKey).Select(c => reportFidByName[c]).ToList();
                    var sortFields = report.SortFields
                        .Where(s => reportFidByName.ContainsKey(s.FieldName))
                        .Select(s => new SortSpec { FieldId = reportFidByName[s.FieldName], Desc = s.Desc })
                        .ToList();

                    // Nothing left to show — now the report really is meaningless.
                    if (columns.Count == 0 && report.Columns.Count > 0)
                    {
                        skipped.Add(new ImportSkippedItem
                        {
                            LogicalRef = report.LogicalRef,
                            Name = report.Name,
                            Reason = $"Report references no field that was created: {string.Join(", ", unresolved)}.",
                        });
                        continue;
                    }

                    if (unresolved.Count > 0)
                    {
                        skipped.Add(new ImportSkippedItem
                        {
                            LogicalRef = report.LogicalRef,
                            Name = report.Name,
                            Reason = $"Report imported without column(s) whose field was not created: {string.Join(", ", unresolved)}.",
                        });
                    }

                    var createdReport = await CreateReportAsync(report, createdTable, allFields, columns, sortFields, roleIdsByRef, reportFormulas, skipped, ct);
                    if (createdReport is null)
                        continue;

                    // Same reasoning as forms: reports are created IsDefault=false and the seeded
                    // default was suppressed, so the first one that imports for a table becomes it.
                    // Only a report everyone can see may become the table's default.
                    var visibleToAll = report.VisibleToRoleRefs is null || roleIdsByRef.Count == 0 || report.VisibleToRoleRefs.Count(roleIdsByRef.ContainsKey) >= roleIdsByRef.Count;
                    if (!tableHasDefaultReport && visibleToAll)
                    {
                        await _reportRepo.SetDefaultAsync(createdTable.PublicId, createdReport.Id, ct);
                        tableHasDefaultReport = true;
                    }

                    reportsCreated++;
                    createdReports[report.LogicalRef] = new CreatedReport(createdReport.Id, report.Name, createdTable.PublicId);
                }

                // Every table needs a default report everyone can open. When all of this table's reports are restricted to some roles,
                // none may become the default (see above), so PowerBase's own "List All" is added instead of leaving the table without one.
                if (!tableHasDefaultReport)
                {
                    var taken = pblReports.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var name = "List All";
                    for (var n = 2; taken.Contains(name); n++)
                        name = $"List All ({n})";
                    await _reportRepo.CreateAsync(new Report
                    {
                        AppTableId = createdTable.Id,
                        OwnerId = createdTable.CreatedBy,
                        Name = name,
                        ReportType = "Table",
                        Visibility = "Shared",
                        Definition = JsonSerializer.Serialize(new ReportDefinition()),
                        IsDefault = true,
                        DisplayOrder = 1,
                    }, ct);
                    tableHasDefaultReport = true;
                    skipped.Add(new ImportSkippedItem { LogicalRef = table.LogicalRef, Name = table.Name,
                        Reason = $"Every report of '{table.Name}' is limited to some roles, so a default report '{name}' (all fields, everyone) was added." });
                }
            }

            if (forms.Count > 0)
            {
                ImportProgress.Report("Creating forms and rules", 80);
                var createdFormLayouts = await CreateFormsAsync(forms, tableNameByRef, tableByName, tablesKeepingDefaults, createdReports, relationships, skipped, ct);
                formsCreated = createdFormLayouts.Count;
                await ApplyFormRoleDefaultsAsync(document, createdFormLayouts, tableNameByRef, tableByName, roleIdsByRef, skipped, ct);

                if (forms.Any(f => f.Rules.Count > 0))
                    formRulesCreated = await CreateFormRulesAsync(forms, createdFormLayouts, tableNameByRef, tableByName, skipped, ct);
            }

            // Dashboards point at reports, so they come after the reports (and charts) exist.
            if ((document.Dashboards ?? []).Count > 0)
            {
                ImportProgress.Report("Creating dashboards", 85);
                dashboardsCreated = await _extrasImporter.CreateDashboardsAsync(
                    document.Dashboards!, appPublicId, createdReports.ToDictionary(kv => kv.Key, kv => kv.Value.PublicId, StringComparer.Ordinal), skipped, ct);
            }
        }


        // Role switches, record filters and field-level access need every field to exist.
        ImportProgress.Report("Applying permissions", 88);
        await _permissionsApplier.ApplyAsync(document, appPublicId, skipped, ct);

        ImportProgress.Report("Creating pages", 90);
        var pagesCreated = await _extrasImporter.CreatePagesAsync(document.Pages ?? [], appPublicId, skipped, ct) + dashboardsCreated;

        return new PblApplyResult(fieldsCreated, reportsCreated, relationshipsCreated, formsCreated, formRulesCreated, rolesCreated, variablesCreated, pagesCreated);
    }

    /// <summary>
    /// One scalar field's creation item. Settings/flags the target type rejects are dropped individually and reported
    /// (see <see cref="ImportFieldSanitizer"/>) so a single bad value never fails the table. Unique is applied afterwards
    /// (it needs a physical index), see <see cref="PblSettingsApplier"/>.
    /// </summary>
    public BulkCreateFieldItem BuildScalarItem(PblField field, List<ImportSkippedItem> skipped, List<ImportFieldNoteDraft>? notes = null, string? tableName = null)
    {
        var label = (field.Label ?? field.Name).Trim();
        var clean = _fieldSanitizer.Sanitize(field.TypeCode, label, field.Settings, field.DefaultValue, field.IsRequired, field.IsUnique == true);
        foreach (var note in clean.Notes)
            skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Reason = note });

        // A label or description longer than PowerBase holds would be rejected, and with it every other field of the table. It is cut
        // instead — and the complete text stays in the field's import note.
        var description = field.Description;
        var fullText = new List<string>();
        if (label.Length > ImportFieldSanitizer.LABEL_MAX)
        {
            fullText.Add($"Label: {label}");
            label = label[..(ImportFieldSanitizer.LABEL_MAX - 1)].TrimEnd() + "…";
            skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Code = "IMPORT_FIELD_TEXT_SHORTENED", Reason = $"The label was longer than {ImportFieldSanitizer.LABEL_MAX} characters and was shortened; the complete label is kept in the field's import note." });
        }
        if (description is { Length: > ImportFieldSanitizer.DESCRIPTION_MAX })
        {
            fullText.Add($"Description: {description}");
            description = description[..(ImportFieldSanitizer.DESCRIPTION_MAX - 1)] + "…";
            skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Code = "IMPORT_FIELD_TEXT_SHORTENED", Reason = $"The description was longer than {ImportFieldSanitizer.DESCRIPTION_MAX} characters and was shortened; the complete text is kept in the field's import note." });
        }
        if (PowerBase.Formula.Binding.FieldIdReference.IsReservedLabel(label))
        {
            skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Code = "IMPORT_FIELD_TEXT_SHORTENED", Reason = $"The label '{label}' is reserved for referencing a field by its id in formulas; imported as '{label} (imported)'." });
            fullText.Add($"Label: {label}");
            label += " (imported)";
        }
        if (fullText.Count > 0 && notes is not null && tableName is not null)
            notes.Add(new ImportFieldNoteDraft
            {
                TableName = tableName,
                FieldName = field.Name,
                FieldLabel = label,
                SourceFid = field.SourceId,
                Severity = ImportNoteSeverities.Information,
                Code = "IMPORT_FIELD_TEXT_SHORTENED",
                Message = "Part of the label or description was longer than PowerBase holds and was shortened; the complete original text is kept here.",
                SourceLogicalRef = field.LogicalRef,
                SourceType = field.SourceType,
                OriginalDefinition = string.Join(Environment.NewLine, fullText),
            });

        return new BulkCreateFieldItem(
            field.TypeCode, label, description, clean.IsRequired, field.IsAuditable == true, clean.Settings, clean.DefaultValue,
            IsEncrypted: true, Name: field.Name, HelpText: field.HelpText, IsSearchable: field.IsSearchable, IsReportable: field.IsReportable);
    }

    /// <summary>Final PowerBase type code and settings JSON for a formula: the source type can force Url/Email/Phone/RichText
    /// when the expression is text; the source display options are merged with the translated expression (a URL formula keeps it
    /// as <c>template</c>); display options the chosen type does not accept are dropped, never the expression.</summary>
    private (string TypeCode, string Settings) BuildFormulaSettings(PblField field, FormulaTranslationResult translation, List<string> diagnostics)
    {
        var typeCode = translation.TypeCode ?? "Formula_Text";
        if (typeCode == "Formula_Text" && !string.IsNullOrEmpty(field.FormulaVariant))
            typeCode = "Formula_" + field.FormulaVariant;

        string? expression = null;
        if (translation.SettingsJson is not null)
        {
            using var doc = JsonDocument.Parse(translation.SettingsJson);
            expression = doc.RootElement.GetProperty("expression").GetString();
        }

        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(field.Settings))
        {
            foreach (var prop in JsonDocument.Parse(field.Settings).RootElement.EnumerateObject())
                settings[prop.Name] = prop.Value.Clone();
        }
        settings[typeCode == "Formula_Url" ? "template" : "expression"] = expression;

        var withDisplay = JsonSerializer.Serialize(settings);
        if (_fieldSanitizer.Sanitize(typeCode, field.Name, withDisplay, null, false, false).Settings is not null)
            return (typeCode, withDisplay);

        // Display options the type rejects: keep the formula itself.
        diagnostics.Add("Some source display options were not applied to this formula field.");
        var expressionOnly = new Dictionary<string, object?> { [typeCode == "Formula_Url" ? "template" : "expression"] = expression };
        return (typeCode, JsonSerializer.Serialize(expressionOnly));
    }

    /// <summary>The translated PowerBase-syntax expression text out of a successful <see cref="FormulaTranslationResult"/> —
    /// same extraction <see cref="BuildFormulaSettings"/> does for a real Formula field's Settings JSON.</summary>
    private static string? TranslatedExpression(FormulaTranslationResult translation)
    {
        if (translation.SettingsJson is null) return null;
        using var doc = JsonDocument.Parse(translation.SettingsJson);
        return doc.RootElement.TryGetProperty("expression", out var expr) ? expr.GetString() : null;
    }

    private async Task<Dictionary<string, Guid>> ResolveRoleIdsByRefAsync(PblDocument document, Guid appPublicId, CancellationToken ct)
    {
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if ((document.Roles ?? []).Count == 0)
            return result;

        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var byName = (await _appRoleRepo.ListDetailsByAppIdAsync(appId, ct)).ToDictionary(r => r.Name, r => r.PublicId, StringComparer.OrdinalIgnoreCase);
        foreach (var role in document.Roles!)
            if (byName.TryGetValue(role.Name, out var id))
                result[role.LogicalRef] = id;
        return result;
    }

    /// <summary>
    /// Creates one report with everything the source said (filter, grouping, options, role visibility). PowerBase validates a report as a
    /// whole, so if it rejects the full definition the report is retried with just columns and sort — and the reason is reported — rather
    /// than losing the report or failing the import. A report restricted to roles is never created visible to everyone: if its roles
    /// cannot be resolved it is created private to the importer.
    /// </summary>
    private async Task<ReportDetailResult?> CreateReportAsync(
        PblReport report, AppTable table, IReadOnlyList<AppField> fields, List<long> columns, List<SortSpec> sortFields,
        Dictionary<string, Guid> roleIdsByRef, List<ReportFormulaCommand> reportFormulas, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        // The report's own formulas are not table fields, but its filter and grouping name them: give them their reserved ids here so those
        // names resolve exactly like a field's (the same virtual fields the report runs against).
        var virtualFormulaFields = ReportFormulaFields.Build(
            reportFormulas.Select(f => new ReportFormula { Id = f.Id, Name = f.Name, TypeCode = f.TypeCode, DisplayAs = f.DisplayAs, Expression = f.Expression }), table.Id);
        if (virtualFormulaFields.Count > 0)
            fields = [.. fields, .. virtualFormulaFields];

        var byName = FieldFids.ByNameOrLabel(fields);

        FilterGroup? filter = null;
        string? filterProblem = null;
        if (report.Filter is not null)
            filter = PblPermissionsApplier.ToFilterGroup(report.Filter, fields, out filterProblem);
        if (report.Filter is not null && filter is null)
            skipped.Add(new ImportSkippedItem { LogicalRef = report.LogicalRef, Name = report.Name, Reason = $"Report filter not applied: {filterProblem ?? "no usable condition"}." });

        // Grouping: when any level groups, the whole ordered list goes through the sort-and-group levels.
        List<SortGroupLevelCommand>? tableSortGroup = null;
        if (report.ReportType == "Table" && report.SortFields.Any(s => s.IsGroup))
        {
            tableSortGroup = [];
            foreach (var level in report.SortFields)
            {
                if (!byName.TryGetValue(level.FieldName, out var fid)) continue;
                var fieldType = fields.First(f => f.Fid == fid).TypeCode;
                var mode = level.IsGroup ? level.GroupMode ?? "EqualValues" : "EqualValues";
                if (!GroupByModeCategoryHelper.GetAllowedGroupByModes(fieldType).Contains(mode, StringComparer.OrdinalIgnoreCase))
                    mode = "EqualValues";
                tableSortGroup.Add(new SortGroupLevelCommand(fid, level.Desc, level.IsGroup, mode));
            }
        }

        // Summary reports: "Rows" group levels, summarize-data columns and the crosstab column. PowerBase needs at least one group, so a report
        // with none that can be created is grouped by Record ID# (and says so) instead of being lost.
        List<RowGroupLevelCommand>? rowGroupLevels = null;
        List<RowGroupLevelCommand>? recordIdGroup = null;
        var aggregations = new List<SummaryAggregationCommand>();
        ChartConfigCommand? crosstab = null;
        if (report.ReportType == "Summary")
        {
            RowGroupLevelCommand? Level(PblSummaryGroup g)
            {
                if (!byName.TryGetValue(g.FieldName, out var fid)) return null;
                var type = fields.First(f => f.Fid == fid).TypeCode;
                var mode = GroupByModeCategoryHelper.GetAllowedGroupByModes(type).Contains(g.GroupMode, StringComparer.OrdinalIgnoreCase) ? g.GroupMode : "EqualValues";
                return new RowGroupLevelCommand(fid, mode);
            }

            var recordId = fields.FirstOrDefault(f => f.Fid == 3);
            recordIdGroup = recordId?.Fid is { } rid ? [new RowGroupLevelCommand(rid, "EqualValues")] : null;

            rowGroupLevels = report.SummaryRows.Select(Level).OfType<RowGroupLevelCommand>().ToList();
            if (rowGroupLevels.Count == 0 && recordIdGroup is not null)
            {
                rowGroupLevels = recordIdGroup;
                skipped.Add(new ImportSkippedItem { LogicalRef = report.LogicalRef, Name = report.Name,
                    Reason = "Summary report has no group that could be created; it is grouped by Record ID# so it exists and can be repaired." });
            }

            foreach (var agg in report.Aggregations)
                if (byName.TryGetValue(agg.FieldName, out var aggFid))
                    aggregations.Add(new SummaryAggregationCommand(aggFid, agg.Function, agg.DisplayAs));

            if (report.SummaryCrosstab is { } crosstabGroup && Level(crosstabGroup) is { } ctLevel)
                crosstab = new ChartConfigCommand(
                    "Bar", ctLevel.FieldId, ctLevel.GroupByMode, null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);
        }

        // Chart reports: the category (what the chart is grouped by), an optional series, the measured values and the gauge field.
        long? chartGroupBy = null;
        var chartGroupMode = "EqualValues";
        ChartConfigCommand? chartConfig = null;
        if (report.ReportType == "Chart" && report.Chart is { } pc)
        {
            (long Fid, string Mode)? Resolve(PblSummaryGroup? g)
            {
                if (g is null || !byName.TryGetValue(g.FieldName, out var fid)) return null;
                var type = fields.First(f => f.Fid == fid).TypeCode;
                var mode = GroupByModeCategoryHelper.GetAllowedGroupByModes(type).Contains(g.GroupMode, StringComparer.OrdinalIgnoreCase) ? g.GroupMode : "EqualValues";
                return (fid, mode);
            }

            long? gaugeFid = null;
            if (pc.ChartType == "Gauge")
            {
                gaugeFid = pc.GaugeFieldName is { } gn && byName.TryGetValue(gn, out var gf) ? gf : null;
                chartGroupBy = gaugeFid;
            }
            else if (Resolve(pc.Category) is { } cat)
            {
                chartGroupBy = cat.Fid;
                chartGroupMode = cat.Mode;
            }

            var series = pc.ChartType == "Pie" || pc.ChartType == "Gauge" ? null : Resolve(pc.Series);
            foreach (var agg in report.Aggregations)
                if (byName.TryGetValue(agg.FieldName, out var aggFid))
                    aggregations.Add(new SummaryAggregationCommand(aggFid, agg.Function, agg.DisplayAs));
            // A chart with a series can only show one measured value.
            if (series is not null && aggregations.Count > 1)
                aggregations = aggregations.Take(1).ToList();

            chartConfig = new ChartConfigCommand(
                pc.ChartType, series?.Fid, series?.Mode ?? "EqualValues", null, null, null, null, false, pc.SortBy, pc.SortDirection,
                pc.GoalValue, pc.GoalLabel, pc.DataLabelsVisible, false, null,
                GaugeFieldId: gaugeFid, GaugeFunction: pc.ChartType == "Gauge" ? pc.GaugeFunction : null);
        }

        // Visibility from the source's per-role "listed" switches.
        var visibility = "Shared";
        List<Guid>? visibleRoles = null;
        if (report.VisibleToRoleRefs is { } roleRefs)
        {
            var resolved = roleRefs.Where(roleIdsByRef.ContainsKey).Select(r => roleIdsByRef[r]).Distinct().ToList();
            if (resolved.Count == 0)
            {
                visibility = "Personal";
            }
            else if (resolved.Count < roleIdsByRef.Count)
            {
                visibility = "SpecificRoles";
                visibleRoles = resolved;
            }
        }

        var dynamicFields = report.DynamicFilterFieldNames.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
        var options = new ReportOptionsCommand(
            ColumnHeaderText: report.ColumnHeaderText ?? "Default",
            ShowEditIcon: report.ShowEditIcon ?? true,
            ShowViewIcon: report.ShowViewIcon ?? true,
            ShowQuickPeekIcon: true,
            DisableBulkDelete: report.DisableBulkDelete ?? false,
            DisableBulkUpdate: false,
            DisableGridEdit: report.DisableGridEdit ?? false,
            ShowDescriptionOnReportPage: report.ShowDescription);

        CreateReportCommand Command(bool minimal) => new(
            table.PublicId,
            report.Name,
            Description: report.Description,
            Visibility: minimal ? (visibility == "Personal" ? "Personal" : "Shared") : visibility,
            ReportType: report.ReportType,
            Columns: report.ReportType is "Summary" or "Chart" ? [] : columns,
            SortFields: report.ReportType is "Summary" or "Chart" ? [] : sortFields,
            FilterTree: minimal ? null : filter,
            GroupByFieldId: report.ReportType == "Chart" ? chartGroupBy : null,
            GroupByMode: report.ReportType == "Chart" ? chartGroupMode : "EqualValues",
            HideTotals: !minimal && report.HideTotals == true,
            GroupDefaultCollapsed: minimal ? false : report.GroupDefaultCollapsed ?? false,
            GroupByDescending: false,
            Aggregations: minimal && report.ReportType != "Chart" ? [] : aggregations,
            DynamicFilterType: !minimal && report.DynamicFilterType == "Custom" && dynamicFields.Count > 0 ? "Custom" : !minimal && report.DynamicFilterType == "None" ? "None" : "Default",
            CustomDynamicFilterFields: minimal ? [] : dynamicFields,
            CustomDynamicFilterItems: null,
            AllowQuickSearch: minimal || report.AllowQuickSearch != false,
            VisibleToRoleIds: minimal ? null : visibleRoles,
            Chart: report.ReportType == "Chart" ? chartConfig : minimal ? null : crosstab,
            Options: minimal || report.ReportType is "Summary" or "Chart" ? null : options,
            TableSortGroup: minimal ? null : tableSortGroup,
            RowGroupLevels: report.ReportType == "Summary" ? (minimal ? recordIdGroup ?? rowGroupLevels : rowGroupLevels) : null,
            ReportFormulas: report.ReportType == "Table" && reportFormulas.Count > 0 ? reportFormulas : null);

        try
        {
            return await _createReportHandler.HandleAsync(Command(minimal: false), ct);
        }
        catch (DomainException ex)
        {
            try
            {
                var created = await _createReportHandler.HandleAsync(Command(minimal: true), ct);
                skipped.Add(new ImportSkippedItem { LogicalRef = report.LogicalRef, Name = report.Name,
                    Reason = $"Report imported with columns and sort only; its filter, grouping and options were rejected: {Describe(ex)}" });
                return created;
            }
            catch (DomainException retryEx)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = report.LogicalRef, Name = report.Name, Reason = $"Report not created: {Describe(retryEx)}" });
                return null;
            }
        }
    }

    private static string Describe(DomainException ex) =>
        ex is ValidationException ve ? string.Join(" ", ve.Errors.SelectMany(e => e.Value)) : ex.Message;

    /// <summary>Translates and creates every table's formula fields, returning how many were
    /// created. Runs after relationships (a formula can reference a Lookup/Summary field) but
    /// before forms and reports (both can reference a formula field). A formula that fails to
    /// compile against the table's real schema is reported and skipped — never created with a
    /// broken expression.</summary>
    private async Task<(int Created, List<(PblTable Table, PblField Field)> StillPending)> CreateFormulaFieldsAsync(
        IReadOnlyList<(PblTable Table, PblField Field)> candidates,
        Dictionary<string, AppTable> tableByName,
        List<FormulaTranslationReportItem> formulaTranslations,
        List<ImportFieldNoteDraft> notes,
        CancellationToken ct)
    {
        var created = 0;
        var stillPending = new List<(PblTable Table, PblField Field)>();

        AppTableAliasSchema? aliasSchema = null;

        foreach (var tableGroup in candidates.GroupBy(c => c.Table))
        {
            var table = tableGroup.Key;
            if (!tableByName.TryGetValue(table.Name, out var createdTable))
                continue; // defensive: every PBL table was just created by CreateAppCommandHandler

            var formulaFields = tableGroup.Select(c => c.Field).ToList();

            var currentFields = await _fieldRepo.ListByTableAsync(createdTable.Id, ct);
            var formulaItems = new List<(PblField Field, BulkCreateFieldItem Item, FormulaTranslationResult Translation)>();

            // PowerBase requires a unique label per table (case-insensitive); the source file can
            // carry a formula whose label matches a field that already exists (confirmed in a real
            // export). One such clash used to reject the whole batch and fail the entire import, so
            // the later field gets a numbered label instead — and the report says so.
            var usedLabels = currentFields
                .Select(f => (f.Label ?? "").Trim())
                .Where(l => l.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var field in formulaFields)
            {
                aliasSchema ??= await AppTableAliasSchema.BuildAsync(_tableRepo, createdTable.AppId, ct);
                FormulaTranslationResult translation;
                try
                {
                    translation = _formulaTranslator.Translate(field.ResultType, field.FormulaExpression, currentFields, aliasSchema);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A formula the engine chokes on costs only that field.
                    translation = new FormulaTranslationResult
                    {
                        Status = FormulaTranslationStatus.NeedsManualReview,
                        Diagnostics = [$"The formula could not be analysed: {ex.Message}"],
                    };
                }

                var reportItem = new FormulaTranslationReportItem
                {
                    LogicalRef = field.LogicalRef,
                    Name = field.Name,
                    Status = translation.Status.ToString(),
                    Diagnostics = translation.Diagnostics.ToList(),
                };
                // A retry replaces the earlier attempt's entry so the report has one row per formula.
                formulaTranslations.RemoveAll(t => t.LogicalRef == field.LogicalRef);
                formulaTranslations.Add(reportItem);

                if (translation.Status == FormulaTranslationStatus.NeedsManualReview)
                {
                    // May compile once the lookups/summaries/formulas it refers to exist - retried later.
                    stillPending.Add((table, field));
                    continue;
                }

                // Preserve the imported file's exact Name (its stable third-party identifier) rather
                // than regenerating one — see BulkCreateFieldItem.Name.
                var label = UniqueLabel((field.Label ?? field.Name).Trim(), usedLabels);
                if (!string.Equals(label, (field.Label ?? field.Name).Trim(), StringComparison.Ordinal))
                    reportItem.Diagnostics.Add($"Label '{(field.Label ?? field.Name).Trim()}' is already used by another field on this table; imported as '{label}'.");

                var (formulaTypeCode, formulaSettings) = BuildFormulaSettings(field, translation, reportItem.Diagnostics);
                var description = field.Description;
                if (description is { Length: > ImportFieldSanitizer.DESCRIPTION_MAX })
                {
                    // Too long for a field description (it would be refused, and with it the whole batch): cut, the full text goes to the note.
                    notes.Add(new ImportFieldNoteDraft
                    {
                        TableName = table.Name, FieldName = field.Name, FieldLabel = label, SourceFid = KeptFid(field),
                        Severity = ImportNoteSeverities.Information, Code = "IMPORT_FIELD_TEXT_SHORTENED",
                        Message = "The description was longer than PowerBase holds and was shortened; the complete original text is kept here.",
                        SourceLogicalRef = field.LogicalRef, SourceType = field.SourceType, OriginalDefinition = $"Description: {description}",
                    });
                    description = description[..(ImportFieldSanitizer.DESCRIPTION_MAX - 1)] + "…";
                }
                formulaItems.Add((field, new BulkCreateFieldItem(
                    formulaTypeCode, label, description, IsRequired: false, IsAuditable: field.IsAuditable == true,
                    Settings: formulaSettings, Name: field.Name, HelpText: field.HelpText,
                    IsSearchable: field.IsSearchable, IsReportable: field.IsReportable, Fid: KeptFid(field)), translation));
            }

            if (formulaItems.Count > 0)
            {
                var (ok, failed) = await CreateFormulaBatchAsync(createdTable, formulaItems.Select(i => (i.Field, i.Item)).ToList(), ct);
                created += ok.Count;

                foreach (var (field, error) in failed)
                {
                    // It compiled but could not be saved: retried with the others, and a stand-in field at the end if it never works.
                    formulaTranslations.FirstOrDefault(t => t.LogicalRef == field.LogicalRef)?.Diagnostics.Add($"The formula field could not be created: {error}");
                    stillPending.Add((table, field));
                }

                // A formula that only compiled after PowerBase rewrote it keeps its original text in an import note.
                foreach (var (field, item, translation) in formulaItems.Where(i => ok.Contains(i.Field) && i.Translation.Status == FormulaTranslationStatus.Adjusted))
                {
                    var converted = ConvertedExpression(translation);
                    var original = field.OriginalFormula ?? field.FormulaExpression;
                    if (string.IsNullOrEmpty(original) || string.Equals(original, converted, StringComparison.Ordinal)) continue;
                    notes.Add(new ImportFieldNoteDraft
                    {
                        TableName = table.Name, FieldName = field.Name, FieldLabel = item.Label, SourceFid = KeptFid(field),
                        Severity = ImportNoteSeverities.Information, Code = "FORMULA_ADJUSTED",
                        Message = "The formula was rewritten so PowerBase accepts it (Quickbase allows constructs PowerBase does not). The original text is kept here.",
                        SourceLogicalRef = field.LogicalRef, SourceType = field.SourceType,
                        OriginalDefinition = original,
                        OriginalMetadata = JsonSerializer.Serialize(new { kind = "Formula", sourceType = field.SourceType, resultType = field.ResultType, convertedExpression = converted }, NoteJson),
                    });
                }
            }
        }

        return (created, stillPending);
    }

    private static string? ConvertedExpression(FormulaTranslationResult translation)
    {
        if (translation.SettingsJson is null) return null;
        using var doc = JsonDocument.Parse(translation.SettingsJson);
        return doc.RootElement.TryGetProperty("expression", out var e) ? e.GetString() : null;
    }

    /// <summary>Creates a table's formula fields: the whole batch first (the usual case); if anything in it fails, one at a time, so a
    /// field PowerBase will not save costs only itself, never the other formulas and never the app.</summary>
    private async Task<(List<PblField> Created, List<(PblField Field, string Error)> Failed)> CreateFormulaBatchAsync(
        AppTable table, List<(PblField Field, BulkCreateFieldItem Item)> items, CancellationToken ct)
    {
        try
        {
            await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(table.PublicId, items.Select(i => i.Item).ToList()), ct);
            return (items.Select(i => i.Field).ToList(), []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // fall through: part of the batch may exist already
        }

        var created = new List<PblField>();
        var failed = new List<(PblField, string)>();
        foreach (var (field, item) in items)
        {
            try
            {
                var current = await _fieldRepo.ListByTableAsync(table.Id, ct);
                if (!current.Any(f => string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase)))
                    await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(table.PublicId, [item]), ct);
                created.Add(field);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add((field, ex is DomainException de ? Describe(de) : ex.Message));
            }
        }
        return (created, failed);
    }

    /// <summary>Saves the formulas that never compiled as formula fields of their result type, with the expression exactly as the source had it.
    /// PowerBase evaluates a formula that does not compile to blank (it never breaks a listing) and shows the error when the field is opened,
    /// the way Quickbase does. Every formula is compiled once more against the finished table, so one that only failed because it reads another
    /// broken formula (which now exists) is not reported. The rest get an audit-history note with the reason in plain words. A formula PowerBase
    /// cannot save even so is handed back, and gets a stand-in field.</summary>
    private async Task<(int Saved, List<(PblTable Table, PblField Field)> NotSaved)> CreateBrokenFormulaFieldsAsync(
        IReadOnlyList<(PblTable Table, PblField Field)> pending,
        Dictionary<string, AppTable> tableByName,
        List<FormulaTranslationReportItem> formulaTranslations,
        List<ImportSkippedItem> skipped,
        List<ImportFieldNoteDraft> notes,
        CancellationToken ct)
    {
        var saved = 0;
        var notSaved = new List<(PblTable Table, PblField Field)>();

        foreach (var tableGroup in pending.GroupBy(p => p.Table))
        {
            var table = tableGroup.Key;
            if (!tableByName.TryGetValue(table.Name, out var createdTable))
            {
                notSaved.AddRange(tableGroup);
                continue;
            }

            var existing = await _fieldRepo.ListByTableAsync(createdTable.Id, ct);
            var usedLabels = existing.Select(f => (f.Label ?? "").Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usedNames = existing.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var items = new List<(PblField Field, BulkCreateFieldItem Item)>();
            foreach (var (_, field) in tableGroup)
            {
                if (usedNames.Contains(field.Name)) continue; // it exists already (created after all)

                var expected = FormulaTypeMap.ParseExpected(field.ResultType)?.ToString() ?? nameof(PowerBase.Formula.Types.FormulaType.Text);
                var typeCode = FormulaTranslator.VariantTypeCode(expected);
                var asWritten = new FormulaTranslationResult
                {
                    Status = FormulaTranslationStatus.NeedsManualReview,
                    TypeCode = typeCode,
                    SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string?> { ["expression"] = field.FormulaExpression }),
                };
                var diagnostics = formulaTranslations.FirstOrDefault(t => t.LogicalRef == field.LogicalRef)?.Diagnostics ?? [];
                var (formulaTypeCode, formulaSettings) = BuildFormulaSettings(field, asWritten, diagnostics);

                var description = field.Description is { Length: > ImportFieldSanitizer.DESCRIPTION_MAX } d
                    ? d[..(ImportFieldSanitizer.DESCRIPTION_MAX - 1)] + "…" : field.Description;
                var label = UniqueLabel((field.Label ?? field.Name).Trim(), usedLabels);
                items.Add((field, new BulkCreateFieldItem(
                    formulaTypeCode, label, description, IsRequired: false, IsAuditable: field.IsAuditable == true,
                    Settings: formulaSettings, Name: field.Name, HelpText: field.HelpText,
                    IsSearchable: field.IsSearchable, IsReportable: field.IsReportable, Fid: KeptFid(field))));
            }
            if (items.Count == 0) continue;

            var (ok, failed) = await CreateFormulaBatchAsync(createdTable, items, ct);
            saved += ok.Count;
            foreach (var (field, error) in failed)
            {
                formulaTranslations.FirstOrDefault(t => t.LogicalRef == field.LogicalRef)?.Diagnostics.Add($"The formula field could not be saved: {error}");
                notSaved.Add((table, field));
            }

            // Compile again now that every field exists: some only failed because they read another formula that was missing then.
            var current = await _fieldRepo.ListByTableAsync(createdTable.Id, ct);
            AppTableAliasSchema? aliasSchema = null;
            foreach (var field in ok)
            {
                aliasSchema ??= await AppTableAliasSchema.BuildAsync(_tableRepo, createdTable.AppId, ct);
                FormulaTranslationResult again;
                try { again = _formulaTranslator.Translate(field.ResultType, field.FormulaExpression, current, aliasSchema); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    again = new FormulaTranslationResult { Status = FormulaTranslationStatus.NeedsManualReview, Diagnostics = [$"The formula could not be analysed: {ex.Message}"] };
                }

                var report = formulaTranslations.FirstOrDefault(t => t.LogicalRef == field.LogicalRef);
                if (again.Status == FormulaTranslationStatus.Clean)
                {
                    if (report is not null) { formulaTranslations.Remove(report); formulaTranslations.Add(new FormulaTranslationReportItem { LogicalRef = field.LogicalRef, Name = field.Name, Status = nameof(FormulaTranslationStatus.Clean) }); }
                    continue;
                }

                var problems = again.Status == FormulaTranslationStatus.NeedsManualReview && again.Diagnostics.Count > 0
                    ? again.Diagnostics.ToList()
                    : ["It uses Quickbase syntax PowerBase does not accept as written."];
                report?.Diagnostics.Clear();
                report?.Diagnostics.AddRange(problems);
                report?.Diagnostics.Add("The formula was saved as a formula field, as written, so it can be fixed.");

                var friendly = ImportFieldNoteBuilder.FriendlyDiagnostics(problems);
                var message = $"The formula could not be compiled: {friendly}. It was saved as a formula field exactly as written in Quickbase; its value stays empty until the formula is fixed.";
                skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Code = ImportFieldNoteBuilder.BROKEN_FORMULA_CODE, Reason = message });
                notes.Add(new ImportFieldNoteDraft
                {
                    TableName = table.Name, FieldName = field.Name, FieldLabel = field.Label, SourceFid = KeptFid(field),
                    Severity = ImportNoteSeverities.Warning, Code = ImportFieldNoteBuilder.BROKEN_FORMULA_CODE, Message = message,
                    RecommendedAction = "Open the field, correct the formula (for example recreate the fields it names), and save.",
                    SourceLogicalRef = field.LogicalRef, SourceType = field.SourceType,
                    OriginalDefinition = field.OriginalFormula ?? field.FormulaExpression,
                    OriginalMetadata = JsonSerializer.Serialize(new
                    {
                        kind = "Formula", sourceType = field.SourceType, resultType = field.ResultType, variant = field.FormulaVariant, sourceId = field.SourceId,
                        settings = field.Settings, savedExpression = field.FormulaExpression, translationDiagnostics = problems,
                    }, NoteJson),
                });
            }
        }

        return (saved, notSaved);
    }

    /// <summary>Creates every relationship via <see cref="CreateRelationshipCommandHandler"/>,
    /// resolving each Lookup's parent-side source field and each Summary's child-side target
    /// field to real Fids first (both already exist from Pass 1 — <see cref="PblValidator"/>
    /// guarantees these names resolve before import ever reaches this point, so a lookup miss
    /// here reflects a table/field that itself failed to create, not a validator gap).</summary>
    /// <summary>Fills each table's hidden Default Report Settings record (what the "Default report" page edits) with the source table's
    /// default columns, default dynamic filters, quick search and default sort. A name that did not become a field is reported and left out;
    /// a part the source does not state keeps PowerBase's default.</summary>
    /// <summary>SingleSelect/MultiSelect fields whose choices come from another field (<see cref="PblField.ChoiceSourceFieldRef"/>)
    /// need that other field's real TableId/Fid, which only exist once every table's fields are created. Builds one
    /// LogicalRef → (real table, real field) map across the whole app, then patches each such field's own Settings with the
    /// resolved ChoiceSource. A target that was never created (unsupported type, failed formula, or this import's best-effort
    /// guess at where Quickbase names the source field turning out wrong) is reported, never left as a silently empty choice list.</summary>
    private async Task ApplyChoiceSourcesAsync(
        PblDocument document, Dictionary<string, AppTable> tableByName, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var candidates = (document.Tables ?? [])
            .SelectMany(t => (t.Fields ?? []).Where(f => f.ChoiceSourceFieldRef is not null).Select(f => (Table: t, Field: f)))
            .ToList();
        if (candidates.Count == 0) return;

        var realByRef = new Dictionary<string, (AppTable Table, AppField Field)>(StringComparer.Ordinal);
        foreach (var table in document.Tables ?? [])
        {
            if (!tableByName.TryGetValue(table.Name, out var createdTable)) continue;
            var realFields = await _fieldRepo.ListByTableAsync(createdTable.Id, ct);
            var byName = realFields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var field in table.Fields ?? [])
                if (byName.TryGetValue(field.Name, out var real))
                    realByRef[field.LogicalRef] = (createdTable, real);
        }

        foreach (var (table, field) in candidates)
        {
            if (!realByRef.TryGetValue(field.LogicalRef, out var source))
                continue; // the select field itself was never created — nothing to patch

            if (!realByRef.TryGetValue(field.ChoiceSourceFieldRef!, out var target))
            {
                skipped.Add(new ImportSkippedItem
                {
                    LogicalRef = field.LogicalRef,
                    Name = field.Name,
                    Reason = $"Field '{field.Name}' on table '{table.Name}' has choices that come from another field in Quickbase, but that field could not be found in this import. Set up the field's \"choices from another field\" setting manually in PowerBase.",
                });
                continue;
            }

            var settings = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(source.Field.Settings))
                foreach (var prop in JsonDocument.Parse(source.Field.Settings).RootElement.EnumerateObject())
                    settings[prop.Name] = prop.Value.Clone();
            settings.Remove("options");
            settings["choiceSource"] = new Dictionary<string, object?> { ["tableId"] = target.Table.PublicId, ["fid"] = target.Field.Fid };

            await _fieldRepo.UpdateSettingsAsync(source.Field.Id, JsonSerializer.Serialize(settings), ct);
        }
    }

    private async Task ApplyDefaultReportSettingsAsync(
        PblDocument document, Dictionary<string, AppTable> tableByName, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        // New-app imports only: updating an existing app must not overwrite the default report its owner has set up.
        if (ImportRuntime.Fids is null)
            return;

        foreach (var table in document.Tables ?? [])
        {
            var wanted = table.DefaultReportSettings;
            if (wanted is null || !tableByName.TryGetValue(table.Name, out var created))
                continue;

            try
            {
                var record = await _reportRepo.GetDefaultSettingsRecordAsync(created.PublicId, ct);
                if (record is null)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = table.LogicalRef, Name = table.Name, Reason = "Default report settings not applied: the table has no Default Report Settings record." });
                    continue;
                }

                var fidByName = FidByNameOrLabel(await _fieldRepo.ListByTableAsync(created.Id, ct));
                var missing = new List<string>();
                List<long> Resolve(IEnumerable<string> names)
                {
                    var fids = new List<long>();
                    foreach (var name in names)
                    {
                        if (fidByName.TryGetValue(name, out var fid)) fids.Add(fid);
                        else missing.Add(name);
                    }
                    return fids;
                }

                var definition = string.IsNullOrWhiteSpace(record.Definition)
                    ? new Reports.ReportDefinition()
                    : JsonSerializer.Deserialize<Reports.ReportDefinition>(record.Definition) ?? new Reports.ReportDefinition();

                if (wanted.ColumnFieldNames is { Count: > 0 } columnNames)
                {
                    var columns = Resolve(columnNames);
                    if (columns.Count > 0)
                    {
                        definition.Columns = columns;
                        definition.ColumnsMode = "Custom";
                    }
                }

                if (wanted.DynamicFilterType is { Length: > 0 } filterType)
                {
                    definition.DynamicFilterType = filterType;
                    definition.CustomDynamicFilterFields = filterType == "Custom" ? Resolve(wanted.DynamicFilterFieldNames) : [];
                    definition.CustomDynamicFilterItems = [];
                }
                if (wanted.AllowQuickSearch is { } quickSearch)
                    definition.AllowQuickSearch = quickSearch;

                if (wanted.SortFieldName is { Length: > 0 } sortName)
                {
                    var sort = Resolve([sortName]);
                    if (sort.Count > 0)
                        definition.SortFields = [new Reports.SortSpec { FieldId = sort[0], Desc = wanted.SortDescending }];
                }

                await _reportRepo.UpdateAsync(record.PublicId, record.Name, record.Description, record.Visibility, JsonSerializer.Serialize(definition), ct);
                await _refIndexer.ReindexReportAsync(record.PublicId, ct);

                if (missing.Count > 0)
                    skipped.Add(new ImportSkippedItem { LogicalRef = table.LogicalRef, Name = table.Name, Reason = $"Default report settings: {string.Join(", ", missing.Distinct().Select(m => $"'{m}'"))} did not become a field, so it is not among the default columns, dynamic filters or sort." });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = table.LogicalRef, Name = table.Name, Reason = $"Default report settings not applied: {ex.Message}" });
            }
        }
    }

    /// <summary>Creates the source report links that are not a relationship's own (they match on an ordinary field, or are a second link
    /// to the same child), each on its source number. A link whose target or source field was not created is reported, not guessed.</summary>
    /// <param name="onlyRefs">Retry pass: only these links (by logical ref).</param>
    /// <param name="final">False on the first pass: a link whose target or matching field is not created yet (a formula or lookup, made later) is
    /// held back for the retry instead of being reported as lost. True on the retry: whatever still cannot be created is reported.</param>
    /// <returns>How many links were created, and the refs held back for the retry.</returns>
    private async Task<(int Created, List<string> Deferred)> CreateStandaloneReportLinksAsync(
        PblDocument document,
        Dictionary<string, string> tableNameByRef,
        Dictionary<string, AppTable> tableByName,
        Guid appPublicId,
        List<ImportSkippedItem> skipped,
        CancellationToken ct,
        IReadOnlyCollection<string>? onlyRefs = null,
        bool final = true)
    {
        var created = 0;
        var deferred = new List<string>();
        foreach (var table in document.Tables ?? [])
        {
            foreach (var link in table.ReportLinks ?? [])
            {
                if (onlyRefs is not null && !onlyRefs.Contains(link.LogicalRef)) continue;

                if (_reportLinkFactory is null)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = link.LogicalRef, Name = link.Name, Reason = "Report link not created: this import is not set up to create report links." });
                    continue;
                }

                if (!tableByName.TryGetValue(table.Name, out var ownTable)
                    || !tableNameByRef.TryGetValue(link.TargetTableRef, out var targetName) || !tableByName.TryGetValue(targetName, out var targetTable))
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = link.LogicalRef, Name = link.Name, Reason = "Report link not created: the table it opens was not created." });
                    continue;
                }

                var targetFids = FidByNameOrLabel(await _fieldRepo.ListByTableAsync(targetTable.Id, ct));
                if (!targetFids.TryGetValue(link.TargetFieldName, out var targetFid))
                {
                    if (!final) { deferred.Add(link.LogicalRef); continue; }
                    skipped.Add(new ImportSkippedItem { LogicalRef = link.LogicalRef, Name = link.Name, Reason = $"Report link not created: its field '{link.TargetFieldName}' on '{targetName}' was not created." });
                    continue;
                }

                int? sourceFid = null;
                if (link.SourceFieldName is { Length: > 0 } sourceName)
                {
                    var ownFids = FidByNameOrLabel(await _fieldRepo.ListByTableAsync(ownTable.Id, ct));
                    if (!ownFids.TryGetValue(sourceName, out var found))
                    {
                        if (!final) { deferred.Add(link.LogicalRef); continue; }
                        skipped.Add(new ImportSkippedItem { LogicalRef = link.LogicalRef, Name = link.Name, Reason = $"Report link not created: the field '{sourceName}' it matches on was not created." });
                        continue;
                    }
                    sourceFid = (int)found;
                }

                try
                {
                    await _reportLinkFactory.CreateAsync(ownTable, nameof(PowerBase.Domain.Enums.FieldTypeCode.ReportLink), (link.Label ?? link.Name).Trim(), false,
                        new ReportLinkSettings
                        {
                            TargetAppPublicId = appPublicId.ToString(),
                            TargetTablePublicId = targetTable.PublicId.ToString(),
                            TargetFid = (int)targetFid,
                            SourceFid = sourceFid,
                            LinkText = string.IsNullOrWhiteSpace(link.LinkText) ? (link.Label ?? link.Name) : link.LinkText.Trim(),
                            OpenInNewWindow = false,
                        }, ct, explicitFid: link.SourceId);
                    created++;
                }
                catch (DomainException ex)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = link.LogicalRef, Name = link.Name, Reason = $"Report link not created: {ex.Message}" });
                }
            }
        }
        return (created, deferred);
    }

    private async Task<int> CreateRelationshipsAsync(
        List<PblRelationship> relationships,
        Dictionary<string, string> tableNameByRef,
        Dictionary<string, AppTable> tableByName,
        Guid appPublicId,
        List<DeferredLookup> deferredLookups,
        List<DeferredSummary> deferredSummaries,
        List<ImportSkippedItem> skipped,
        CancellationToken ct)
    {
        var created = 0;

        foreach (var rel in relationships)
        {
            if (!tableNameByRef.TryGetValue(rel.ParentTableRef, out var parentName) || !tableByName.TryGetValue(parentName, out var parentTable) ||
                !tableNameByRef.TryGetValue(rel.ChildTableRef, out var childName) || !tableByName.TryGetValue(childName, out var childTable))
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = rel.LogicalRef, Name = rel.ReferenceFieldName, Reason = "Relationship references a table that was not created." });
                continue;
            }

            var parentFields = await _fieldRepo.ListByTableAsync(parentTable.Id, ct);
            var childFields = await _fieldRepo.ListByTableAsync(childTable.Id, ct);
            var parentFidByName = FidByNameOrLabel(parentFields).ToDictionary(kv => kv.Key, kv => (int)kv.Value, StringComparer.OrdinalIgnoreCase);
            var childFidByName = FidByNameOrLabel(childFields).ToDictionary(kv => kv.Key, kv => (int)kv.Value, StringComparer.OrdinalIgnoreCase);

            // A lookup/summary whose source/target doesn't exist yet (typically a formula or another
            // lookup, created later) is deferred and retried by PblDeferredResolver - only reported
            // as skipped if it never becomes possible.
            var pendingLookups = new List<PblLookupField>();
            var pendingSummaries = new List<PblSummaryField>();

            // A file that lists the parent's link fields (a Quickbase file) gets exactly those, on their own numbers. Only a new-app import
            // keeps source numbers (the update flow adds to an app that has its own numbering).
            var explicitLinks = rel.ExplicitLinks && ImportRuntime.Fids is not null;
            var reportLink = explicitLinks ? rel.ReportLink : null;

            var lookupSpecs = new List<CreateLookupSpec>();
            foreach (var lookup in rel.Lookups)
            {
                if (!parentFidByName.TryGetValue(lookup.SourceFieldName, out var sourceFid))
                {
                    pendingLookups.Add(lookup);
                    continue;
                }
                // A lookup can only be chained through a few lookups (LookupChain.MaxLength). The command
                // checks this part-way through creating the relationship, which would leave it half-built
                // and abort the import — so check here and skip just this lookup instead.
                var sourceField = parentFields.FirstOrDefault(f => f.Fid == sourceFid);
                if (sourceField is not null)
                {
                    var (_, chainLength) = await LookupChain.ResolveAsync(sourceField, _fieldRepo, ct);
                    if (chainLength + 1 > LookupChain.MaxLength)
                    {
                        skipped.Add(new ImportSkippedItem { LogicalRef = lookup.LogicalRef, Name = lookup.Name, Reason = $"Lookup of '{lookup.SourceFieldName}' would chain through more than {LookupChain.MaxLength} lookups, which PowerBase does not allow." });
                        continue;
                    }
                }
                // Note: relationship-created fields don't carry the Name-preservation escape hatch that
                // plain/formula fields do (see AppFieldSpec.Name) — re-importing a file with lookups
                // gets freshly generated Names rather than the originally-exported ones.
                lookupSpecs.Add(new CreateLookupSpec(sourceFid, lookup.Label ?? lookup.Name, lookup.SourceSubField));
            }

            var summarySpecs = new List<CreateSummarySpec>();
            var summaryBySpec = new Dictionary<CreateSummarySpec, PblSummaryField>();
            // A summary over a lookup of the child is judged by that lookup's source field.
            var lookupSources = rel.Summaries.Count > 0 ? await SummaryLookupSources.LoadAsync(childFields, _fieldRepo, ct) : null;
            foreach (var summary in rel.Summaries)
            {
                if (summary.SkipReason is not null)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = summary.LogicalRef, Name = summary.Name, Reason = $"Summary not created: {summary.SkipReason}." });
                    continue;
                }

                // CreateRelationship cannot carry matching criteria; AddSummaryField can, so a filtered summary is added right after.
                if (summary.Filter is not null)
                {
                    pendingSummaries.Add(summary);
                    continue;
                }

                int? targetFid = null;
                if (summary.TargetFieldName is not null)
                {
                    if (!childFidByName.TryGetValue(summary.TargetFieldName, out var fid))
                    {
                        pendingSummaries.Add(summary);
                        continue;
                    }
                    targetFid = fid;
                }
                // PBL accepts function names case-insensitively; store the canonical spelling.
                var function = SummaryFunctions.Normalize(summary.Function);
                if (function is null)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = summary.LogicalRef, Name = summary.Name, Reason = $"Unknown summary function '{summary.Function}'." });
                    continue;
                }
                // Same function-vs-type rule as the relationship endpoints; skip just this summary
                // (reported) rather than letting CreateRelationship reject the whole relationship.
                try
                {
                    var target = targetFid.HasValue ? childFields.FirstOrDefault(f => f.Fid == targetFid) : null;
                    // A summary of a Reference reads the key its parent shows; CreateRelationship has the maps to judge that.
                    if (target?.TypeCode != "Reference")
                        SummaryTargetValidator.Validate(function, targetFid, target, summary.TargetSubField, lookupSources: lookupSources);
                }
                catch (ValidationException ex)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = summary.LogicalRef, Name = summary.Name, Reason = string.Join(" ", ex.Errors.SelectMany(e => e.Value)) });
                    continue;
                }
                summarySpecs.Add(new CreateSummarySpec(summary.Label ?? summary.Name, function, targetFid, summary.TargetSubField));
                summaryBySpec[summarySpecs[^1]] = summary;
            }

            try
            {
                RelationshipDto createdRel;
                try
                {
                    createdRel = await _createRelationshipHandler.HandleAsync(new CreateRelationshipCommand(
                        appPublicId,
                        parentTable.PublicId,
                        childTable.PublicId,
                        rel.ReferenceFieldLabel ?? rel.ReferenceFieldName,
                        rel.IsReferenceRequired,
                        lookupSpecs,
                        summarySpecs,
                        ReportLinkFid: reportLink?.SourceId,
                        ReportLinkLabel: reportLink?.Label,
                        ReportLinkText: reportLink?.LinkText,
                        CreateReportLink: !explicitLinks || reportLink is not null,
                        CreateAddRecordLink: !explicitLinks), ct);
                }
                catch (ValidationException) when (summarySpecs.Count > 0)
                {
                    // A summary PowerBase's relationship check refuses (for example Max of a Reference whose key is text) must not cost the whole
                    // relationship: create it without the summaries, and add each one on its own afterwards, where a refusal is reported per
                    // summary and leaves a repairable placeholder.
                    createdRel = await _createRelationshipHandler.HandleAsync(new CreateRelationshipCommand(
                        appPublicId, parentTable.PublicId, childTable.PublicId,
                        rel.ReferenceFieldLabel ?? rel.ReferenceFieldName, rel.IsReferenceRequired, lookupSpecs, [],
                        ReportLinkFid: reportLink?.SourceId,
                        ReportLinkLabel: reportLink?.Label,
                        ReportLinkText: reportLink?.LinkText,
                        CreateReportLink: !explicitLinks || reportLink is not null,
                        CreateAddRecordLink: !explicitLinks), ct);
                    pendingSummaries.AddRange(summarySpecs.Select(sp => summaryBySpec[sp]));
                }

                created++;
                deferredLookups.AddRange(pendingLookups.Select(l => new DeferredLookup(createdRel.PublicId, parentTable, l)));
                deferredSummaries.AddRange(pendingSummaries.Select(sm => new DeferredSummary(createdRel.PublicId, childTable, sm)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One relationship PowerBase rejects must not take the whole import down with it. Its reference field, lookups and
                // summaries are reported one by one, so each gets a placeholder field at the end.
                var why = ex is ValidationException ve ? string.Join(" ", ve.Errors.SelectMany(e => e.Value)) : ex.Message;
                skipped.Add(new ImportSkippedItem { LogicalRef = rel.LogicalRef, Name = rel.ReferenceFieldName, Code = "REFERENCE_NOT_CREATED", Reason = $"Relationship not fully created: {why}" });
                foreach (var lookup in rel.Lookups)
                    skipped.Add(new ImportSkippedItem { LogicalRef = lookup.LogicalRef, Name = lookup.Name, Code = "RELATIONSHIP_NOT_CREATED", Reason = $"Lookup not created: its relationship '{rel.ReferenceFieldName}' could not be created ({why})." });
                foreach (var summary in rel.Summaries)
                    skipped.Add(new ImportSkippedItem { LogicalRef = summary.LogicalRef, Name = summary.Name, Code = "RELATIONSHIP_NOT_CREATED", Reason = $"Summary not created: its relationship '{rel.ReferenceFieldName}' could not be created ({why})." });
            }
        }

        return created;
    }

    /// <summary>Guids assigned to sections/blocks/elements while saving a form's layout are
    /// only meaningful within this single import call — <see cref="CreateFormRulesAsync"/> uses
    /// them to re-fetch the layout and match the real, persisted DbIds back to the
    /// PblForm*.LogicalRef each Guid was generated for. PowerBase itself has no need for these
    /// ids to be stable across requests.</summary>
    /// <summary>
    /// The JSON PowerBase keeps for an embedded report element: the report, the table it belongs to, and the parent's ReportLink field that
    /// filters it to related records. The link is found through the relationship between the form's table and the report's table.
    /// Null when any of those do not exist.
    /// </summary>
    private async Task<long?> FindReportLinkFidAsync(
        PblFormElement element, AppTable parentTable, Dictionary<string, string> tableNameByRef, Dictionary<string, AppTable> tableByName, CancellationToken ct)
    {
        if (element.ReportLinkChildTableRef is null || element.ReportLinkReferenceFieldName is null) return null;
        if (!tableNameByRef.TryGetValue(element.ReportLinkChildTableRef, out var childName) || !tableByName.TryGetValue(childName, out var childTable)) return null;

        var referenceName = element.ReportLinkReferenceFieldName.Trim();
        var childFields = await _fieldRepo.ListByTableAsync(childTable.Id, ct);
        var reference = childFields.FirstOrDefault(f => string.Equals(f.TypeCode, "Reference", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(f.Label?.Trim(), referenceName, StringComparison.OrdinalIgnoreCase) || string.Equals(f.Name, referenceName, StringComparison.OrdinalIgnoreCase)));
        if (reference?.Fid is not { } referenceFid) return null;

        var parentFields = await _fieldRepo.ListByTableAsync(parentTable.Id, ct);
        return parentFields.FirstOrDefault(f => string.Equals(f.TypeCode, "ReportLink", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(f.Settings)
            && JsonSerializer.Deserialize<ReportLinkSettings>(f.Settings!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } rl
            && string.Equals(rl.TargetTablePublicId, childTable.PublicId.ToString(), StringComparison.OrdinalIgnoreCase)
            && rl.TargetFid == referenceFid)?.Fid;
    }

    private async Task<string?> BuildReportElementContentAsync(
        PblFormElement element, PblForm form, AppTable parentTable, Dictionary<string, CreatedReport> createdReports,
        List<PblRelationship> relationships, Dictionary<string, string> tableNameByRef, Dictionary<string, AppTable> tableByName, CancellationToken ct)
    {
        if (element.EmbeddedReportRef is null || !createdReports.TryGetValue(element.EmbeddedReportRef, out var report))
            return null;
        if (element.EmbeddedReportTableRef is null || !tableNameByRef.TryGetValue(element.EmbeddedReportTableRef, out var childName) || !tableByName.TryGetValue(childName, out var childTable))
            return null;

        var relationship = relationships.FirstOrDefault(r => r.ParentTableRef == form.TableRef && r.ChildTableRef == element.EmbeddedReportTableRef);
        if (relationship is null)
            return null;

        var parentFields = await _fieldRepo.ListByTableAsync(parentTable.Id, ct);
        var childFields = await _fieldRepo.ListByTableAsync(childTable.Id, ct);
        var referenceLabel = (relationship.ReferenceFieldLabel ?? relationship.ReferenceFieldName).Trim();
        var referenceField = childFields.FirstOrDefault(f => string.Equals(f.TypeCode, "Reference", StringComparison.OrdinalIgnoreCase)
            && string.Equals(f.Label?.Trim(), referenceLabel, StringComparison.OrdinalIgnoreCase));
        if (referenceField?.Fid is not { } referenceFid)
            return null;

        var reportLink = parentFields.FirstOrDefault(f => string.Equals(f.TypeCode, "ReportLink", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(f.Settings)
            && JsonSerializer.Deserialize<ReportLinkSettings>(f.Settings!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } rl
            && string.Equals(rl.TargetTablePublicId, childTable.PublicId.ToString(), StringComparison.OrdinalIgnoreCase)
            && rl.TargetFid == referenceFid);
        if (reportLink?.Fid is not { } reportLinkFid)
            return null;

        var appPublicId = await _appRepo.GetPublicIdByIdAsync(parentTable.AppId, ct);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["reportId"] = report.PublicId.ToString(),
            ["reportName"] = report.Name,
            ["reportAppPublicId"] = appPublicId.ToString(),
            ["reportTablePublicId"] = report.TablePublicId.ToString(),
            ["filterMode"] = "reportLink",
            ["reportLinkFid"] = reportLinkFid,
            ["heightMode"] = "auto",
            ["heightPx"] = null,
            ["showToolbar"] = element.ShowToolbar,
            ["showCreateButton"] = element.AllowCreateRecords,
            ["openInSubForm"] = element.OpenInSubForm,
            ["subFormId"] = null,
            ["embeddedForFid"] = null,
        });
    }

    private sealed record CreatedReport(Guid PublicId, string Name, Guid TablePublicId);

    private sealed record CreatedFormLayout(
        Guid FormPublicId,
        Dictionary<string, Guid> SectionGuidByRef,
        Dictionary<string, Guid> BlockGuidByRef,
        Dictionary<string, Guid> ElementGuidByRef,
        Dictionary<string, Guid> PageGuidByRef);

    /// <summary>Creates every form via <see cref="CreateFormCommandHandler"/> then
    /// <see cref="SaveFormLayoutCommandHandler"/>. Form elements reference fields by Name,
    /// resolved to Fid the same way report columns are.</summary>
    private async Task<Dictionary<string, CreatedFormLayout>> CreateFormsAsync(
        List<PblForm> forms,
        Dictionary<string, string> tableNameByRef,
        Dictionary<string, AppTable> tableByName,
        ISet<string>? tablesKeepingDefaults,
        Dictionary<string, CreatedReport> createdReports,
        List<PblRelationship> relationships,
        List<ImportSkippedItem> skipped,
        CancellationToken ct)
    {
        var result = new Dictionary<string, CreatedFormLayout>(StringComparer.Ordinal);
        var defaultedTableRefs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var form in forms)
        {
            if (!tableNameByRef.TryGetValue(form.TableRef, out var tableName) || !tableByName.TryGetValue(tableName, out var table))
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = form.LogicalRef, Name = form.Name, Reason = "Form references a table that was not created." });
                continue;
            }

            var tableFields = await _fieldRepo.ListByTableAsync(table.Id, ct);
            var fidByName = FidByNameOrLabel(tableFields);

            var sectionGuidByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var blockGuidByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var elementGuidByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var sectionLayouts = new List<FormSectionLayout>();

            // Pages (only a multi-page form has them) and element groups get stable ids for this save.
            var pageGuidByRef = form.Pages.ToDictionary(pg => pg.LogicalRef, _ => Guid.NewGuid(), StringComparer.Ordinal);
            var groupGuidByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
            IReadOnlyList<FormPageLayout>? pageLayouts = form.Pages.Count == 0 ? null
                : form.Pages.OrderBy(pg => pg.DisplayOrder).Select(pg => new FormPageLayout(pageGuidByRef[pg.LogicalRef], pg.Heading, pg.DisplayOrder)).ToList();

            foreach (var section in form.Sections)
            {
                var blockLayouts = new List<FormBlockLayout>();

                foreach (var block in section.Blocks)
                {
                    var elementLayouts = new List<FormElementLayout>();

                    foreach (var element in block.Elements)
                    {
                        long? appFieldId = null;
                        var elementContent = element.ElementContent;

                        if (string.Equals(element.ElementType, "Report", StringComparison.OrdinalIgnoreCase))
                        {
                            elementContent = await BuildReportElementContentAsync(element, form, table, createdReports, relationships, tableNameByRef, tableByName, ct);
                            if (elementContent is null)
                            {
                                skipped.Add(new ImportSkippedItem { LogicalRef = element.LogicalRef, Name = element.EmbeddedReportRef ?? element.LogicalRef,
                                    Reason = "Embedded report not added: the report, or the relationship it hangs off, was not created." });
                                continue;
                            }
                        }

                        if (string.Equals(element.ElementType, "Field", StringComparison.OrdinalIgnoreCase))
                        {
                            var linkOrFid = element.FieldName is not null && fidByName.TryGetValue(element.FieldName, out var knownFid)
                                ? knownFid
                                : await FindReportLinkFidAsync(element, table, tableNameByRef, tableByName, ct);
                            if (linkOrFid is not { } fid)
                            {
                                skipped.Add(new ImportSkippedItem { LogicalRef = element.LogicalRef, Name = element.FieldName ?? element.LogicalRef, Reason = "Form element references a field that was not created." });
                                continue;
                            }
                            appFieldId = fid;
                        }

                        var elementGuid = Guid.NewGuid();
                        elementGuidByRef[element.LogicalRef] = elementGuid;
                        Guid? groupId = null;
                        if (element.GroupRef is not null)
                        {
                            if (!groupGuidByRef.TryGetValue(element.GroupRef, out var g)) groupGuidByRef[element.GroupRef] = g = Guid.NewGuid();
                            groupId = g;
                        }

                        elementLayouts.Add(new FormElementLayout(
                            elementGuid, appFieldId, element.ElementType, elementContent,
                            element.LabelMode, Fit(element.CustomLabel, FormTextMaxLength), element.ShowOnAdd, element.ShowOnEdit, element.ShowOnView,
                            element.WidthMode, element.WidthValue, element.HelpTextOverride, element.IsReadOnly, element.IsRequired, element.DisplayAs,
                            GroupId: groupId));
                    }

                    var blockGuid = Guid.NewGuid();
                    blockGuidByRef[block.LogicalRef] = blockGuid;
                    blockLayouts.Add(new FormBlockLayout(blockGuid, Fit(block.Heading, FormTextMaxLength), block.BackgroundColor, block.Width, elementLayouts));
                }

                var sectionGuid = Guid.NewGuid();
                sectionGuidByRef[section.LogicalRef] = sectionGuid;
                sectionLayouts.Add(new FormSectionLayout(
                    sectionGuid, Fit(section.Name, FormTextMaxLength), section.IsCollapsed, blockLayouts,
                    PagePublicId: section.PageRef is not null && pageGuidByRef.TryGetValue(section.PageRef, out var pageGuid) ? pageGuid : null,
                    IsPinned: section.IsPinned ? true : null,
                    ShowDividers: section.ShowDividers,
                    ShowHeading: section.ShowHeading));
            }

            if (sectionLayouts.Count == 0)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = form.LogicalRef, Name = form.Name, Reason = "Form has no sections after resolving fields; skipped." });
                continue;
            }

            var createdForm = await _createFormHandler.HandleAsync(new CreateFormCommand(table.PublicId, Fit(form.Name, FormTextMaxLength)!), ct);
            await _saveFormLayoutHandler.HandleAsync(new SaveFormLayoutCommand(
                createdForm.Id, sectionLayouts, pageLayouts, form.PageNavMode, form.AlwaysTabsOnView), ct);

            // Form-level switches (auto-add fields, built-in fields footer, save buttons).
            if (form.AutoAddNewFields.HasValue || form.ShowBuiltInFields.HasValue || !string.IsNullOrEmpty(form.SaveOptions))
            {
                try
                {
                    var current = await _formRepo.GetByPublicIdAsync(createdForm.Id, ct);
                    await _formRepo.UpdateSettingsAsync(
                        createdForm.Id, current.Name, form.AutoAddNewFields ?? current.AutoAddNewFields,
                        form.ShowBuiltInFields ?? current.ShowBuiltInFields, form.SaveOptions ?? current.SaveOptions, current.RowVersion, ct: ct);
                }
                catch (DomainException ex)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = form.LogicalRef, Name = form.Name, Reason = $"Form settings not applied: {Describe(ex)}" });
                }
            }

            // Forms are created IsDefault=false, and the seeder's default form was suppressed for
            // any table supplying its own — so without this the table would have no default form
            // at all. First imported form for a table wins, matching document order.
            if (defaultedTableRefs.Add(form.TableRef) && tablesKeepingDefaults?.Contains(tableName) != true)
                await _formRepo.SetDefaultAsync(table.PublicId, createdForm.Id, ct);

            result[form.LogicalRef] = new CreatedFormLayout(createdForm.Id, sectionGuidByRef, blockGuidByRef, elementGuidByRef, pageGuidByRef);
        }

        return result;
    }

    /// <summary>Quickbase "role defaults": which form each role (or everyone) edits and adds with. Only matters where a table has more than one
    /// form, so single-form tables are left alone. Everyone's edit form also becomes the table's default form.</summary>
    private async Task ApplyFormRoleDefaultsAsync(
        PblDocument document, Dictionary<string, CreatedFormLayout> createdFormLayouts, Dictionary<string, string> tableNameByRef,
        Dictionary<string, AppTable> tableByName, Dictionary<string, Guid> roleIdsByRef, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var defaults = document.FormRoleDefaults ?? [];
        if (defaults.Count == 0) return;

        var formsPerTable = (document.Forms ?? []).GroupBy(f => f.TableRef).ToDictionary(g => g.Key, g => g.Count());
        foreach (var group in defaults.GroupBy(d => d.TableRef))
        {
            if (formsPerTable.GetValueOrDefault(group.Key) < 2) continue;
            if (!tableNameByRef.TryGetValue(group.Key, out var tableName) || !tableByName.TryGetValue(tableName, out var table)) continue;

            Guid? FormId(string? formRef) => formRef is not null && createdFormLayouts.TryGetValue(formRef, out var layout) ? layout.FormPublicId : null;

            var overrides = new List<(Guid? RolePublicId, Guid? EditFormPublicId, Guid? AddFormPublicId)>();
            foreach (var d in group)
            {
                Guid? roleId = null;
                if (d.RoleRef is not null)
                {
                    if (!roleIdsByRef.TryGetValue(d.RoleRef, out var resolved))
                    {
                        skipped.Add(new ImportSkippedItem { LogicalRef = d.TableRef, Name = tableName, Reason = $"Form choice for role '{d.RoleRef}' not applied: the role was not created." });
                        continue;
                    }
                    roleId = resolved;
                }
                var edit = FormId(d.EditFormRef);
                var add = FormId(d.AddFormRef);
                if (edit is null && add is null) continue;
                overrides.Add((roleId, edit, add));
            }

            try
            {
                if (overrides.Count > 0)
                    await _formRepo.UpdateRoleFormOverridesAsync(table.PublicId, overrides, ct);
                if (overrides.FirstOrDefault(o => o.RolePublicId is null && o.EditFormPublicId is not null).EditFormPublicId is { } everyoneForm)
                    await _formRepo.SetDefaultAsync(table.PublicId, everyoneForm, ct);
            }
            catch (DomainException ex)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = group.Key, Name = tableName, Reason = $"Form choices per role not applied: {Describe(ex)}" });
            }
        }
    }

    /// <summary>Re-fetches each form's persisted layout to resolve the real DbIds rule action
    /// targets need (<c>SaveFormLayoutCommandHandler</c> only takes ids, it doesn't hand them
    /// back — see <see cref="CreateFormsAsync"/>'s Guid bookkeeping), then creates each rule via
    /// <see cref="CreateFormRuleCommandHandler"/> followed by <see cref="SaveFormRuleCommandHandler"/>.</summary>
    private async Task<int> CreateFormRulesAsync(
        List<PblForm> forms,
        Dictionary<string, CreatedFormLayout> createdFormLayouts,
        Dictionary<string, string> tableNameByRef,
        Dictionary<string, AppTable> tableByName,
        List<ImportSkippedItem> skipped,
        CancellationToken ct)
    {
        var created = 0;

        foreach (var form in forms)
        {
            if (form.Rules.Count == 0)
                continue;

            if (!createdFormLayouts.TryGetValue(form.LogicalRef, out var layout) ||
                !tableNameByRef.TryGetValue(form.TableRef, out var tableName) || !tableByName.TryGetValue(tableName, out var table))
                continue; // form itself failed to create; already reported by CreateFormsAsync

            var tableFields = await _fieldRepo.ListByTableAsync(table.Id, ct);
            var fidByName = FidByNameOrLabel(tableFields);

            var persisted = await _getFormLayoutHandler.HandleAsync(new GetFormLayoutQuery(layout.FormPublicId), ct);
            var sectionDbIdByPublicId = persisted.Sections.ToDictionary(s => s.Id, s => s.DbId);
            var blockDbIdByPublicId = persisted.Sections.SelectMany(s => s.Blocks).ToDictionary(b => b.Id, b => b.DbId);
            var elementDbIdByPublicId = persisted.Sections.SelectMany(s => s.Blocks).SelectMany(b => b.Elements).ToDictionary(e => e.Id, e => e.DbId);

            foreach (var rule in form.Rules)
            {
                var conditionSpecs = new List<FormRuleConditionSpec>();
                var unresolvedCondition = false;

                if (!rule.IsExpressionMode)
                {
                    foreach (var condition in rule.Conditions)
                    {
                        if (string.Equals(condition.Kind, "role", StringComparison.OrdinalIgnoreCase))
                        {
                            conditionSpecs.Add(new FormRuleConditionSpec(null, condition.Operator, condition.Value, null, null, condition.DisplayOrder, ConditionKind: "role"));
                            continue;
                        }

                        if (!fidByName.TryGetValue(condition.FieldName, out var fid))
                        {
                            unresolvedCondition = true;
                            break;
                        }
                        long? valueFieldId = null;
                        if (condition.ValueFieldName is not null)
                        {
                            if (!fidByName.TryGetValue(condition.ValueFieldName, out var otherFid))
                            {
                                unresolvedCondition = true;
                                break;
                            }
                            valueFieldId = otherFid;
                        }
                        conditionSpecs.Add(new FormRuleConditionSpec(fid, condition.Operator, condition.Value, condition.ValueType, valueFieldId, condition.DisplayOrder));
                    }
                }

                if (unresolvedCondition)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = rule.LogicalRef, Name = rule.Name, Reason = "Form rule condition references a field that was not created." });
                    continue;
                }

                var actionSpecs = new List<FormRuleActionSpec>();
                var unresolvedAction = false;

                foreach (var action in rule.Actions)
                {
                    long? targetElementId = null, targetSectionId = null, targetBlockId = null;
                    Guid? targetPagePublicId = null;
                    var ok = false;

                    // A message or "block the save" that names no target is complete as it is.
                    if (action.ActionType is "DisplayMessage" or "PreventSave" && action.TargetElementRef is null && action.TargetSectionRef is null && action.TargetBlockRef is null)
                    {
                        actionSpecs.Add(new FormRuleActionSpec(
                            action.ActionType, "Field", null, null, null, action.ActionValue, action.DisplayOrder,
                            MessageType: action.MessageType, MessageSeverity: action.MessageSeverity));
                        continue;
                    }

                    if (action.TargetType == "Field" && action.TargetElementRef is not null &&
                        layout.ElementGuidByRef.TryGetValue(action.TargetElementRef, out var eg) && elementDbIdByPublicId.TryGetValue(eg, out var elementDbId))
                    {
                        targetElementId = elementDbId;
                        ok = true;
                    }
                    else if (action.TargetType == "Section" && action.TargetSectionRef is not null &&
                        layout.SectionGuidByRef.TryGetValue(action.TargetSectionRef, out var sg) && sectionDbIdByPublicId.TryGetValue(sg, out var sectionDbId))
                    {
                        targetSectionId = sectionDbId;
                        ok = true;
                    }
                    else if (action.TargetType == "Block" && action.TargetBlockRef is not null &&
                        layout.BlockGuidByRef.TryGetValue(action.TargetBlockRef, out var bg) && blockDbIdByPublicId.TryGetValue(bg, out var blockDbId))
                    {
                        targetBlockId = blockDbId;
                        ok = true;
                    }

                    else if (action.TargetType == "Page" && action.TargetPageRef is not null &&
                        layout.PageGuidByRef.TryGetValue(action.TargetPageRef, out var pg))
                    {
                        targetPagePublicId = pg;
                        ok = true;
                    }

                    if (!ok)
                    {
                        unresolvedAction = true;
                        break;
                    }

                    long? sourceFieldId = null;
                    if (action.ValueSource == "field")
                    {
                        if (action.SourceFieldName is null || !fidByName.TryGetValue(action.SourceFieldName, out var sourceFid))
                        {
                            unresolvedAction = true;
                            break;
                        }
                        sourceFieldId = sourceFid; // like a condition's field, a rule action refers to the source field by Fid
                    }

                    actionSpecs.Add(new FormRuleActionSpec(
                        action.ActionType, action.TargetType, targetElementId, targetSectionId, targetBlockId, action.ActionValue, action.DisplayOrder,
                        TargetPagePublicId: targetPagePublicId,
                        ValueSource: action.ValueSource, SourceFieldId: sourceFieldId, MessageType: action.MessageType, MessageSeverity: action.MessageSeverity));
                }

                if (unresolvedAction || actionSpecs.Count == 0)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = rule.LogicalRef, Name = rule.Name, Reason = "Form rule action references a target that was not created." });
                    continue;
                }

                // A rule formula is Quickbase text: give it the same dialect bridging the field formulas get (ToText around a number in "&",
                // a multi-branch If…) so what is saved is what PowerBase's own check accepts.
                var expressionText = rule.ExpressionText;
                if (rule.IsExpressionMode && !string.IsNullOrWhiteSpace(expressionText))
                {
                    var translation = _formulaTranslator.Translate("Bool", expressionText, tableFields);
                    if (translation.Status == FormulaTranslationStatus.NeedsManualReview)
                    {
                        skipped.Add(new ImportSkippedItem { LogicalRef = rule.LogicalRef, Name = rule.Name, Reason = $"Form rule formula needs manual review: {string.Join("; ", translation.Diagnostics)}" });
                        continue;
                    }
                    if (translation.SettingsJson is not null
                        && JsonSerializer.Deserialize<Dictionary<string, string>>(translation.SettingsJson) is { } settings
                        && settings.TryGetValue("expression", out var translated))
                        expressionText = translated;
                }

                try
                {
                    var createdRule = await _createFormRuleHandler.HandleAsync(new CreateFormRuleCommand(layout.FormPublicId, Fit(rule.Name, FormTextMaxLength)!), ct);
                    await _saveFormRuleHandler.HandleAsync(new SaveFormRuleCommand(
                        createdRule.Id, Fit(rule.Name, FormTextMaxLength)!, rule.Description, Tags: null, rule.IsActive, rule.RunTrigger, rule.ConditionLogic,
                        rule.IsExpressionMode, expressionText, conditionSpecs, actionSpecs, createdRule.RowVersion), ct);

                    created++;
                }
                catch (DomainException ex)
                {
                    // One rule PowerBase rejects must not take the import down with it.
                    skipped.Add(new ImportSkippedItem { LogicalRef = rule.LogicalRef, Name = rule.Name, Reason = $"Form rule not created: {Describe(ex)}" });
                }
            }
        }

        return created;
    }

    /// <summary>Creates every custom role via <see cref="CreateAppRoleCommandHandler"/> then
    /// its table permissions via <see cref="UpdateTablePermissionsCommandHandler"/>. Real QBL
    /// exports name roles like "Viewer"/"Administrator" that collide with the
    /// Administrator/Participant/Viewer roles <see cref="CreateAppCommandHandler"/> already
    /// seeded for this app — rather than merging permissions onto the pre-existing default role
    /// (a materially different, riskier operation), a name collision is reported and that
    /// role's permissions are skipped, matching the "flag, don't silently drop or guess" rule.</summary>
    private async Task<int> CreateRolesAsync(
        List<PblRole> roles,
        Dictionary<string, string> tableNameByRef,
        Dictionary<string, AppTable> tableByName,
        Guid appPublicId,
        List<ImportSkippedItem> skipped,
        CancellationToken ct)
    {
        var created = 0;

        // Roles named Administrator/Participant/Viewer already exist — CreateAppCommandHandler
        // seeds them for every new app — and real exports routinely carry roles by those same
        // names. Adopting the existing role and writing the imported permissions onto it is the
        // faithful outcome: skipping instead would silently leave PowerBase's default permissions
        // in place under a name the source file defines differently, which is a permissions
        // change nobody asked for and nothing surfaces.
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var existingRoles = await _appRoleRepo.ListDetailsByAppIdAsync(appId, ct);
        var existingRoleIdByName = existingRoles.ToDictionary(r => r.Name, r => r.PublicId, StringComparer.OrdinalIgnoreCase);

        foreach (var role in roles)
        {
            Guid rolePublicId;
            var adoptedExisting = false;
            try
            {
                var createdRole = await _createAppRoleHandler.HandleAsync(new CreateAppRoleCommand(appPublicId, role.Name, role.IsDefault, Rank: 3), ct);
                rolePublicId = createdRole.PublicId;
            }
            catch (DuplicateException)
            {
                if (!existingRoleIdByName.TryGetValue(role.Name, out rolePublicId))
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = role.LogicalRef, Name = role.Name, Reason = $"A role named '{role.Name}' already exists but could not be resolved; table permissions for this role were not imported." });
                    continue;
                }
                adoptedExisting = true;
            }

            if (adoptedExisting)
                skipped.Add(new ImportSkippedItem { LogicalRef = role.LogicalRef, Name = role.Name, Reason = $"A role named '{role.Name}' already existed on the new app; its permissions were replaced with the imported ones rather than creating a second role." });
            else
                created++;

            if (role.TablePermissions.Count == 0)
                continue;

            var tableInputs = new List<TablePermissionInput>();
            foreach (var perm in role.TablePermissions)
            {
                if (!tableNameByRef.TryGetValue(perm.TableRef, out var tableName) || !tableByName.TryGetValue(tableName, out var table))
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = role.LogicalRef, Name = role.Name, Reason = $"Role '{role.Name}' has a table permission for a table that was not created." });
                    continue;
                }

                tableInputs.Add(new TablePermissionInput(
                    table.PublicId, perm.ViewScope, perm.ModifyScope, perm.CanAdd, perm.CanDelete,
                    perm.CanSaveSharedReports, perm.CanEditFieldProperties, perm.FieldAccessLevel));
            }

            if (tableInputs.Count > 0)
                await _updateTablePermissionsHandler.HandleAsync(new UpdateTablePermissionsCommand(rolePublicId, tableInputs), ct);
        }

        return created;
    }

    /// <summary>Form names, section/block headings, element labels and rule names are stored in 200-character columns; a longer
    /// source text (real exports have 200+ character labels) is cut to fit instead of failing the whole import.</summary>
    private const int FormTextMaxLength = 200;

    private static string? Fit(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>Returns <paramref name="label"/>, or "label (2)", "label (3)"… when it is already in
    /// <paramref name="used"/>; the returned label is added to the set.</summary>
    private static string UniqueLabel(string label, HashSet<string> used)
    {
        var candidate = label;
        for (var n = 2; !used.Add(candidate); n++)
            candidate = $"{label} ({n})";
        return candidate;
    }

    private static Dictionary<string, long> FidByNameOrLabel(IEnumerable<AppField> fields) => FieldFids.ByNameOrLabel(fields);

    private static bool IsFormulaField(PblField field) =>
        string.Equals(field.TypeCode, PblValidator.FormulaTypeCode, StringComparison.OrdinalIgnoreCase);
}
