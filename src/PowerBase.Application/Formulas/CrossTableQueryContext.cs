using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Querying;

namespace PowerBase.Application.Formulas;

/// <summary>
/// DB-backed implementation of the cross-table record operations used by the Tier-2
/// formula functions (GetRecords/GetFieldValues/…). Shared across all rows of one
/// projection/evaluation batch so table/field metadata is resolved once and a query
/// budget caps the worst-case fan-out. Cross-table reads are NOT re-filtered by role
/// (consistent with the Lookup/Summary relational projector — computed values are
/// server-authoritative).
///
/// The repository calls are async; these are invoked from the synchronous evaluator,
/// so they block. Like QuickBase formula-queries, this path is comparatively
/// expensive — the budget keeps a runaway formula from issuing unbounded queries.
/// </summary>
public sealed class CrossTableQueryContext
{
    private const int MaxRowsPerQuery = 1000;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly AppTable? _currentTable;
    private readonly Dictionary<string, Resolved?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _budget;

    private sealed record Resolved(AppTable Table, IReadOnlyList<AppField> Fields);

    public CrossTableQueryContext(
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo,
        AppTable? currentTable, int queryBudget = 500)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _currentTable = currentTable;
        _budget = queryBudget;
    }

    public IReadOnlyList<long> QueryRecords(string tableId, RecordQuery query)
    {
        if (!TakeBudget() || Resolve(tableId) is not { } r) return Array.Empty<long>();
        var filter = ToFilterGroup(query);
        var rows = Block(_recordRepo.ListAsync(r.Table, r.Fields, 1, MaxRowsPerQuery, filter));
        var ids = new List<long>(rows.Count);
        foreach (var row in rows)
            if (TryGetLong(row, "Id", out var id)) ids.Add(id);
        return ids;
    }

    public long? FindRecordByField(string tableId, long fid, string value)
    {
        if (!TakeBudget() || Resolve(tableId) is not { } r) return null;
        var filter = new FilterGroup { Logic = "and", Nodes = { Node(fid, "eq", value) } };
        var rows = Block(_recordRepo.ListAsync(r.Table, r.Fields, 1, 1, filter));
        return rows.Count > 0 && TryGetLong(rows[0], "Id", out var id) ? id : null;
    }

    public bool RecordExists(string tableId, long recordId)
    {
        if (!TakeBudget() || Resolve(tableId) is not { } r) return false;
        return Block(_recordRepo.ExistsAsync(r.Table, recordId));
    }

    public IReadOnlyList<object?> GetFieldValues(string tableId, IReadOnlyList<long> recordIds, long fid)
    {
        if (recordIds.Count == 0 || !TakeBudget() || Resolve(tableId) is not { } r) return Array.Empty<object?>();

        var field = r.Fields.FirstOrDefault(f => f.Fid == (int)fid);
        var col = field?.PhysicalColumnName ?? PhysicalNaming.ColumnName((int)fid);

        var byId = Block(_recordRepo.GetRowsByIdsAsync(r.Table, r.Fields, recordIds));
        var values = new List<object?>(recordIds.Count);
        foreach (var id in recordIds)
            values.Add(byId.TryGetValue(id, out var row) && row.TryGetValue(col, out var v) ? v : null);
        return values;
    }

    // ── Resolution + translation ─────────────────────────────────────────────

    private Resolved? Resolve(string tableId)
    {
        var key = string.IsNullOrEmpty(tableId) ? "_current" : tableId;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        Resolved? resolved = null;
        if (string.IsNullOrEmpty(tableId))
        {
            if (_currentTable is not null)
                resolved = new Resolved(_currentTable, Block(_fieldRepo.ListByTableAsync(_currentTable.Id)));
        }
        else if (Guid.TryParse(tableId, out var guid))
        {
            try
            {
                var table = Block(_tableRepo.GetByPublicIdAsync(guid));
                if (table is not null)
                    resolved = new Resolved(table, Block(_fieldRepo.ListByTableAsync(table.Id)));
            }
            catch { resolved = null; }
        }

        _cache[key] = resolved;
        return resolved;
    }

    private static FilterGroup ToFilterGroup(RecordQuery query)
    {
        // QuickBase joins clauses left-to-right; FilterGroup carries a single logic.
        // Use the connector when uniform, else default to AND (the common case).
        var logic = query.Connectors.Count > 0 && query.Connectors.All(c => c == QueryConnector.Or) ? "or" : "and";
        var group = new FilterGroup { Logic = logic };
        foreach (var c in query.Clauses)
        {
            var op = MapOp(c.Op);
            if (op is null) continue; // unsupported operator → drop the clause
            group.Nodes.Add(Node(c.Fid, op, c.Value));
        }
        return group;
    }

    private static FilterNode Node(long fid, string op, string value) =>
        new() { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = value } };

    private static string? MapOp(string qbOp) => qbOp switch
    {
        "EX" => "eq",
        "XEX" => "ne",
        "CT" => "contains",
        "SW" => "startsWith",
        "GT" => "gt",
        "GTE" => "gte",
        "LT" => "lt",
        "LTE" => "lte",
        "BF" => "lt",
        "AF" => "gt",
        "OBF" => "lte",
        "OAF" => "gte",
        _ => null,
    };

    private bool TakeBudget() => _budget-- > 0;

    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static bool TryGetLong(IReadOnlyDictionary<string, object?> row, string key, out long value)
    {
        value = 0;
        if (!row.TryGetValue(key, out var raw) || raw is null) return false;
        try { value = Convert.ToInt64(raw, CultureInfo.InvariantCulture); return true; }
        catch { return long.TryParse(raw.ToString(), out value); }
    }
}

/// <summary>
/// Per-row record context that adds cross-table support: field reads delegate to the
/// inner single-record context; the record-set operations delegate to a shared
/// <see cref="CrossTableQueryContext"/>.
/// </summary>
public sealed class CrossTableRecordContext : IRecordContext
{
    private readonly IRecordContext _inner;
    private readonly CrossTableQueryContext _x;

    public CrossTableRecordContext(IRecordContext inner, CrossTableQueryContext x)
    {
        _inner = inner;
        _x = x;
    }

    public object? GetValue(long fid) => _inner.GetValue(fid);
    public IReadOnlyList<long> QueryRecords(string tableId, RecordQuery query) => _x.QueryRecords(tableId, query);
    public long? FindRecordByField(string tableId, long fid, string value) => _x.FindRecordByField(tableId, fid, value);
    public bool RecordExists(string tableId, long recordId) => _x.RecordExists(tableId, recordId);
    public IReadOnlyList<object?> GetFieldValues(string tableId, IReadOnlyList<long> recordIds, long fid)
        => _x.GetFieldValues(tableId, recordIds, fid);
}
