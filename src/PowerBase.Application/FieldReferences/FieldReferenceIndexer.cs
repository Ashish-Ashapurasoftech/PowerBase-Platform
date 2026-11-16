using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences.Extractors;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Application.FieldReferences;

/// <summary>
/// Rebuilds meta.FieldReference rows from the real sources. Every Reindex* loads one source,
/// runs its extractor and replaces that source's rows wholesale (delete + insert), so the index
/// can never accumulate stale rows for something that changed. Failures are logged, never thrown:
/// the index is derived data and must not break the save that triggered it.
/// </summary>
public sealed class FieldReferenceIndexer : IFieldReferenceIndexer
{
    private readonly IFieldReferenceRepository _references;
    private readonly IAppFieldRepository _fields;
    private readonly IAppTableRepository _tables;
    private readonly IReportRepository _reports;
    private readonly IFormRepository _forms;
    private readonly IFormRuleRepository _rules;
    private readonly FormulaEngine _engine;
    private readonly ILogger<FieldReferenceIndexer> _logger;

    public FieldReferenceIndexer(
        IFieldReferenceRepository references,
        IAppFieldRepository fields,
        IAppTableRepository tables,
        IReportRepository reports,
        IFormRepository forms,
        IFormRuleRepository rules,
        FormulaEngine engine,
        ILogger<FieldReferenceIndexer> logger)
    {
        _references = references;
        _fields = fields;
        _tables = tables;
        _reports = reports;
        _forms = forms;
        _rules = rules;
        _engine = engine;
        _logger = logger;
    }

    // ── Single-source reindex (called by the command handlers after a save) ─────────────────────

    public Task ReindexReportAsync(Guid reportPublicId, CancellationToken ct = default) =>
        SafelyAsync($"report {reportPublicId}", async () =>
        {
            Report report;
            try { report = await _reports.GetByPublicIdAsync(reportPublicId, ct); }
            catch (NotFoundException)
            {
                await _references.DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Report, reportPublicId, ct);
                return;
            }
            var tables = new TableIndexCache(_fields, _tables);
            await IndexReportAsync(report, await RequireTableAsync(tables, report.AppTableId, ct), ct);
        });

    public Task ReindexFormAsync(Guid formPublicId, CancellationToken ct = default) =>
        SafelyAsync($"form {formPublicId}", async () =>
        {
            Form form;
            try { form = await _forms.GetByPublicIdAsync(formPublicId, ct); }
            catch (NotFoundException)
            {
                await RemoveFormRowsAsync(formPublicId, ct);
                return;
            }
            var tables = new TableIndexCache(_fields, _tables);
            await IndexFormAsync(form, await RequireTableAsync(tables, form.AppTableId, ct), ct);
        });

    public Task ReindexFormRuleAsync(Guid rulePublicId, CancellationToken ct = default) =>
        SafelyAsync($"form rule {rulePublicId}", async () =>
        {
            FormRule rule;
            try { rule = await _rules.GetByPublicIdAsync(rulePublicId, ct); }
            catch (NotFoundException)
            {
                await _references.DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.FormRule, rulePublicId, ct);
                return;
            }
            if (await _forms.GetTableIdByFormIdAsync(rule.FormId, ct) is not { } tableId) return;

            var tables = new TableIndexCache(_fields, _tables);
            var layout = await _forms.GetLayoutAsync(rule.FormId, ct);
            await IndexRuleAsync(rule, tableId, ElementFids(layout), await RequireTableAsync(tables, tableId, ct), ct);
        });

    public Task ReindexFieldAsync(Guid fieldPublicId, CancellationToken ct = default) =>
        SafelyAsync($"field {fieldPublicId}", async () =>
        {
            var field = await _fields.GetByPublicIdAsync(fieldPublicId, ct);
            if (field is null || field.IsDeleted)
            {
                await _references.DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Field, fieldPublicId, ct);
                return;
            }
            var tables = new TableIndexCache(_fields, _tables);
            await IndexFieldAsync(field, await RequireTableAsync(tables, field.AppTableId, ct), tables, ct);
        });

    public Task ReindexRelationshipAsync(Guid relationshipPublicId, CancellationToken ct = default) =>
        SafelyAsync($"relationship {relationshipPublicId}", async () =>
        {
            var relationship = await _references.GetRelationshipSourceAsync(relationshipPublicId, ct);
            if (relationship is null)
            {
                await _references.DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Relationship, relationshipPublicId, ct);
                return;
            }
            await IndexRelationshipAsync(relationship, ct);
        });

    public Task ReindexDashboardAsync(Guid pagePublicId, CancellationToken ct = default) =>
        SafelyAsync($"dashboard {pagePublicId}", async () =>
        {
            var page = await _references.GetDashboardSourceAsync(pagePublicId, ct);
            if (page is null)
            {
                await _references.DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Dashboard, pagePublicId, ct);
                return;
            }
            var rows = await DashboardRowsAsync(page, new TableIndexCache(_fields, _tables), ct);
            // The page may use fields of several tables, so replace every row of the page, whatever table it is filed under.
            await _references.ReplaceForSourceAsync(FieldReferenceSourceTypes.Dashboard, page.Id, rows, ct);
        });

    public Task ReindexCustomRulesAsync(Guid rolePublicId, CancellationToken ct = default) =>
        SafelyAsync($"custom rules of role {rolePublicId}", async () =>
        {
            if (await _references.GetRoleIdAsync(rolePublicId, ct) is not { } roleId)
            {
                await _references.DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.CustomRule, rolePublicId, ct);
                return;
            }
            var rules = await _references.ListCustomRulesOfRoleAsync(roleId, ct);
            var rows = await CustomRuleRowsAsync(rules, new TableIndexCache(_fields, _tables), ct);
            // Saving a role's rules replaces all of them at once, so all of the role's rows are replaced together.
            await _references.ReplaceForSourceAsync(FieldReferenceSourceTypes.CustomRule, roleId, rows, ct);
        });

    public Task ReindexImportAsync(Guid importPublicId, CancellationToken ct = default) =>
        SafelyAsync($"import {importPublicId}", async () =>
        {
            var import = await _references.GetImportDefinitionAsync(importPublicId, ct);
            if (import is null)
            {
                await _references.DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Import, importPublicId, ct);
                return;
            }
            // An import can touch several tables, so every row of the import is replaced, whatever table it is filed under.
            await _references.ReplaceForSourceAsync(
                FieldReferenceSourceTypes.Import, import.Id,
                await ImportReferenceExtractor.ExtractAsync(import, new TableIndexCache(_fields, _tables), _engine, ct), ct);
        });

    public Task ReindexPipelineAsync(Guid pipelinePublicId, CancellationToken ct = default) =>
        SafelyAsync($"PowerFlow {pipelinePublicId}", () => IndexPipelineAsync(pipelinePublicId, ct));

    private async Task IndexPipelineAsync(Guid pipelinePublicId, CancellationToken ct)
    {
        var pipeline = await _references.GetPipelineSourceAsync(pipelinePublicId, ct);
        if (pipeline is null)
        {
            await _references.DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.PowerFlow, pipelinePublicId, ct);
            return;
        }
        // A PowerFlow can use fields of several tables, so every row of the pipeline is replaced, whatever table it is filed under.
        await _references.ReplaceForSourceAsync(
            FieldReferenceSourceTypes.PowerFlow, pipeline.Id, await PipelineRowsAsync(pipeline, new TableIndexCache(_fields, _tables), null, ct), ct);
    }

    public async Task<int> SyncPipelinesAsync(CancellationToken ct = default)
    {
        var count = 0;
        try
        {
            var since = await _references.GetPipelineSyncWatermarkAsync(ct);
            // The clocks of the app server and the database can differ a little, so look back a few minutes; re-indexing is idempotent.
            var (changed, scannedUpTo) = await _references.ListPipelinesChangedSinceAsync(since?.AddMinutes(-5), ct);
            foreach (var id in changed)
            {
                await IndexPipelineAsync(id, ct);
                count++;
            }
            await _references.SetPipelineSyncWatermarkAsync(scannedUpTo, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PowerFlow field-reference sync failed after {Count} PowerFlows; the Usage tab may miss recent PowerFlow edits until the next sync.", count);
        }
        return count;
    }

    public Task ReindexTableFieldsAsync(long tableId, CancellationToken ct = default) =>
        SafelyAsync($"fields of table {tableId}", async () =>
        {
            var tables = new TableIndexCache(_fields, _tables);
            var table = await RequireTableAsync(tables, tableId, ct);
            foreach (var field in table.Fields)
                await IndexFieldAsync(field, table, tables, ct);
        });

    public Task RemoveSourceAsync(string sourceType, Guid sourcePublicId, CancellationToken ct = default) =>
        SafelyAsync($"remove {sourceType} {sourcePublicId}", () =>
            _references.DeleteBySourcePublicIdAsync(sourceType, sourcePublicId, ct));

    public Task RemoveFormAsync(Guid formPublicId, CancellationToken ct = default) =>
        SafelyAsync($"remove form {formPublicId}", () => RemoveFormRowsAsync(formPublicId, ct));

    private async Task RemoveFormRowsAsync(Guid formPublicId, CancellationToken ct)
    {
        await _references.DeleteRuleRowsOfFormAsync(formPublicId, ct);
        await _references.DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Form, formPublicId, ct);
    }

    // ── Whole-table rebuild (backfill + repair) ─────────────────────────────────────────────────

    public async Task RebuildTableAsync(long tableId, CancellationToken ct = default)
    {
        var tables = new TableIndexCache(_fields, _tables);
        var table = await RequireTableAsync(tables, tableId, ct);
        var appTable = await _tables.GetByIdAsync(tableId, ct);
        var tablePublicId = appTable.PublicId;

        // Start clean so rows left behind by sources that no longer exist disappear too.
        await _references.ClearTableAsync(tableId, ct);

        foreach (var report in await _references.ListReportSourcesAsync(tableId, ct))
            await IndexReportAsync(report, table, ct);

        foreach (var form in await _forms.ListByTableAsync(tablePublicId, ct))
            await IndexFormAsync(form, table, ct);

        foreach (var field in table.Fields)
            await IndexFieldAsync(field, table, tables, ct);

        // Relationships are filed under their child table, so this table's own are rebuilt as a whole.
        foreach (var relationship in await _references.ListRelationshipsOfChildTableAsync(tableId, ct))
            await IndexRelationshipAsync(relationship, ct);

        // Dashboards and a role's Custom Rules can touch several tables, and the clear above only removed the rows filed
        // under THIS table — so only this table's rows are put back (insert-only), never the source's rows on other tables.
        foreach (var page in await _references.ListDashboardSourcesAsync(appTable.AppId, ct))
            await _references.InsertRowsAsync(
                (await DashboardRowsAsync(page, tables, ct)).Where(r => r.SourceTableId == tableId).ToList(), ct);

        await _references.InsertRowsAsync(
            await CustomRuleRowsAsync(await _references.ListCustomRulesOfTableAsync(tableId, ct), tables, ct), ct);

        // PowerFlows likewise: only the rows for fields of THIS table are asked for (and put back, insert-only).
        foreach (var pipeline in await _references.ListPipelineSourcesAsync(appTable.AppId, ct))
            await _references.InsertRowsAsync(await PipelineRowsAsync(pipeline, tables, tablePublicId, ct), ct);

        // Imports likewise: this table's rows only, insert-only.
        foreach (var import in await _references.ListImportDefinitionsAsync(appTable.AppId, ct))
            await _references.InsertRowsAsync(
                (await ImportReferenceExtractor.ExtractAsync(import, tables, _engine, ct)).Where(r => r.SourceTableId == tableId).ToList(), ct);

        await _references.MarkTableIndexedAsync(tableId, ct);
    }

    public Task EnsureTableIndexedAsync(long tableId, CancellationToken ct = default) =>
        SafelyAsync($"backfill of table {tableId}", async () =>
        {
            if (!await _references.IsTableIndexedAsync(tableId, ct))
                await RebuildTableAsync(tableId, ct);
        });

    public Task EnsureTablesIndexedAsync(IReadOnlyCollection<long> tableIds, CancellationToken ct = default) =>
        SafelyAsync($"backfill of {tableIds.Count} tables", async () =>
        {
            var indexed = await _references.ListIndexedTableIdsAsync(tableIds, ct);
            foreach (var tableId in tableIds.Where(id => !indexed.Contains(id)))
            {
                // One table failing must not leave the others without their rows.
                try { await RebuildTableAsync(tableId, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Field reference backfill failed for table {TableId}; the Usage tab may be incomplete for it until it is rebuilt.", tableId);
                }
            }
        });

    // ── Per-source indexing ─────────────────────────────────────────────────────────────────────

    private async Task IndexRelationshipAsync(RelationshipSource relationship, CancellationToken ct)
    {
        var parent = await _tables.GetByIdAsync(relationship.ParentTableId, ct);
        var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.Relationship, relationship.Id, relationship.ChildTableId);
        RelationshipReferenceExtractor.Extract(relationship, parent.KeyFieldId, collector);
        await _references.ReplaceForSourceAsync(FieldReferenceSourceTypes.Relationship, relationship.Id, collector.Rows, ct);
    }

    private async Task<IReadOnlyCollection<FieldReferenceRow>> DashboardRowsAsync(DashboardSource page, TableIndexCache tables, CancellationToken ct)
    {
        var reportTables = await _references.GetReportTableIdsAsync(DashboardReferenceExtractor.ReportIds(page.Definition), ct);
        return await DashboardReferenceExtractor.ExtractAsync(page, new DashboardResolver(reportTables, _tables, tables), ct);
    }

    /// <summary>The rows of one PowerFlow. <paramref name="onlyTable"/> limits it to the fields of one table (a table rebuild).</summary>
    private static async Task<IReadOnlyCollection<FieldReferenceRow>> PipelineRowsAsync(
        PipelineSource pipeline, TableIndexCache tables, Guid? onlyTable, CancellationToken ct)
    {
        var rows = new List<FieldReferenceRow>();
        foreach (var tablePublicId in PipelineReferenceExtractor.TablesMentioned(pipeline))
        {
            if (onlyTable.HasValue && tablePublicId != onlyTable.Value) continue;
            var table = await tables.GetByPublicIdAsync(tablePublicId, ct);
            if (table is null) continue; // not a table of this account (a connection's table, or a stale id)
            var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.PowerFlow, pipeline.Id, table.TableId);
            PipelineReferenceExtractor.Extract(pipeline, tablePublicId, table, collector);
            rows.AddRange(collector.Rows);
        }
        return rows;
    }

    private static async Task<IReadOnlyCollection<FieldReferenceRow>> CustomRuleRowsAsync(
        IReadOnlyList<CustomRuleSource> rules, TableIndexCache tables, CancellationToken ct)
    {
        var rows = new List<FieldReferenceRow>();
        foreach (var rule in rules)
        {
            var table = await tables.GetByIdAsync(rule.TableId, ct);
            if (table is null) continue;
            var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.CustomRule, rule.RoleId, rule.TableId);
            CustomRuleReferenceExtractor.Extract(rule, table, collector);
            rows.AddRange(collector.Rows);
        }
        return rows;
    }

    private async Task IndexReportAsync(Report report, TableFieldIndex table, CancellationToken ct)
    {
        var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.Report, report.Id, report.AppTableId);
        ReportReferenceExtractor.Extract(report.Definition, table, collector, _engine);
        await _references.ReplaceForSourceAsync(FieldReferenceSourceTypes.Report, report.Id, collector.Rows, ct);
    }

    /// <summary>A form's own elements, then each rule defined on it (a rule's action targets are
    /// form elements, so they resolve through this same layout).</summary>
    private async Task IndexFormAsync(Form form, TableFieldIndex table, CancellationToken ct)
    {
        var layout = await _forms.GetLayoutAsync(form.Id, ct);

        var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.Form, form.Id, form.AppTableId);
        FormReferenceExtractor.Extract(layout, table, collector);
        await _references.ReplaceForSourceAsync(FieldReferenceSourceTypes.Form, form.Id, collector.Rows, ct);

        var elementFids = ElementFids(layout);
        foreach (var rule in await _rules.ListByFormAsync(form.Id, ct))
            await IndexRuleAsync(rule, form.AppTableId, elementFids, table, ct);
    }

    private async Task IndexRuleAsync(FormRule rule, long tableId, IReadOnlyDictionary<long, long?> elementFids, TableFieldIndex table, CancellationToken ct)
    {
        var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.FormRule, rule.Id, tableId);
        FormRuleReferenceExtractor.Extract(rule, elementFids, table, _engine, collector);
        await _references.ReplaceForSourceAsync(FieldReferenceSourceTypes.FormRule, rule.Id, collector.Rows, ct);
    }

    private async Task IndexFieldAsync(AppField field, TableFieldIndex table, IFieldIndexProvider others, CancellationToken ct)
    {
        var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.Field, field.Id, field.AppTableId);
        await FieldSettingsReferenceExtractor.ExtractAsync(field, table, others, _engine, collector, ct);
        await _references.ReplaceForSourceAsync(FieldReferenceSourceTypes.Field, field.Id, collector.Rows, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<long, long?> ElementFids(IEnumerable<FormSection> layout) =>
        FormReferenceExtractor.Elements(layout).ToDictionary(e => e.Id, e => e.AppFieldId);

    private static async Task<TableFieldIndex> RequireTableAsync(TableIndexCache tables, long tableId, CancellationToken ct) =>
        await tables.GetByIdAsync(tableId, ct) ?? throw new NotFoundException("Table", tableId);

    private async Task SafelyAsync(string what, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Field reference index update failed for {Source}; the Usage tab may be stale until the table is rebuilt.", what);
        }
    }

    /// <summary>Lets the dashboard extractor place a field: which table a report runs on, a table by public id, a table's fields.</summary>
    private sealed class DashboardResolver : DashboardReferenceExtractor.IResolver
    {
        private readonly IReadOnlyDictionary<Guid, long> _reportTables;
        private readonly IAppTableRepository _tableRepo;
        private readonly TableIndexCache _tables;

        public DashboardResolver(IReadOnlyDictionary<Guid, long> reportTables, IAppTableRepository tableRepo, TableIndexCache tables)
        {
            _reportTables = reportTables;
            _tableRepo = tableRepo;
            _tables = tables;
        }

        public long? TableOfReport(Guid reportPublicId) => _reportTables.TryGetValue(reportPublicId, out var id) ? id : null;

        public async Task<long?> TableByPublicIdAsync(Guid tablePublicId, CancellationToken ct)
        {
            try { return (await _tableRepo.GetByPublicIdAsync(tablePublicId, ct)).Id; }
            catch (NotFoundException) { return null; }
        }

        public Task<TableFieldIndex?> TableAsync(long tableId, CancellationToken ct) => _tables.GetByIdAsync(tableId, ct);
    }

    /// <summary>One indexing operation's view of the tables it touches, loaded at most once each.</summary>
    private sealed class TableIndexCache : IFieldIndexProvider
    {
        private readonly IAppFieldRepository _fields;
        private readonly IAppTableRepository _tables;
        private readonly Dictionary<long, TableFieldIndex> _byId = [];
        private readonly HashSet<Guid> _missing = [];

        public TableIndexCache(IAppFieldRepository fields, IAppTableRepository tables)
        {
            _fields = fields;
            _tables = tables;
        }

        public async Task<TableFieldIndex?> GetByIdAsync(long tableId, CancellationToken ct = default)
        {
            if (_byId.TryGetValue(tableId, out var cached)) return cached;
            var fields = await _fields.ListByTableAsync(tableId, ct);
            return _byId[tableId] = new TableFieldIndex(tableId, fields);
        }

        public async Task<TableFieldIndex?> GetByPublicIdAsync(Guid tablePublicId, CancellationToken ct = default)
        {
            if (_missing.Contains(tablePublicId)) return null;
            try
            {
                var table = await _tables.GetByPublicIdAsync(tablePublicId, ct);
                return await GetByIdAsync(table.Id, ct);
            }
            catch (NotFoundException)
            {
                _missing.Add(tablePublicId); // a PowerFlow names many ids that are not tables; ask once each
                return null;
            }
        }
    }
}
