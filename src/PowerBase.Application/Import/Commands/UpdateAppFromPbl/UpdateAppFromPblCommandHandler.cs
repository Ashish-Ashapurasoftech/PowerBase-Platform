using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.BulkCreateFields;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Diff;
using PowerBase.Application.Import.Mapping;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Commands.UpdateAppFromPbl;

/// <summary>
/// Applies a QBL/PBL file onto an existing app — only what the user approved, and never deleting.
///
/// The server recomputes the diff itself (it never trusts classifications from the client), then:
///   • New elements are added only when not skipped; their tables/fields go in first, then the
///     shared import passes (formulas, relationships, forms, reports, roles, variables, pages) run
///     on just the approved subset via <see cref="ImportAppFromPblCommandHandler.ApplyDocumentAsync"/>.
///   • Changed elements are overwritten only with an explicit Overwrite decision. An undecided
///     Changed element rejects the whole request — there is no silent overwrite.
///   • Conflicts (e.g. a different field type) and anything the user skipped are left untouched.
///   • Elements only in the app are preserved.
///
/// Like Create, this cannot be wrapped in one transaction (the sub-handlers each own a connection
/// and DDL would hold schema locks for the whole run). Instead every element is applied
/// independently and its outcome reported; source refs are written for whatever now exists, so a
/// retry of the same import matches what was already added and does not create it again.
/// </summary>
public class UpdateAppFromPblCommandHandler
{
    private const string ADMINISTRATOR_ROLE = "Administrator";
    private const string COMMIT_MESSAGE = "Updated by app import";

    private readonly PblValidator _validator;
    private readonly ImportDiffBuilder _diffBuilder;
    private readonly ImportAppFromPblCommandHandler _importer;
    private readonly ImportSourceRefRecorder _recorder;
    private readonly IImportSourceRefRepository _sourceRefRepo;
    private readonly PblExtrasImporter _extrasImporter;
    private readonly BulkCreateFieldsCommandHandler _bulkCreateHandler;
    private readonly UpdateFieldCommandHandler _updateFieldHandler;
    private readonly IAppSeeder _appSeeder;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppAccessService _accessService;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;

    public UpdateAppFromPblCommandHandler(
        PblValidator validator, ImportDiffBuilder diffBuilder, ImportAppFromPblCommandHandler importer,
        ImportSourceRefRecorder recorder, IImportSourceRefRepository sourceRefRepo, PblExtrasImporter extrasImporter,
        BulkCreateFieldsCommandHandler bulkCreateHandler, UpdateFieldCommandHandler updateFieldHandler,
        IAppSeeder appSeeder, IAppRepository appRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo,
        IAppAccessService accessService, IAuditRepository auditRepo, IQueryContext queryContext)
    {
        _validator = validator;
        _diffBuilder = diffBuilder;
        _importer = importer;
        _recorder = recorder;
        _sourceRefRepo = sourceRefRepo;
        _extrasImporter = extrasImporter;
        _bulkCreateHandler = bulkCreateHandler;
        _updateFieldHandler = updateFieldHandler;
        _appSeeder = appSeeder;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _accessService = accessService;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
    }

    public async Task<ImportUpdateReport> HandleAsync(UpdateAppFromPblCommand command, CancellationToken ct = default)
    {
        await _accessService.RequireAppRoleAsync(command.TargetAppPublicId, ADMINISTRATOR_ROLE, ct);

        var mapping = command.Mapping ?? new ImportMapping();
        var (document, warnings) = PblDocumentTools.Load(command.Content, _validator);
        PblDocumentTools.ExcludeTables(document, mapping.ExcludedTableRefs);

        var diff = await _diffBuilder.BuildAsync(document, command.TargetAppPublicId, mapping, ct);
        var appId = await _appRepo.GetIdByPublicIdAsync(command.TargetAppPublicId, ct);

        var plan = ImportUpdatePlanner.Plan(diff, mapping);

        var outcomes = plan.Outcomes;
        var skipped = new List<ImportSkippedItem>(warnings);
        var formulaTranslations = new List<FormulaTranslationReportItem>();
        var failures = new List<string>();

        var tableItemByRef = diff.Items.Where(i => i.ElementType == ImportElementTypes.Table).ToDictionary(i => i.SourceLogicalRef, StringComparer.Ordinal);
        var docTableByRef = (document.Tables ?? []).ToDictionary(t => t.LogicalRef, StringComparer.Ordinal);
        var failedKeys = new HashSet<string>(StringComparer.Ordinal);

        // ---- Stage 1: new tables ----
        foreach (var tableRef in plan.AddKeys.Where(k => k.StartsWith(ImportElementTypes.Table + "|", StringComparison.Ordinal)).Select(k => k[(ImportElementTypes.Table.Length + 1)..]).ToList())
        {
            var table = docTableByRef[tableRef];
            var supplies = plan.AddKeys.Any(k => OwnsViewOf(k, tableRef, diff));
            try
            {
                var entity = new AppTable
                {
                    AppId = appId, Name = table.Name, SingularLabel = table.SingularLabel, PluralLabel = table.PluralLabel,
                    Description = table.Description, Icon = table.Icon, CreatedBy = _queryContext.UserId,
                };
                entity = await _appSeeder.CreateTableWithDefaultsAsync(entity, _queryContext.UserId, seedDefaultViews: !supplies, ct: ct);
                await _auditRepo.LogActivityAsync(AuditActions.Created, AuditEntityTypes.AppTable, entity.PublicId.ToString(),
                    $"Table added by import: {entity.Name}", appId: appId, ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedKeys.Add(ImportSourceRefRecorder.Key(ImportElementTypes.Table, tableRef));
                failures.Add($"Table '{table.Name}' could not be created: {ex.Message}");
                // Nothing that hangs off this table can be applied either.
                foreach (var k in plan.AddKeys.Where(k => BelongsToTable(k, tableRef, diff)).ToList())
                    plan.AddKeys.Remove(k);
            }
        }
        foreach (var k in failedKeys) plan.AddKeys.Remove(k);

        // ---- Stage 2: new scalar fields (formula fields are created by the shared passes) ----
        var tables = await _tableRepo.ListByAppAsync(appId, ct);
        var tableByName = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var existingNameByTableId = tables.ToDictionary(t => t.Id, t => t.Name);

        foreach (var table in document.Tables ?? [])
        {
            var tableName = ResolveExistingName(table, tableItemByRef, existingNameByTableId);
            if (!tableByName.TryGetValue(tableName, out var appTable))
                continue;

            var items = new List<BulkCreateFieldItem>();
            var pending = new List<PblField>();
            foreach (var field in (table.Fields ?? []).Where(f => plan.AddKeys.Contains(ImportSourceRefRecorder.Key(ImportElementTypes.Field, f.LogicalRef))))
            {
                if (string.Equals(field.TypeCode, PblValidator.FormulaTypeCode, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!PblValidator.SupportedFieldTypeCodes.Contains(field.TypeCode))
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Reason = $"Unsupported field type '{field.TypeCode}' for this import phase." });
                    plan.AddKeys.Remove(ImportSourceRefRecorder.Key(ImportElementTypes.Field, field.LogicalRef));
                    continue;
                }

                items.Add(new BulkCreateFieldItem(field.TypeCode, field.Label ?? field.Name, Settings: field.Settings, Name: field.Name));
                pending.Add(field);
            }

            if (items.Count == 0)
                continue;

            try
            {
                await _bulkCreateHandler.HandleAsync(new BulkCreateFieldsCommand(appTable.PublicId, items), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"Fields on '{table.Name}' could not be created: {ex.Message}");
                foreach (var f in pending)
                    plan.AddKeys.Remove(ImportSourceRefRecorder.Key(ImportElementTypes.Field, f.LogicalRef));
            }
        }

        // ---- Stage 3: shared import passes on the approved subset ----
        var subset = PblDocumentTools.Subset(document, plan.AddKeys);
        foreach (var subsetTable in subset.Tables)
            subsetTable.Name = ResolveExistingName(subsetTable, tableItemByRef, existingNameByTableId);

        var tablesKeepingDefaults = new HashSet<string>(
            (document.Tables ?? []).Where(t => tableItemByRef.TryGetValue(t.LogicalRef, out var i) && i.Status != ImportDiffStatus.New)
                .Select(t => ResolveExistingName(t, tableItemByRef, existingNameByTableId)),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            await _importer.ApplyDocumentAsync(subset, command.TargetAppPublicId, tablesKeepingDefaults, skipped, formulaTranslations, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add($"Some elements could not be applied: {ex.Message}. Nothing was deleted; re-running the import will not duplicate what was already added.");
        }

        // ---- Stage 4: approved overwrites ----
        foreach (var item in plan.Overwrites)
        {
            var (ok, reason) = await OverwriteAsync(item, document, appId, command.TargetAppPublicId, skipped, ct);
            outcomes.Add(new ImportElementOutcome
            {
                ElementType = item.ElementType, SourceLogicalRef = item.SourceLogicalRef, Name = item.Name, TableName = item.TableName,
                Outcome = ok ? ImportOutcome.Overwritten : ImportOutcome.Failed, Reason = reason,
            });
        }

        // ---- Stage 5: remember what now exists, so retries and later updates match it ----
        var recorded = await _recorder.RecordAsync(subset, command.TargetAppPublicId, ct);

        var matchedRefs = diff.Items
            .Where(i => i.TargetElementId.HasValue && i.Status != ImportDiffStatus.Conflict)
            .Select(i => (i.ElementType, i.SourceLogicalRef, i.TargetElementId!.Value))
            .ToList();
        await _sourceRefRepo.UpsertManyAsync(appId, matchedRefs, ct);

        // ---- Stage 6: outcome for every approved addition ----
        var skippedByRef = skipped.Where(s => !string.IsNullOrEmpty(s.LogicalRef)).GroupBy(s => s.LogicalRef).ToDictionary(g => g.Key, g => g.First().Reason);
        foreach (var item in diff.Items.Where(i => i.Status == ImportDiffStatus.New))
        {
            var key = ImportSourceRefRecorder.Key(item.ElementType, item.SourceLogicalRef);
            if (outcomes.Any(o => o.ElementType == item.ElementType && o.SourceLogicalRef == item.SourceLogicalRef))
                continue; // already decided (skipped by user, parent not added, ...)

            if (recorded.Contains(key) || (item.ElementType == ImportElementTypes.Table && !failedKeys.Contains(key) && plan.TableAdded(item.SourceLogicalRef) ))
                outcomes.Add(Outcome(item, ImportOutcome.Added, null));
            else if (skippedByRef.TryGetValue(item.SourceLogicalRef, out var reason))
                outcomes.Add(Outcome(item, ImportOutcome.Skipped, reason));
            else
                outcomes.Add(Outcome(item, ImportOutcome.Failed, "Not created — see the failures above."));
        }

        var appName = diff.TargetAppName;
        return new ImportUpdateReport
        {
            AppPublicId = command.TargetAppPublicId,
            AppName = appName,
            Elements = outcomes,
            Added = outcomes.Count(o => o.Outcome == ImportOutcome.Added),
            Overwritten = outcomes.Count(o => o.Outcome == ImportOutcome.Overwritten),
            SkippedCount = outcomes.Count(o => o.Outcome == ImportOutcome.Skipped),
            Unchanged = outcomes.Count(o => o.Outcome == ImportOutcome.Unchanged),
            Failed = outcomes.Count(o => o.Outcome == ImportOutcome.Failed),
            PreservedOnlyInApp = diff.OnlyInApp.Count,
            Failures = failures,
            Skipped = skipped,
            FormulaTranslations = formulaTranslations,
        };
    }

    private static ImportElementOutcome Outcome(ImportDiffItem item, string outcome, string? reason) =>
        ImportUpdatePlanner.Outcome(item, outcome, reason);

    private static bool OwnsViewOf(string key, string tableRef, ImportDiffResult diff) =>
        (key.StartsWith(ImportElementTypes.Report + "|", StringComparison.Ordinal) || key.StartsWith(ImportElementTypes.Form + "|", StringComparison.Ordinal))
        && diff.Items.Any(i => ImportSourceRefRecorder.Key(i.ElementType, i.SourceLogicalRef) == key && i.TableRef == tableRef);

    private static bool BelongsToTable(string key, string tableRef, ImportDiffResult diff) =>
        diff.Items.Any(i => i.TableRef == tableRef && i.ElementType != ImportElementTypes.Table && ImportSourceRefRecorder.Key(i.ElementType, i.SourceLogicalRef) == key);

    /// <summary>The table's current name in the app: the matched table's own name, or the file's
    /// name for a table that is new (just created under that name).</summary>
    private static string ResolveExistingName(PblTable table, Dictionary<string, ImportDiffItem> tableItemByRef, Dictionary<long, string> existingNameByTableId)
    {
        if (tableItemByRef.TryGetValue(table.LogicalRef, out var item)
            && item.TargetElementId.HasValue
            && existingNameByTableId.TryGetValue(item.TargetElementId.Value, out var existingName))
            return existingName;
        return table.Name;
    }

    // ----- Overwrites (label/description/value only) -----

    private async Task<(bool Ok, string? Reason)> OverwriteAsync(
        ImportDiffItem item, PblDocument document, long appId, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        try
        {
            switch (item.ElementType)
            {
                case ImportElementTypes.Table:
                {
                    var table = document.Tables!.First(t => t.LogicalRef == item.SourceLogicalRef);
                    var existing = await _tableRepo.GetByPublicIdAsync(item.TargetElementPublicId!.Value, ct);
                    await _tableRepo.UpdateAsync(
                        existing.PublicId, existing.Name,
                        string.IsNullOrWhiteSpace(table.SingularLabel) ? existing.SingularLabel : table.SingularLabel,
                        string.IsNullOrWhiteSpace(table.PluralLabel) ? existing.PluralLabel : table.PluralLabel,
                        string.IsNullOrWhiteSpace(table.Description) ? existing.Description : table.Description,
                        existing.Icon,
                        existing.DefaultRecordPickerField1Id, existing.DefaultRecordPickerField2Id, existing.DefaultRecordPickerField3Id,
                        isShowInBar: null, ct: ct);
                    return (true, null);
                }
                case ImportElementTypes.Field:
                {
                    var field = document.Tables!.SelectMany(t => t.Fields ?? []).First(f => f.LogicalRef == item.SourceLogicalRef);
                    var existing = await _fieldRepo.GetByPublicIdAsync(item.TargetElementPublicId!.Value, ct)
                                   ?? throw new NotFoundException("Field", item.TargetElementPublicId!.Value);
                    var table = await _tableRepo.GetByIdAsync(existing.AppTableId, ct);
                    await _updateFieldHandler.HandleAsync(new UpdateFieldCommand(
                        table.PublicId, existing.PublicId,
                        field.Label ?? field.Name,
                        string.IsNullOrWhiteSpace(field.Description) ? existing.Description : field.Description,
                        existing.IsRequired, existing.DefaultValue,
                        existing.IsSearchable, existing.IsSortable, existing.IsFilterable, existing.IsReportable,
                        existing.IsAuditable, existing.IsUnique, existing.IsEncrypted, existing.IsAutoFill,
                        existing.Settings, COMMIT_MESSAGE), ct);
                    return (true, null);
                }
                case ImportElementTypes.Variable:
                {
                    var variable = document.Variables!.First(v => v.LogicalRef == item.SourceLogicalRef);
                    var ok = await _extrasImporter.OverwriteVariableAsync(variable, appPublicId, item.TargetElementPublicId!.Value, skipped, ct);
                    return ok ? (true, null) : (false, "Variable could not be updated — see the skipped list.");
                }
                default:
                    return (false, "This element type cannot be overwritten.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
