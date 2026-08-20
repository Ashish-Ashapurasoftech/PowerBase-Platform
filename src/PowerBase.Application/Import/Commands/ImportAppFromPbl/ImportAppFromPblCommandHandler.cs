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
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using YamlDotNet.Core;

namespace PowerBase.Application.Import.Commands.ImportAppFromPbl;

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
        UpdateTablePermissionsCommandHandler updateTablePermissionsHandler)
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
    }

    public async Task<ImportReport> HandleAsync(ImportAppFromPblCommand command, CancellationToken ct = default)
    {
        if (command.Mode != ImportMode.CreateNewApp)
            throw new BadRequestException("UNSUPPORTED_IMPORT_MODE", $"Import mode '{command.Mode}' is not supported yet.");

        PblDocument document;
        List<PblIssue> conversionIssues;
        try
        {
            (document, conversionIssues) = ImportDocumentParser.Parse(command.PblJson);
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

        var conversionErrors = conversionIssues.Where(i => i.Severity == PblIssueSeverity.Error).ToList();
        if (conversionErrors.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Qbl"] = conversionErrors.Select(e => e.Message).ToArray(),
            });

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
                fieldSpecs.Add(new AppFieldSpec(field.Label ?? field.Name, field.TypeCode, field.Settings, Name: field.Name));
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
                SeedDefaultViews: !suppliesOwnViews));
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
        var relationshipsCreated = 0;
        var formsCreated = 0;
        var formRulesCreated = 0;
        var rolesCreated = 0;
        var relationships = document.Relationships ?? [];
        var forms = document.Forms ?? [];
        var roles = document.Roles ?? [];

        var hasFormulasReportsRelationshipsFormsOrRoles = relationships.Count > 0 || forms.Count > 0 || roles.Count > 0 || (document.Tables ?? []).Any(t =>
            (t.Fields ?? []).Any(IsFormulaField) || (t.Reports ?? []).Count > 0);

        // Everything past this point builds onto the app just created. A single transaction around
        // it isn't viable — these handlers each open their own connection, and holding one open
        // across ~46 CREATE TABLEs and hundreds of ADD COLUMNs would keep schema locks on a shared
        // tenant database for the whole run. Because CreateNewApp only ever builds a brand-new app,
        // deleting it on failure gives the same all-or-nothing result the caller cares about
        // without blocking every other user of that tenant.
        try
        {
            if (hasFormulasReportsRelationshipsFormsOrRoles)
            {
                var appId = await _appRepo.GetIdByPublicIdAsync(createResult.PublicId, ct);
                var createdTables = await _tableRepo.ListByAppAsync(appId, ct);
                var tableByName = createdTables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
                var tableNameByRef = (document.Tables ?? []).ToDictionary(t => t.LogicalRef, t => t.Name, StringComparer.Ordinal);

                if (roles.Count > 0)
                    rolesCreated = await CreateRolesAsync(roles, tableNameByRef, tableByName, createResult.PublicId, skipped, ct);

                if (relationships.Count > 0)
                    relationshipsCreated = await CreateRelationshipsAsync(relationships, tableNameByRef, tableByName, createResult.PublicId, skipped, ct);

                // Formula fields must exist before forms and reports are built: a form element or
                // report column referencing a formula field can only resolve once that field has a
                // real Fid. Running this after CreateFormsAsync silently dropped every such element
                // (confirmed against a real export — 20 elements referencing successfully-created
                // formula fields went missing), so this pass stays ahead of both.
                fieldsCreated += await CreateFormulaFieldsAsync(document, tableByName, formulaTranslations, skipped, ct);

                if (forms.Count > 0)
                {
                    var createdFormLayouts = await CreateFormsAsync(forms, tableNameByRef, tableByName, skipped, ct);
                    formsCreated = createdFormLayouts.Count;

                    if (forms.Any(f => f.Rules.Count > 0))
                        formRulesCreated = await CreateFormRulesAsync(forms, createdFormLayouts, tableNameByRef, tableByName, skipped, ct);
                }

                foreach (var table in document.Tables ?? [])
                {
                    if (!tableByName.TryGetValue(table.Name, out var createdTable))
                        continue; // defensive: every PBL table was just created by CreateAppCommandHandler

                    var pblReports = table.Reports ?? [];
                    if (pblReports.Count == 0)
                        continue;

                    var allFields = await _fieldRepo.ListByTableAsync(createdTable.Id, ct);
                    var fidByName = allFields
                        .Where(f => f.Fid.HasValue)
                        .ToDictionary(f => f.Name, f => (long)f.Fid!.Value, StringComparer.OrdinalIgnoreCase);

                    var tableHasDefaultReport = false;

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

                        var createdReport = await _createReportHandler.HandleAsync(new CreateReportCommand(
                            createdTable.PublicId,
                            report.Name,
                            Description: null,
                            Visibility: "Shared",
                            ReportType: report.ReportType,
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

                        // Same reasoning as forms: reports are created IsDefault=false and the seeded
                        // default was suppressed, so the first one that imports for a table becomes it.
                        if (!tableHasDefaultReport)
                        {
                            await _reportRepo.SetDefaultAsync(createdTable.PublicId, createdReport.Id, ct);
                            tableHasDefaultReport = true;
                        }

                        reportsCreated++;
                    }
                }
            }
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

        return new ImportReport
        {
            AppPublicId = createResult.PublicId,
            AppName = createResult.Name,
            TablesCreated = tableSpecs.Count,
            FieldsCreated = fieldsCreated,
            ReportsCreated = reportsCreated,
            RelationshipsCreated = relationshipsCreated,
            FormsCreated = formsCreated,
            FormRulesCreated = formRulesCreated,
            RolesCreated = rolesCreated,
            Skipped = skipped,
            FormulaTranslations = formulaTranslations,
        };
    }

    /// <summary>Translates and creates every table's formula fields, returning how many were
    /// created. Runs after relationships (a formula can reference a Lookup/Summary field) but
    /// before forms and reports (both can reference a formula field). A formula that fails to
    /// compile against the table's real schema is reported and skipped — never created with a
    /// broken expression.</summary>
    private async Task<int> CreateFormulaFieldsAsync(
        PblDocument document,
        Dictionary<string, AppTable> tableByName,
        List<FormulaTranslationReportItem> formulaTranslations,
        List<ImportSkippedItem> skipped,
        CancellationToken ct)
    {
        var created = 0;

        foreach (var table in document.Tables ?? [])
        {
            if (!tableByName.TryGetValue(table.Name, out var createdTable))
                continue; // defensive: every PBL table was just created by CreateAppCommandHandler

            var formulaFields = (table.Fields ?? []).Where(IsFormulaField).ToList();
            if (formulaFields.Count == 0)
                continue;

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

                // Preserve the imported file's exact Name (its stable third-party identifier) rather
                // than regenerating one — see BulkCreateFieldItem.Name.
                formulaItems.Add(new BulkCreateFieldItem("Formula", field.Label ?? field.Name, Settings: translation.SettingsJson, Name: field.Name));
            }

            if (formulaItems.Count > 0)
            {
                await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(createdTable.PublicId, formulaItems), ct);
                created += formulaItems.Count;
            }
        }

        return created;
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
            var parentFidByName = parentFields.Where(f => f.Fid.HasValue).ToDictionary(f => f.Name, f => f.Fid!.Value, StringComparer.OrdinalIgnoreCase);
            var childFidByName = childFields.Where(f => f.Fid.HasValue).ToDictionary(f => f.Name, f => f.Fid!.Value, StringComparer.OrdinalIgnoreCase);

            var lookupSpecs = new List<CreateLookupSpec>();
            foreach (var lookup in rel.Lookups)
            {
                if (!parentFidByName.TryGetValue(lookup.SourceFieldName, out var sourceFid))
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = lookup.LogicalRef, Name = lookup.Name, Reason = $"Lookup source field '{lookup.SourceFieldName}' was not created on '{parentName}'." });
                    continue;
                }
                // Note: relationship-created fields don't carry the Name-preservation escape hatch that
                // plain/formula fields do (see AppFieldSpec.Name) — re-importing a file with lookups
                // gets freshly generated Names rather than the originally-exported ones.
                lookupSpecs.Add(new CreateLookupSpec(sourceFid, lookup.Label ?? lookup.Name, lookup.SourceSubField));
            }

            var summarySpecs = new List<CreateSummarySpec>();
            foreach (var summary in rel.Summaries)
            {
                int? targetFid = null;
                if (summary.TargetFieldName is not null)
                {
                    if (!childFidByName.TryGetValue(summary.TargetFieldName, out var fid))
                    {
                        skipped.Add(new ImportSkippedItem { LogicalRef = summary.LogicalRef, Name = summary.Name, Reason = $"Summary target field '{summary.TargetFieldName}' was not created on '{childName}'." });
                        continue;
                    }
                    targetFid = fid;
                }
                summarySpecs.Add(new CreateSummarySpec(summary.Label ?? summary.Name, summary.Function, targetFid, summary.TargetSubField));
            }

            await _createRelationshipHandler.HandleAsync(new CreateRelationshipCommand(
                appPublicId,
                parentTable.PublicId,
                childTable.PublicId,
                rel.ReferenceFieldLabel ?? rel.ReferenceFieldName,
                rel.IsReferenceRequired,
                lookupSpecs,
                summarySpecs), ct);

            created++;
        }

        return created;
    }

    /// <summary>Guids assigned to sections/blocks/elements while saving a form's layout are
    /// only meaningful within this single import call — <see cref="CreateFormRulesAsync"/> uses
    /// them to re-fetch the layout and match the real, persisted DbIds back to the
    /// PblForm*.LogicalRef each Guid was generated for. PowerBase itself has no need for these
    /// ids to be stable across requests.</summary>
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
            var fidByName = tableFields.Where(f => f.Fid.HasValue).ToDictionary(f => f.Name, f => (long)f.Fid!.Value, StringComparer.OrdinalIgnoreCase);

            var sectionGuidByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var blockGuidByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var elementGuidByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var sectionLayouts = new List<FormSectionLayout>();

            foreach (var section in form.Sections)
            {
                var blockLayouts = new List<FormBlockLayout>();

                foreach (var block in section.Blocks)
                {
                    var elementLayouts = new List<FormElementLayout>();

                    foreach (var element in block.Elements)
                    {
                        long? appFieldId = null;
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
                        elementLayouts.Add(new FormElementLayout(
                            elementGuid, appFieldId, element.ElementType, element.ElementContent,
                            element.LabelMode, element.CustomLabel, element.ShowOnAdd, element.ShowOnEdit, element.ShowOnView,
                            element.WidthMode, element.WidthValue, element.HelpTextOverride, element.IsReadOnly, element.IsRequired, element.DisplayAs));
                    }

                    var blockGuid = Guid.NewGuid();
                    blockGuidByRef[block.LogicalRef] = blockGuid;
                    blockLayouts.Add(new FormBlockLayout(blockGuid, block.Heading, block.BackgroundColor, block.Width, elementLayouts));
                }

                var sectionGuid = Guid.NewGuid();
                sectionGuidByRef[section.LogicalRef] = sectionGuid;
                sectionLayouts.Add(new FormSectionLayout(sectionGuid, section.Name, section.IsCollapsed, blockLayouts));
            }

            if (sectionLayouts.Count == 0)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = form.LogicalRef, Name = form.Name, Reason = "Form has no sections after resolving fields; skipped." });
                continue;
            }

            var createdForm = await _createFormHandler.HandleAsync(new CreateFormCommand(table.PublicId, form.Name), ct);
            await _saveFormLayoutHandler.HandleAsync(new SaveFormLayoutCommand(createdForm.Id, sectionLayouts), ct);

            // Forms are created IsDefault=false, and the seeder's default form was suppressed for
            // any table supplying its own — so without this the table would have no default form
            // at all. First imported form for a table wins, matching document order.
            if (defaultedTableRefs.Add(form.TableRef))
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
            var fidByName = tableFields.Where(f => f.Fid.HasValue).ToDictionary(f => f.Name, f => (long)f.Fid!.Value, StringComparer.OrdinalIgnoreCase);

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

                    actionSpecs.Add(new FormRuleActionSpec(action.ActionType, action.TargetType, targetElementId, targetSectionId, targetBlockId, action.ActionValue, action.DisplayOrder));
                }

                if (unresolvedAction || actionSpecs.Count == 0)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = rule.LogicalRef, Name = rule.Name, Reason = "Form rule action references a target that was not created." });
                    continue;
                }

                var createdRule = await _createFormRuleHandler.HandleAsync(new CreateFormRuleCommand(layout.FormPublicId, rule.Name), ct);
                await _saveFormRuleHandler.HandleAsync(new SaveFormRuleCommand(
                    createdRule.Id, rule.Name, rule.Description, Tags: null, rule.IsActive, rule.RunTrigger, rule.ConditionLogic,
                    rule.IsExpressionMode, rule.ExpressionText, conditionSpecs, actionSpecs, createdRule.RowVersion), ct);

                created++;
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
                var createdRole = await _createAppRoleHandler.HandleAsync(new CreateAppRoleCommand(appPublicId, role.Name, role.IsDefault), ct);
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

    private static bool IsFormulaField(PblField field) =>
        string.Equals(field.TypeCode, PblValidator.FormulaTypeCode, StringComparison.OrdinalIgnoreCase);
}
