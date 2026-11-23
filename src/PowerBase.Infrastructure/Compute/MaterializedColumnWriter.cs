using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula.Types;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Compute;

/// <summary>Which rows of a stored field to recompute.</summary>
public abstract record WriteScope
{
    /// <summary>Every row of the field's table (a first build, a table-wide change, the nightly check).</summary>
    public sealed record All : WriteScope;
    /// <summary>These rows of the field's own table (Summary: these parents; Lookup: these children; Formula: these rows).</summary>
    public sealed record Rows(IReadOnlyCollection<long> Ids) : WriteScope;
    /// <summary>Lookup only: every child of these parents (a parent's source value changed, or the parent was deleted/restored).</summary>
    public sealed record ChildrenOf(IReadOnlyCollection<long> ParentIds) : WriteScope;
}

/// <summary>The field's MaterializeVersion moved on while it was being written: the work for the old version is abandoned.</summary>
public sealed class MaterializeVersionChangedException(long fieldId) : Exception($"Field {fieldId} changed while its stored column was being written.");

/// <summary>
/// Writes stored c_ columns (design 6.4). Lookup / Summary: one set-based UPDATE … FROM per batch of 2,000–5,000 rows. Formula: rows read in
/// batches of 1,000, evaluated by the API's formula engine, bulk-copied into a temp table, then one UPDATE … FROM #tmp. Each statement is
/// short (never one long transaction, no lock escalation), always RECOMPUTES from current data (never increments), and carries the version
/// fence. Only rows whose stored value really differs are written: less log, and the ids that come back tell chained fields (and the
/// search index) exactly which rows changed. The Summary SQL comes from the builders the dynamic read uses, so the stored value is the one shown.
/// </summary>
public static class MaterializedColumnWriter
{
    private const int CommandTimeoutSeconds = 120;
    private const string TargetAlias = "x";
    internal const int FormulaBatchSize = 1000;
    /// <summary>Changed ids kept per run; past this a run reports "too many" and dependents are recomputed table-wide.</summary>
    internal const int MaxTrackedChanges = 100_000;

    private const string FenceSql =
        "EXISTS (SELECT 1 FROM meta.AppField fx WHERE fx.Id = @fieldId AND fx.MaterializeVersion = @version AND fx.MaterializeMode = 1)";

    /// <summary>
    /// The batch's ids, parsed ONCE into a keyed table variable. A plain <c>IN (SELECT … FROM OPENJSON(@ids))</c> can end up as the inner
    /// side of a loop, re-parsing the whole JSON for every row it tests (measured: ~3.8 s of CPU per 2,000-parent batch over 4 lakh
    /// children). Every batch statement starts with <see cref="KeysSql"/> and reads <c>@k</c>.
    /// </summary>
    internal const string KeysSql = "DECLARE @k TABLE (k BIGINT NOT NULL PRIMARY KEY);\nINSERT INTO @k (k) SELECT DISTINCT k FROM OPENJSON(@ids) WITH (k BIGINT '$');\n";
    private const string IdsInSql = "(SELECT k FROM @k)";

    private static readonly StoredFormulaEvaluator Formulas = new();

    // ── stored type ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The c_ column type for the plan, from the real column it reads; null when that value cannot be stored.</summary>
    public static async Task<string?> StoredTypeAsync(SqlConnection connection, MaterializePlan plan, CancellationToken ct)
    {
        switch (plan)
        {
            case LookupPlan l:
                return SqlColumnType.StoredTypeFor(await SqlColumnType.ReadAsync(connection, PhysicalNaming.FullTableName(l.ParentTableId), l.SourceColumn, ct: ct));
            case SummaryPlan s:
                var fixedType = SummaryStoredType(s.Function);
                if (fixedType is not null) return fixedType;
                if (s.TargetFid is not int target) return null;
                var column = s.TargetIsStored
                    ? PhysicalNaming.MaterializedColumnName(target)
                    : target switch { 1 => "CreatedOn", 2 => "ModifiedOn", 3 => "Id", 4 => "CreatedBy", 5 => "ModifiedBy", _ => PhysicalNaming.ColumnName(target) };
                return SqlColumnType.StoredTypeFor(await SqlColumnType.ReadAsync(connection, PhysicalNaming.FullTableName(s.ChildTableId), column, ct: ct));
            case FormulaPlan f:
                return FormulaAnalysis.StoredSqlType(f.ResultType);
            default:
                return null;
        }
    }

    /// <summary>The type of the aggregates whose result type does not depend on the target (exactly what SQL Server returns for them).</summary>
    internal static string? SummaryStoredType(string function) => function switch
    {
        SummaryFunctions.Count or SummaryFunctions.DistinctCount => "INT",
        SummaryFunctions.Exists => "BIT",
        SummaryFunctions.Sum => "DECIMAL(38,4)",
        SummaryFunctions.Avg => "DECIMAL(38,6)",
        _ => null   // Min / Max: the target's own type
    };

    // ── SQL ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The "FROM … WHERE …" of one Summary batch (target rows = the parents in @ids) and the new value of each parent. Children are read
    /// only through the reference index (IsDeleted = 0 AND f_ref IN @ids). No matching children: Count / Distinct Count 0, Exists false,
    /// anything else NULL — the same as RelationalProjector. A chained target reads the children's own stored column.
    /// </summary>
    internal static (string From, string Value) SummaryBatchParts(SummaryPlan plan, string sqlType, IReadOnlyDictionary<long, AppField> childFields, DynamicParameters parameters)
    {
        var refCol = PhysicalNaming.ColumnName(plan.ReferenceFid);
        var c = RecordRepository.ChildAlias;
        var aggregate = RecordRepository.SummaryAggregateExpr(plan.Function, plan.TargetFid.HasValue);
        var targetSelect = plan.TargetFid is not int target
            ? ""
            : plan.TargetIsStored
                ? $", {c}.{PhysicalNaming.MaterializedColumnName(target)} AS {RecordRepository.SummaryTargetAlias}"
                : $", {RecordRepository.TargetColumnExpr(target, plan.TargetSubField, childFields)} AS {RecordRepository.SummaryTargetAlias}";
        var filterWhere = RecordRepository.BuildFilterTreeWhere(plan.Filter, parameters, childFields, null);

        var from = $"""
            FROM {PhysicalNaming.FullTableName(plan.ParentTableId)} {TargetAlias}
            LEFT JOIN (
                SELECT ParentKey, {aggregate} AS Value
                FROM (
                    SELECT {c}.{refCol} AS ParentKey{targetSelect}
                    FROM {PhysicalNaming.FullTableName(plan.ChildTableId)} {c}
                    WHERE {c}.IsDeleted = 0 AND {c}.{refCol} BETWEEN @lo AND @hi AND {c}.{refCol} IN {IdsInSql}{filterWhere}
                ) matched
                GROUP BY ParentKey
            ) a ON a.ParentKey = {TargetAlias}.Id
            WHERE {TargetAlias}.Id BETWEEN @lo AND @hi AND {TargetAlias}.Id IN {IdsInSql}
            """;
        var value = plan.Function switch
        {
            SummaryFunctions.Count or SummaryFunctions.DistinctCount => "COALESCE(a.Value, 0)",
            SummaryFunctions.Exists => "COALESCE(a.Value, CAST(0 AS BIT))",
            _ => SqlColumnType.CastTo("a.Value", sqlType)
        };
        return (from, value);
    }

    /// <summary>The "FROM … WHERE …" of one Lookup batch (target rows = the children in @ids): the parent's source value through the child's
    /// reference, a deleted (or missing) parent giving NULL — exactly LookupColumnExpr.</summary>
    internal static (string From, string Value) LookupBatchParts(LookupPlan plan, string sqlType)
    {
        var from = $"""
            FROM {PhysicalNaming.FullTableName(plan.ChildTableId)} {TargetAlias}
            LEFT JOIN {PhysicalNaming.FullTableName(plan.ParentTableId)} lk ON lk.Id = {TargetAlias}.{PhysicalNaming.ColumnName(plan.ReferenceFid)} AND lk.IsDeleted = 0
            WHERE {TargetAlias}.Id BETWEEN @lo AND @hi AND {TargetAlias}.Id IN {IdsInSql}
            """;
        return (from, SqlColumnType.CastTo($"lk.{plan.SourceColumn}", sqlType));
    }

    /// <summary>The "FROM … WHERE …" of one Formula batch: the values the formula engine produced, bulk-copied into #cv.</summary>
    internal static (string From, string Value) FormulaBatchParts(FormulaPlan plan) =>
        ($"""
          FROM {PhysicalNaming.FullTableName(plan.TableId)} {TargetAlias}
          JOIN #cv v ON v.Id = {TargetAlias}.Id
          WHERE 1 = 1
          """, "v.v");

    /// <summary>"The stored value differs from the fresh one" — NULLs both ways, text exactly (case included).</summary>
    internal static string DiffersSql(string column, string sqlType, string fresh)
    {
        var stored = $"{TargetAlias}.{column}";
        var differs = SqlColumnType.IsText(sqlType)
            ? $"{stored} COLLATE Latin1_General_BIN2 <> {fresh} COLLATE Latin1_General_BIN2"
            : $"{stored} <> {fresh}";
        return $"(({stored} IS NULL AND {fresh} IS NOT NULL) OR ({stored} IS NOT NULL AND {fresh} IS NULL) OR {differs})";
    }

    /// <summary>The UPDATE of one batch: only rows whose value differs, only while the version fence holds; returns the ids it changed.</summary>
    internal static string UpdateSql(string column, string sqlType, (string From, string Value) parts) =>
        $"{KeysSql}UPDATE {TargetAlias} SET {TargetAlias}.{column} = {parts.Value}\nOUTPUT inserted.Id\n{parts.From}\n  AND {DiffersSql(column, sqlType, parts.Value)}\n  AND {FenceSql};";

    // ── execution ───────────────────────────────────────────────────────────────────────────────────────

    /// <param name="RowsWritten">Rows whose stored value changed (= the differences found, for the nightly check).</param>
    /// <param name="ChangedIds">Those rows' ids (up to <see cref="MaxTrackedChanges"/>); <paramref name="TooManyChanges"/> when there were more.</param>
    public sealed record WriteResult(long RowsWritten, int Batches, IReadOnlyCollection<long> ChangedIds, bool TooManyChanges)
    {
        public long Differences => RowsWritten;
    }

    /// <summary>
    /// Recomputes the plan's stored column for <paramref name="scope"/>, batch by batch through <paramref name="guard"/>, writing only what
    /// changed. Throws <see cref="MaterializeVersionChangedException"/> when the field moved on mid-way.
    /// </summary>
    public static async Task<WriteResult> RecomputeAsync(GuardedConnection guard, MaterializePlan plan, string sqlType, int version,
        WriteScope scope, ComputeSchema schema, ComputeOptions options, bool reconcile, CancellationToken ct)
    {
        var column = PhysicalNaming.MaterializedColumnName(plan.Fid);
        var batchSize = plan switch { SummaryPlan => options.SummaryBatchSize, FormulaPlan => FormulaBatchSize, _ => options.LookupBatchSize };
        var childFields = plan is SummaryPlan s ? schema.FieldsByFid(s.ChildTableId) : new Dictionary<long, AppField>();
        long written = 0;
        var batches = 0;
        var changed = new HashSet<long>();
        var tooMany = false;

        if (plan is FormulaPlan)
            await guard.Connection.ExecuteAsync(new CommandDefinition(
                $"IF OBJECT_ID('tempdb..#cv') IS NOT NULL DROP TABLE #cv; CREATE TABLE #cv (Id BIGINT NOT NULL PRIMARY KEY, v {sqlType} NULL);", cancellationToken: ct));
        try
        {
            await foreach (var ids in BatchesAsync(guard.Connection, plan, scope, batchSize, ct))
            {
                await guard.BeforeBatchAsync(ct);
                var parameters = new DynamicParameters();
                parameters.Add("ids", JsonSerializer.Serialize(ids));
                parameters.Add("lo", ids.Min());
                parameters.Add("hi", ids.Max());
                parameters.Add("fieldId", plan.FieldId);
                parameters.Add("version", version);

                (string From, string Value) parts;
                switch (plan)
                {
                    case SummaryPlan sp: parts = SummaryBatchParts(sp, sqlType, childFields, parameters); break;
                    case LookupPlan lp: parts = LookupBatchParts(lp, sqlType); break;
                    case FormulaPlan fp:
                        await FillFormulaValuesAsync(guard.Connection, fp, ids, schema, ct);
                        parts = FormulaBatchParts(fp);
                        break;
                    default: throw new NotSupportedException(plan.GetType().Name);
                }

                var changedIds = (await guard.Connection.QueryAsync<long>(new CommandDefinition(
                    UpdateSql(column, sqlType, parts), parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
                if (changedIds.Count == 0 && !await FenceHoldsAsync(guard.Connection, plan.FieldId, version, ct))
                    throw new MaterializeVersionChangedException(plan.FieldId);
                written += changedIds.Count;
                if (!tooMany)
                {
                    changed.UnionWith(changedIds);
                    if (changed.Count > MaxTrackedChanges) { tooMany = true; changed.Clear(); }
                }
                batches++;
                await guard.AfterBatchAsync(ct);
            }
        }
        finally
        {
            if (plan is FormulaPlan && guard.Connection.State == ConnectionState.Open)
                await guard.Connection.ExecuteAsync("IF OBJECT_ID('tempdb..#cv') IS NOT NULL DROP TABLE #cv;");
        }
        return new WriteResult(written, batches, changed, tooMany);
    }

    /// <summary>Reads the batch's rows (their stored fields and the stored values of the calculated fields the formula reads), evaluates the
    /// formula for each, and bulk-copies the results into #cv.</summary>
    private static async Task FillFormulaValuesAsync(SqlConnection connection, FormulaPlan plan, long[] ids, ComputeSchema schema, CancellationToken ct)
    {
        var tableFields = schema.FieldsOf(plan.TableId);
        var storedColumns = string.Concat(plan.StoredReads.Select(f => ", " + PhysicalNaming.MaterializedColumnName(f)));
        var sql = $"""
            {KeysSql}SELECT Id, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{RecordRepository.BuildFieldColumnList(tableFields)}{storedColumns}
            FROM {PhysicalNaming.FullTableName(plan.TableId)}
            WHERE Id BETWEEN @lo AND @hi AND Id IN {IdsInSql}
            """;
        var rows = (await connection.QueryAsync(new CommandDefinition(sql, new { ids = JsonSerializer.Serialize(ids), lo = ids.Min(), hi = ids.Max() },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
            .Select(r => (IReadOnlyDictionary<string, object?>)((IDictionary<string, object?>)r).ToDictionary(kv => kv.Key, kv => kv.Value is DBNull ? null : kv.Value))
            .ToList();
        var values = Formulas.Evaluate(plan, tableFields, rows);

        var data = new DataTable();
        data.Columns.Add("Id", typeof(long));
        data.Columns.Add("v", ClrTypeOf(plan.ResultType));
        foreach (var (id, raw) in values)
            data.Rows.Add(id, (plan.IsUnique ? StoredFormulaEvaluator.Normalize(StoredFormulaEvaluator.ToStored(raw, plan.ResultType)) : StoredFormulaEvaluator.ToStored(raw, plan.ResultType)) ?? DBNull.Value);

        await connection.ExecuteAsync(new CommandDefinition("TRUNCATE TABLE #cv;", cancellationToken: ct));
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#cv", BulkCopyTimeout = CommandTimeoutSeconds };
        await bulk.WriteToServerAsync(data, ct);
    }

    private static Type ClrTypeOf(FormulaType type) => type switch
    {
        FormulaType.Text => typeof(string),
        FormulaType.Number => typeof(decimal),
        FormulaType.Bool => typeof(bool),
        FormulaType.Date or FormulaType.DateTime => typeof(DateTime),
        _ => typeof(string)
    };

    private static async Task<bool> FenceHoldsAsync(SqlConnection connection, long fieldId, int version, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT CASE WHEN {FenceSql} THEN 1 ELSE 0 END", new { fieldId, version }, cancellationToken: ct)) == 1;

    /// <summary>The target row ids, batch by batch: keyset over Id (never OFFSET), so each batch is an index seek.</summary>
    internal static async IAsyncEnumerable<long[]> BatchesAsync(SqlConnection connection, MaterializePlan plan, WriteScope scope, int batchSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var table = PhysicalNaming.FullTableName(plan.TableId);
        switch (scope)
        {
            case WriteScope.Rows rows:
                foreach (var chunk in rows.Ids.Distinct().Order().Chunk(batchSize)) yield return chunk;
                break;

            case WriteScope.All:
                for (long after = 0; ;)
                {
                    var ids = (await connection.QueryAsync<long>(new CommandDefinition(
                        $"SELECT TOP (@batch) Id FROM {table} WHERE Id > @after ORDER BY Id",
                        new { batch = batchSize, after }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToArray();
                    if (ids.Length == 0) yield break;
                    yield return ids;
                    after = ids[^1];
                }

            case WriteScope.ChildrenOf parents when plan is LookupPlan lookup:
                var refCol = PhysicalNaming.ColumnName(lookup.ReferenceFid);
                foreach (var parentChunk in parents.ParentIds.Distinct().Order().Chunk(2000))
                {
                    var parentJson = JsonSerializer.Serialize(parentChunk);
                    for (long after = 0; ;)
                    {
                        // Parents parsed once into a keyed table and joined through the reference index (IsDeleted = 0 matches its filter);
                        // a deleted child's value is never read, and the nightly check covers one restored later.
                        var ids = (await connection.QueryAsync<long>(new CommandDefinition($"""
                            DECLARE @p TABLE (k BIGINT NOT NULL PRIMARY KEY);
                            INSERT INTO @p (k) SELECT DISTINCT k FROM OPENJSON(@parents) WITH (k BIGINT '$');
                            SELECT TOP (@batch) c.Id FROM {table} c JOIN @p p ON p.k = c.{refCol}
                            WHERE c.IsDeleted = 0 AND c.Id > @after ORDER BY c.Id;
                            """,
                            new { batch = batchSize, after, parents = parentJson }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToArray();
                        if (ids.Length == 0) break;
                        yield return ids;
                        after = ids[^1];
                    }
                }
                break;

            default:
                throw new NotSupportedException($"{scope.GetType().Name} does not apply to {plan.GetType().Name}.");
        }
    }
}
