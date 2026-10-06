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
        var tablePublicId = (await _tables.GetByIdAsync(tableId, ct)).PublicId;

        // Start clean so rows left behind by sources that no longer exist disappear too.
        await _references.ClearTableAsync(tableId, ct);

        foreach (var report in await _references.ListReportSourcesAsync(tableId, ct))
            await IndexReportAsync(report, table, ct);

        foreach (var form in await _forms.ListByTableAsync(tablePublicId, ct))
            await IndexFormAsync(form, table, ct);

        foreach (var field in table.Fields)
            await IndexFieldAsync(field, table, tables, ct);

        await _references.MarkTableIndexedAsync(tableId, ct);
    }

    public Task EnsureTableIndexedAsync(long tableId, CancellationToken ct = default) =>
        SafelyAsync($"backfill of table {tableId}", async () =>
        {
            if (!await _references.IsTableIndexedAsync(tableId, ct))
                await RebuildTableAsync(tableId, ct);
        });

    // ── Per-source indexing ─────────────────────────────────────────────────────────────────────

    private async Task IndexReportAsync(Report report, TableFieldIndex table, CancellationToken ct)
    {
        var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.Report, report.Id, report.AppTableId);
        ReportReferenceExtractor.Extract(report.Definition, table, collector);
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

    /// <summary>One indexing operation's view of the tables it touches, loaded at most once each.</summary>
    private sealed class TableIndexCache : IFieldIndexProvider
    {
        private readonly IAppFieldRepository _fields;
        private readonly IAppTableRepository _tables;
        private readonly Dictionary<long, TableFieldIndex> _byId = [];

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
            try
            {
                var table = await _tables.GetByPublicIdAsync(tablePublicId, ct);
                return await GetByIdAsync(table.Id, ct);
            }
            catch (NotFoundException) { return null; }
        }
    }
}
