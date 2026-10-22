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
using PowerBase.Application.Import.Services;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Application.Reports.Commands.CreateReport;
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
        PblPermissionsApplier permissionsApplier)
    {
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

    /// <summary>Imports a file that holds exactly one application. A QBL file with several goes through <see cref="HandleBatchAsync"/>.</summary>
    public async Task<ImportReport> HandleAsync(ImportAppFromPblCommand command, CancellationToken ct = default)
    {
        RequireCreateMode(command);
        var apps = ParseUploaded(command.PblJson);
        if (apps.Count > 1)
            throw new BadRequestException("IMPORT_MULTIPLE_APPS", "This file contains several applications; import it with the multi-application import.");
        return await ImportOneAsync(apps[0], command.Mapping?.ExcludedTableRefs, null, ct);
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
        var mapping = command.Mapping ?? new ImportMapping();
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
                batch.Apps.Add(await ImportOneAsync(app, excluded, nameOverride, ct));
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

        return batch;
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
            .Select(i => new ImportSkippedItem { LogicalRef = i.ElementRef ?? "", Name = i.ElementRef ?? "", Reason = i.Message }));

        var tableSpecs = new List<TableSpec>();

        // Pass 1 input: scalar fields only. Formula fields are deferred to pass 2.
        foreach (var table in document.Tables ?? [])
        {
            var fieldSpecs = new List<AppFieldSpec>();

            // Labels must be unique per table, and PowerBase's own system fields (Record ID#, Date Created, …) already
            // hold theirs. A source field that reuses one of those labels, or repeats another field's, gets a numbered label.
            var usedLabels = new HashSet<string>(AppSeeder.SystemFieldLabels, StringComparer.OrdinalIgnoreCase);

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

                // Preserve the imported file's exact Name (its stable third-party identifier) rather
                // than regenerating one — see AppFieldSpec.Name.
                var item = BuildScalarItem(field, skipped);
                var uniqueLabel = UniqueLabel(item.Label.Trim(), usedLabels);
                if (!string.Equals(uniqueLabel, item.Label.Trim(), StringComparison.Ordinal))
                    skipped.Add(new ImportSkippedItem
                    {
                        LogicalRef = field.LogicalRef,
                        Name = field.Name,
                        Reason = $"Imported as '{uniqueLabel}': the label '{item.Label.Trim()}' is already used by another field on '{table.Name}'.",
                    });
                fieldSpecs.Add(new AppFieldSpec(
                    uniqueLabel, item.TypeCode, item.Settings, Name: item.Name, Description: item.Description, HelpText: item.HelpText,
                    IsRequired: item.IsRequired, DefaultValue: item.DefaultValue, IsAuditable: item.IsAuditable,
                    IsSearchable: item.IsSearchable, IsReportable: item.IsReportable));
            }

            // Only let the seeder add its stock "Main Form"/"List All"/"List Changes" when this
            // document doesn't bring its own — real exports overwhelmingly do (every table in a
            // real Quickbase export carries both a "Main Form" and its own "List All"), and
            // seeding alongside them leaves two of each with the *empty* seeded copy marked
            // default, so that's the one users would open.
            var suppliesOwnViews = (table.Reports ?? []).Count > 0
                || (document.Forms ?? []).Any(f => f.TableRef == table.LogicalRef);

            tableSpecs.Add(new TableSpec(
                table.Name,
                table.SingularLabel,
                table.PluralLabel,
                table.Icon,
                table.Description,
                Config: null,
                Fields: fieldSpecs,
                SeedDefaultViews: !suppliesOwnViews,
                Alias: table.AliasHint));
        }

        var createCommand = new CreateAppCommand(
            document.App.Name,
            document.App.Description,
            document.App.Icon,
            document.App.Color,
            tableSpecs);

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
            await _settingsApplier.ApplyAppSettingsAsync(document.App, createResult.PublicId, skipped, ct);
            await _settingsApplier.ApplyTableKeysAsync(document, createResult.PublicId, skipped, ct);
            ImportProgress.Report("Creating roles, relationships and formulas", 30);
            applied = await ApplyDocumentAsync(document, createResult.PublicId, null, skipped, formulaTranslations, ct);
            ImportProgress.Report("Applying table settings", 92);
            await _settingsApplier.ApplyTableSettingsAsync(document, createResult.PublicId, skipped, ct);
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
        CancellationToken ct)
    {
        var fieldsCreated = 0;
        var reportsCreated = 0;
        var relationshipsCreated = 0;
        var formsCreated = 0;
        var formRulesCreated = 0;
        var rolesCreated = 0;
        var relationships = document.Relationships ?? [];
        var forms = document.Forms ?? [];
        var roles = document.Roles ?? [];

        var hasFormulasReportsRelationshipsFormsOrRoles = relationships.Count > 0 || forms.Count > 0 || roles.Count > 0 || (document.Tables ?? []).Any(t =>
            (t.Fields ?? []).Any(IsFormulaField) || (t.Reports ?? []).Count > 0);

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

            // Formula fields must exist before forms and reports are built: a form element or
            // report column referencing a formula field can only resolve once that field has a
            // real Fid. Running this after CreateFormsAsync silently dropped every such element
            // (confirmed against a real export — 20 elements referencing successfully-created
            // formula fields went missing), so this pass stays ahead of both.
            var formulaCandidates = (document.Tables ?? [])
                .SelectMany(t => (t.Fields ?? []).Where(IsFormulaField).Select(f => (Table: t, Field: f)))
                .ToList();
            ImportProgress.Report("Creating formula fields", 55);
            var (formulasCreated, pendingFormulas) = await CreateFormulaFieldsAsync(formulaCandidates, tableByName, formulaTranslations, ct);
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
                    var (created, stillPending) = await CreateFormulaFieldsAsync(pendingFormulas, tableByName, formulaTranslations, ct);
                    fieldsCreated += created;
                    progress += created;
                    pendingFormulas = stillPending;
                }

                if (progress == 0)
                    break;
            }

            foreach (var l in deferredLookups)
                skipped.Add(new ImportSkippedItem { LogicalRef = l.Lookup.LogicalRef, Name = l.Lookup.Name, Reason = $"Lookup source field '{l.Lookup.SourceFieldName}' was not created on '{l.ParentTable.Name}'." });
            foreach (var sm in deferredSummaries)
                skipped.Add(new ImportSkippedItem { LogicalRef = sm.Summary.LogicalRef, Name = sm.Summary.Name, Reason = $"Summary target field '{sm.Summary.TargetFieldName}' was not created on '{sm.ChildTable.Name}'." });
            foreach (var (_, field) in pendingFormulas)
            {
                var diagnostics = formulaTranslations.FirstOrDefault(t => t.LogicalRef == field.LogicalRef)?.Diagnostics ?? [];
                skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Reason = $"Formula needs manual review: {string.Join("; ", diagnostics)}" });
            }

            ImportProgress.Report("Creating reports", 68);
            // Role refs → the roles this app has now, for report visibility.
            var roleIdsByRef = await ResolveRoleIdsByRefAsync(document, appPublicId, ct);

            // Reports go in before forms: an embedded related-records grid on a form points at a report that must already exist.
            var createdReports = new Dictionary<string, CreatedReport>(StringComparer.Ordinal);

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
                    // A column pointing at a field that didn't make it (a formula that failed
                    // to translate, an unsupported type) costs that column, not the whole
                    // report. Dropping the report outright would be badly disproportionate:
                    // "List All" means "every field in the table", so a single failed formula
                    // anywhere would take the table's main view down with it — and with the
                    // seeded default views suppressed for imported tables, that can leave a
                    // table with no reports at all.
                    var unresolved = report.Columns.Where(c => !fidByName.ContainsKey(c))
                        .Concat(report.SortFields.Select(s => s.FieldName).Where(n => !fidByName.ContainsKey(n)))
                        .Distinct()
                        .ToList();

                    var columns = report.Columns.Where(fidByName.ContainsKey).Select(c => fidByName[c]).ToList();
                    var sortFields = report.SortFields
                        .Where(s => fidByName.ContainsKey(s.FieldName))
                        .Select(s => new SortSpec { FieldId = fidByName[s.FieldName], Desc = s.Desc })
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

                    var createdReport = await CreateReportAsync(report, createdTable, allFields, columns, sortFields, roleIdsByRef, skipped, ct);
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
            }

            if (forms.Count > 0)
            {
                ImportProgress.Report("Creating forms and rules", 80);
                var createdFormLayouts = await CreateFormsAsync(forms, tableNameByRef, tableByName, tablesKeepingDefaults, createdReports, relationships, skipped, ct);
                formsCreated = createdFormLayouts.Count;

                if (forms.Any(f => f.Rules.Count > 0))
                    formRulesCreated = await CreateFormRulesAsync(forms, createdFormLayouts, tableNameByRef, tableByName, skipped, ct);
            }
        }


        // Role switches, record filters and field-level access need every field to exist.
        ImportProgress.Report("Applying permissions", 88);
        await _permissionsApplier.ApplyAsync(document, appPublicId, skipped, ct);

        ImportProgress.Report("Creating variables and pages", 90);
        var variablesCreated = await _extrasImporter.CreateVariablesAsync(document.Variables ?? [], appPublicId, skipped, ct);
        var pagesCreated = await _extrasImporter.CreatePagesAsync(document.Pages ?? [], appPublicId, skipped, ct);

        return new PblApplyResult(fieldsCreated, reportsCreated, relationshipsCreated, formsCreated, formRulesCreated, rolesCreated, variablesCreated, pagesCreated);
    }

    /// <summary>
    /// One scalar field's creation item. Settings/flags the target type rejects are dropped individually and reported
    /// (see <see cref="ImportFieldSanitizer"/>) so a single bad value never fails the table. Unique is applied afterwards
    /// (it needs a physical index), see <see cref="PblSettingsApplier"/>.
    /// </summary>
    public BulkCreateFieldItem BuildScalarItem(PblField field, List<ImportSkippedItem> skipped)
    {
        var label = (field.Label ?? field.Name).Trim();
        var clean = _fieldSanitizer.Sanitize(field.TypeCode, label, field.Settings, field.DefaultValue, field.IsRequired, field.IsUnique == true);
        foreach (var note in clean.Notes)
            skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Reason = note });

        return new BulkCreateFieldItem(
            field.TypeCode, label, field.Description, clean.IsRequired, field.IsAuditable == true, clean.Settings, clean.DefaultValue,
            IsEncrypted: false, Name: field.Name, HelpText: field.HelpText, IsSearchable: field.IsSearchable, IsReportable: field.IsReportable);
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
        Dictionary<string, Guid> roleIdsByRef, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
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
            Columns: columns,
            SortFields: sortFields,
            FilterTree: minimal ? null : filter,
            GroupByFieldId: null,
            GroupByMode: "EqualValues",
            HideTotals: !minimal && report.HideTotals == true,
            GroupDefaultCollapsed: minimal ? false : report.GroupDefaultCollapsed ?? false,
            GroupByDescending: false,
            Aggregations: [],
            DynamicFilterType: !minimal && report.DynamicFilterType == "Custom" && dynamicFields.Count > 0 ? "Custom" : !minimal && report.DynamicFilterType == "None" ? "None" : "Default",
            CustomDynamicFilterFields: minimal ? [] : dynamicFields,
            CustomDynamicFilterItems: null,
            AllowQuickSearch: minimal || report.AllowQuickSearch != false,
            VisibleToRoleIds: minimal ? null : visibleRoles,
            Options: minimal ? null : options,
            TableSortGroup: minimal ? null : tableSortGroup);

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
            var formulaItems = new List<BulkCreateFieldItem>();

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
                var translation = _formulaTranslator.Translate(field.ResultType, field.FormulaExpression, currentFields, aliasSchema);

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
                formulaItems.Add(new BulkCreateFieldItem(
                    formulaTypeCode, label, field.Description, IsRequired: false, IsAuditable: field.IsAuditable == true,
                    Settings: formulaSettings, Name: field.Name, HelpText: field.HelpText,
                    IsSearchable: field.IsSearchable, IsReportable: field.IsReportable));
            }

            if (formulaItems.Count > 0)
            {
                await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(createdTable.PublicId, formulaItems), ct);
                created += formulaItems.Count;
            }
        }

        return (created, stillPending);
    }

    /// <summary>Creates every relationship via <see cref="CreateRelationshipCommandHandler"/>,
    /// resolving each Lookup's parent-side source field and each Summary's child-side target
    /// field to real Fids first (both already exist from Pass 1 — <see cref="PblValidator"/>
    /// guarantees these names resolve before import ever reaches this point, so a lookup miss
    /// here reflects a table/field that itself failed to create, not a validator gap).</summary>
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
                    SummaryTargetValidator.Validate(function, targetFid, target, summary.TargetSubField);
                }
                catch (ValidationException ex)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = summary.LogicalRef, Name = summary.Name, Reason = string.Join(" ", ex.Errors.SelectMany(e => e.Value)) });
                    continue;
                }
                summarySpecs.Add(new CreateSummarySpec(summary.Label ?? summary.Name, function, targetFid, summary.TargetSubField));
            }

            try
            {
                var createdRel = await _createRelationshipHandler.HandleAsync(new CreateRelationshipCommand(
                    appPublicId,
                    parentTable.PublicId,
                    childTable.PublicId,
                    rel.ReferenceFieldLabel ?? rel.ReferenceFieldName,
                    rel.IsReferenceRequired,
                    lookupSpecs,
                    summarySpecs), ct);

                created++;
                deferredLookups.AddRange(pendingLookups.Select(l => new DeferredLookup(createdRel.PublicId, parentTable, l)));
                deferredSummaries.AddRange(pendingSummaries.Select(sm => new DeferredSummary(createdRel.PublicId, childTable, sm)));
            }
            catch (DomainException ex)
            {
                // One relationship PowerBase rejects must not take the whole import down with it.
                skipped.Add(new ImportSkippedItem { LogicalRef = rel.LogicalRef, Name = rel.ReferenceFieldName, Reason = $"Relationship not fully created: {(ex is ValidationException ve ? string.Join(" ", ve.Errors.SelectMany(e => e.Value)) : ex.Message)}" });
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
        Dictionary<string, Guid> ElementGuidByRef);

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
                            if (element.FieldName is null || !fidByName.TryGetValue(element.FieldName, out var fid))
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
                    ShowDividers: section.ShowDividers));
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

            result[form.LogicalRef] = new CreatedFormLayout(createdForm.Id, sectionGuidByRef, blockGuidByRef, elementGuidByRef);
        }

        return result;
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
                        conditionSpecs.Add(new FormRuleConditionSpec(fid, condition.Operator, condition.Value, condition.ValueType, null, condition.DisplayOrder));
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
                        ValueSource: action.ValueSource, SourceFieldId: sourceFieldId, MessageType: action.MessageType, MessageSeverity: action.MessageSeverity));
                }

                if (unresolvedAction || actionSpecs.Count == 0)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = rule.LogicalRef, Name = rule.Name, Reason = "Form rule action references a target that was not created." });
                    continue;
                }

                try
                {
                    var createdRule = await _createFormRuleHandler.HandleAsync(new CreateFormRuleCommand(layout.FormPublicId, Fit(rule.Name, FormTextMaxLength)!), ct);
                    await _saveFormRuleHandler.HandleAsync(new SaveFormRuleCommand(
                        createdRule.Id, Fit(rule.Name, FormTextMaxLength)!, rule.Description, Tags: null, rule.IsActive, rule.RunTrigger, rule.ConditionLogic,
                        rule.IsExpressionMode, rule.ExpressionText, conditionSpecs, actionSpecs, createdRule.RowVersion), ct);

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
