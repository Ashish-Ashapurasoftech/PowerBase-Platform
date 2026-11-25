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
    /// <summary>Every row of the field's table with <c>After &lt; Id &lt;= UpTo</c>: one slice of a split first build.</summary>
    public sealed record Range(long After, long UpTo) : WriteScope;
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
            case ProjectedPlan p:
                return SqlColumnType.IsAllowed(p.SqlType) ? p.SqlType : null;
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

    /// <summary>The "FROM … WHERE …" of one batch whose values were worked out in C# (a formula, or anything that read ciphertext) and
    /// bulk-copied into #cv.</summary>
    internal static (string From, string Value) ValueTableBatchParts(MaterializePlan plan) =>
        ($"""
          FROM {PhysicalNaming.FullTableName(plan.TableId)} {TargetAlias}
          JOIN #cv v ON v.Id = {TargetAlias}.Id
          WHERE 1 = 1
          """, "v.v");

    internal static (string From, string Value) FormulaBatchParts(FormulaPlan plan) => ValueTableBatchParts(plan);

    /// <summary>True when the batch's values are worked out in C# and handed to SQL through #cv: every formula, and a lookup / summary
    /// that reads encrypted data (SQL cannot read ciphertext).</summary>
    internal static bool UsesValueTable(MaterializePlan plan) => plan is FormulaPlan or ProjectedPlan || plan.DecryptAppId is not null;

    /// <summary>The plan whose shape decides which rows a scope means (a projected field's Lookup / Summary / Formula shape).</summary>
    internal static MaterializePlan ShapeOf(MaterializePlan plan) => plan is ProjectedPlan p ? p.Shape : plan;

    /// <summary>Rows per batch of a projected field: the read code works each one out (a summary reads its children per batch).</summary>
    internal const int ProjectedBatchSize = 500;

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
        /// <summary>Time spent working values out (reading rows + the read code / formula engine + copying into #cv), summed over batches.</summary>
        public TimeSpan ComputeTime { get; init; }
        /// <summary>Time spent in the UPDATE statements that write the changed values.</summary>
        public TimeSpan WriteTime { get; init; }

        /// <summary>Several slices of one build as one result (changed ids up to <see cref="MaxTrackedChanges"/>, as a single run keeps them).</summary>
        public static WriteResult Combine(IReadOnlyCollection<WriteResult> parts)
        {
            var tooMany = parts.Any(p => p.TooManyChanges);
            var ids = new HashSet<long>();
            if (!tooMany)
                foreach (var p in parts)
                {
                    ids.UnionWith(p.ChangedIds);
                    if (ids.Count > MaxTrackedChanges) { tooMany = true; ids.Clear(); break; }
                }
            return new WriteResult(parts.Sum(p => p.RowsWritten), parts.Sum(p => p.Batches), ids, tooMany)
            {
                ComputeTime = TimeSpan.FromTicks(parts.Sum(p => p.ComputeTime.Ticks)),
                WriteTime = TimeSpan.FromTicks(parts.Sum(p => p.WriteTime.Ticks))
            };
        }
    }

    /// <summary>
    /// Recomputes the plan's stored column for <paramref name="scope"/>, batch by batch through <paramref name="guard"/>, writing only what
    /// changed. Throws <see cref="MaterializeVersionChangedException"/> when the field moved on mid-way.
    /// </summary>
    /// <param name="decryptor">Needed by a plan that reads encrypted data (<see cref="MaterializePlan.DecryptAppId"/>); without it such a
    /// plan fails (the field is marked Failed and keeps today's dynamic path).</param>
    public static async Task<WriteResult> RecomputeAsync(GuardedConnection guard, MaterializePlan plan, string sqlType, int version,
        WriteScope scope, ComputeSchema schema, ComputeOptions options, bool reconcile, CancellationToken ct, ComputeDecryptor? decryptor = null,
        ProjectedValueComputer? projector = null)
    {
        if (plan.DecryptAppId is not null && decryptor is null)
            throw new InvalidOperationException($"Field {plan.FieldId} reads encrypted data and no decryptor is configured for the compute worker.");
        if (plan is ProjectedPlan && projector is null)
            throw new InvalidOperationException($"Field {plan.FieldId} is worked out by the read code and no projector is configured for the compute worker.");

        var column = PhysicalNaming.MaterializedColumnName(plan.Fid);
        var batchSize = plan switch
        {
            ProjectedPlan => options.ProjectedBatchSize > 0 ? options.ProjectedBatchSize : ProjectedBatchSize,
            SummaryPlan => options.SummaryBatchSize,
            FormulaPlan => FormulaBatchSize,
            LookupPlan when plan.DecryptAppId is not null => FormulaBatchSize,
            _ => options.LookupBatchSize
        };
        var childFields = plan is SummaryPlan s ? schema.FieldsByFid(s.ChildTableId) : new Dictionary<long, AppField>();
        var valueTable = UsesValueTable(plan);
        long written = 0;
        var batches = 0;
        var changed = new HashSet<long>();
        var tooMany = false;
        var computeTime = TimeSpan.Zero;
        var writeTime = TimeSpan.Zero;

        if (valueTable)
            await guard.Connection.ExecuteAsync(new CommandDefinition(
                $"IF OBJECT_ID('tempdb..#cv') IS NOT NULL DROP TABLE #cv; CREATE TABLE #cv (Id BIGINT NOT NULL PRIMARY KEY, v {sqlType} NULL);", cancellationToken: ct));
        try
        {
            await foreach (var ids in BatchesAsync(guard.Connection, ShapeOf(plan), scope, batchSize, ct))
            {
                await guard.BeforeBatchAsync(ct);
                var parameters = new DynamicParameters();
                parameters.Add("ids", JsonSerializer.Serialize(ids));
                parameters.Add("lo", ids.Min());
                parameters.Add("hi", ids.Max());
                parameters.Add("fieldId", plan.FieldId);
                parameters.Add("version", version);

                (string From, string Value) parts;
                var phase = System.Diagnostics.Stopwatch.StartNew();
                switch (plan)
                {
                    case ProjectedPlan pp:
                        await CopyValuesAsync(guard.Connection, sqlType, await projector!.ComputeAsync(pp, ids, ct), ct);
                        parts = ValueTableBatchParts(pp);
                        break;
                    case SummaryPlan { DecryptAppId: not null } sp:
                        await FillEncryptedSummaryValuesAsync(guard.Connection, sp, sqlType, ids, schema, decryptor!, ct);
                        parts = ValueTableBatchParts(sp);
                        break;
                    case LookupPlan { DecryptAppId: not null } lp:
                        await FillEncryptedLookupValuesAsync(guard.Connection, lp, sqlType, ids, decryptor!, ct);
                        parts = ValueTableBatchParts(lp);
                        break;
                    case SummaryPlan sp: parts = SummaryBatchParts(sp, sqlType, childFields, parameters); break;
                    case LookupPlan lp: parts = LookupBatchParts(lp, sqlType); break;
                    case FormulaPlan fp:
                        await FillFormulaValuesAsync(guard.Connection, fp, ids, schema, decryptor, ct);
                        parts = FormulaBatchParts(fp);
                        break;
                    default: throw new NotSupportedException(plan.GetType().Name);
                }
                computeTime += phase.Elapsed;

                phase.Restart();
                // The batch UPDATE is idempotent (it writes only differences, under the version fence), so a deadlock with other work on
                // the table — another field's ALTER TABLE, a slice of the same field — is retried instead of failing the whole build.
                List<long> changedIds;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        changedIds = (await guard.Connection.QueryAsync<long>(new CommandDefinition(
                            UpdateSql(column, sqlType, parts), parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
                        break;
                    }
                    catch (SqlException ex) when (ex.Number == 1205 && attempt < 4)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
                    }
                }
                writeTime += phase.Elapsed;
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
            if (valueTable && guard.Connection.State == ConnectionState.Open)
                await guard.Connection.ExecuteAsync("IF OBJECT_ID('tempdb..#cv') IS NOT NULL DROP TABLE #cv;");
        }
        return new WriteResult(written, batches, changed, tooMany) { ComputeTime = computeTime, WriteTime = writeTime };
    }

    /// <summary>Reads the batch's rows (their stored fields and the stored values of the calculated fields the formula reads), decrypts the
    /// encrypted ones when the app is encrypted, evaluates the formula for each, and bulk-copies the results into #cv.</summary>
    private static async Task FillFormulaValuesAsync(SqlConnection connection, FormulaPlan plan, long[] ids, ComputeSchema schema,
        ComputeDecryptor? decryptor, CancellationToken ct)
    {
        var tableFields = schema.FieldsOf(plan.TableId);
        var storedColumns = string.Concat(plan.StoredReads.Select(f => ", " + PhysicalNaming.MaterializedColumnName(f)));
        var sql = $"""
            {KeysSql}SELECT Id, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{RecordRepository.BuildFieldColumnList(tableFields)}{storedColumns}
            FROM {PhysicalNaming.FullTableName(plan.TableId)}
            WHERE Id BETWEEN @lo AND @hi AND Id IN {IdsInSql}
            """;
        var mutable = await ReadRowsAsync(connection, sql, new { ids = JsonSerializer.Serialize(ids), lo = ids.Min(), hi = ids.Max() }, ct);
        if (plan.DecryptAppId is long appId)
            await decryptor!.DecryptRowsAsync(connection, appId, mutable, PhysicalFields(tableFields), ct);
        var rows = mutable.Select(r => (IReadOnlyDictionary<string, object?>)(Dictionary<string, object?>)r).ToList();
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

    // ── encrypted inputs (worked out in C#, stored plain) ───────────────────────────────────────────────

    /// <summary>A lookup whose source holds ciphertext: the parent's source value through the child's reference (a deleted or missing parent
    /// gives NULL, as LookupColumnExpr), decrypted with the parent app's key, cut to the stored type, bulk-copied into #cv.</summary>
    private static async Task FillEncryptedLookupValuesAsync(SqlConnection connection, LookupPlan plan, string sqlType, long[] ids,
        ComputeDecryptor decryptor, CancellationToken ct)
    {
        var sql = $"""
            {KeysSql}SELECT {TargetAlias}.Id AS Id, lk.{plan.SourceColumn} AS v
            FROM {PhysicalNaming.FullTableName(plan.ChildTableId)} {TargetAlias}
            LEFT JOIN {PhysicalNaming.FullTableName(plan.ParentTableId)} lk ON lk.Id = {TargetAlias}.{PhysicalNaming.ColumnName(plan.ReferenceFid)} AND lk.IsDeleted = 0
            WHERE {TargetAlias}.Id BETWEEN @lo AND @hi AND {TargetAlias}.Id IN {IdsInSql}
            """;
        var rows = await ReadRowsAsync(connection, sql, new { ids = JsonSerializer.Serialize(ids), lo = ids.Min(), hi = ids.Max() }, ct);
        var values = new List<(long Id, object? Value)>(rows.Count);
        foreach (var row in rows)
        {
            var raw = row["v"];
            if (raw is string cipher) raw = await decryptor.DecryptValueAsync(connection, plan.DecryptAppId!.Value, cipher, ct);
            values.Add((Convert.ToInt64(row["Id"]), raw));
        }
        await CopyValuesAsync(connection, sqlType, values, ct);
    }

    /// <summary>
    /// A summary whose target or criteria read ciphertext: the batch's live children (through the reference index), decrypted with the
    /// child app's key, matched and aggregated by EncryptedSummaryAggregator — the same code the dynamic read of an encrypted summary uses,
    /// so the stored value is the one shown. Parents with no matching child: Count / Distinct Count 0, Exists false, anything else NULL.
    /// </summary>
    private static async Task FillEncryptedSummaryValuesAsync(SqlConnection connection, SummaryPlan plan, string sqlType, long[] ids,
        ComputeSchema schema, ComputeDecryptor decryptor, CancellationToken ct)
    {
        // Only the columns the summary reads (the reference, the target, the criteria fields): a child table can have millions of rows and
        // dozens of long encrypted text columns, so reading every column of every child of a batch runs the worker out of memory.
        var needed = new HashSet<int> { plan.ReferenceFid };
        if (plan.TargetFid is int tf) needed.Add(tf);
        CollectCriteriaFids(plan.Filter, needed);
        var childFields = schema.FieldsOf(plan.ChildTableId).Where(f => f.Fid is int fid && needed.Contains(fid)).ToList();
        var refCol = PhysicalNaming.ColumnName(plan.ReferenceFid);
        // A chained target (a stored calculated field of the child) is read from its c_ column under its f_ name, as a plain field of its value type.
        var aggregateFields = childFields.ToList();
        var storedTarget = "";
        if (plan.TargetIsStored && plan.TargetFid is int targetFid
            && childFields.FirstOrDefault(f => f.Fid == targetFid) is { } chained && MaterializedReadFields.ValueTypeOf(chained) is { } valueType)
        {
            var plain = chained.Clone();
            plain.TypeCode = valueType;
            plain.Settings = null;
            plain.IsEncrypted = false;
            aggregateFields[aggregateFields.IndexOf(chained)] = plain;
            storedTarget = $", c.{PhysicalNaming.MaterializedColumnName(targetFid)} AS {PhysicalNaming.ColumnName(targetFid)}";
        }

        var sql = $"""
            {KeysSql}SELECT c.Id, c.CreatedOn, c.CreatedBy, c.ModifiedOn, c.ModifiedBy{RecordRepository.BuildFieldColumnList(childFields)}{storedTarget}
            FROM {PhysicalNaming.FullTableName(plan.ChildTableId)} c
            WHERE c.IsDeleted = 0 AND c.{refCol} BETWEEN @lo AND @hi AND c.{refCol} IN {IdsInSql}
            ORDER BY c.Id
            """;

        // Parents a few at a time: a parent's children are always read together (the aggregate needs all of them), but a batch of
        // thousands of parents over a table of millions of children never sits in memory at once.
        var values = new List<(long Id, object? Value)>(ids.Length);
        foreach (var chunk in ids.Chunk(EncryptedSummaryParentsPerRead))
        {
            var rows = await ReadRowsAsync(connection, sql, new { ids = JsonSerializer.Serialize(chunk), lo = chunk.Min(), hi = chunk.Max() }, ct);
            await decryptor.DecryptRowsAsync(connection, plan.DecryptAppId!.Value, rows, PhysicalFields(childFields), ct);

            var keys = chunk.ToDictionary(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var aggregated = PowerBase.Application.Relationships.EncryptedSummaryAggregator.Aggregate(
                rows.Select(r => (IReadOnlyDictionary<string, object?>)(Dictionary<string, object?>)r).ToList(),
                aggregateFields, plan.Function, plan.ReferenceFid, plan.TargetFid, plan.TargetSubField,
                keys.Keys.ToHashSet(), plan.Filter, options: null, format: v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            rows.Clear();

            foreach (var (key, id) in keys)
            {
                object? value = aggregated.TryGetValue(key, out var v) ? v : null;
                value ??= plan.Function switch
                {
                    SummaryFunctions.Count or SummaryFunctions.DistinctCount => 0,
                    SummaryFunctions.Exists => false,
                    _ => null
                };
                values.Add((id, value));
            }
        }
        await CopyValuesAsync(connection, sqlType, values, ct);
    }

    /// <summary>Parents whose children are read (and decrypted) in one go by an encrypted summary.</summary>
    internal const int EncryptedSummaryParentsPerRead = 100;

    /// <summary>Every field a criteria tree reads (the field it filters on and the one it compares to).</summary>
    private static void CollectCriteriaFids(PowerBase.Application.Reports.FilterGroup? group, HashSet<int> into)
    {
        if (group is null) return;
        foreach (var node in group.Nodes)
        {
            if (node.Group is { } sub) CollectCriteriaFids(sub, into);
            if (node.Condition is not { } c) continue;
            into.Add((int)c.FieldId);
            if (c.ValueFieldId is long other) into.Add((int)other);
        }
    }

    /// <summary>The table's own stored (non-calculated) fields: the only columns that can hold ciphertext.</summary>
    private static IReadOnlyList<AppField> PhysicalFields(IReadOnlyList<AppField> fields) =>
        fields.Where(f => f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();

    private static async Task<List<IDictionary<string, object?>>> ReadRowsAsync(SqlConnection connection, string sql, object parameters, CancellationToken ct) =>
        (await connection.QueryAsync(new CommandDefinition(sql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
            .Select(r => (IDictionary<string, object?>)((IDictionary<string, object?>)r).ToDictionary(kv => kv.Key, kv => kv.Value is DBNull ? null : kv.Value))
            .ToList();

    /// <summary>Bulk-copies (Id, value) into #cv, each value converted to the stored column's type (text cut to the sort key); a value that does
    /// not convert is stored as NULL, as the dynamic read shows nothing for it either.</summary>
    private static async Task CopyValuesAsync(SqlConnection connection, string sqlType, IReadOnlyList<(long Id, object? Value)> values, CancellationToken ct)
    {
        var clr = ClrTypeOfSql(sqlType);
        var data = new DataTable();
        data.Columns.Add("Id", typeof(long));
        data.Columns.Add("v", clr);
        foreach (var (id, raw) in values)
            data.Rows.Add(id, ToStoredValue(raw, sqlType, clr) ?? DBNull.Value);

        await connection.ExecuteAsync(new CommandDefinition("TRUNCATE TABLE #cv;", cancellationToken: ct));
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#cv", BulkCopyTimeout = CommandTimeoutSeconds };
        await bulk.WriteToServerAsync(data, ct);
    }

    internal static Type ClrTypeOfSql(string sqlType) => sqlType switch
    {
        "BIT" => typeof(bool),
        "TINYINT" => typeof(byte),
        "SMALLINT" => typeof(short),
        "INT" => typeof(int),
        "BIGINT" => typeof(long),
        "DATE" => typeof(DateTime),
        _ when sqlType.StartsWith("DATETIME2", StringComparison.Ordinal) => typeof(DateTime),
        _ when sqlType.StartsWith("TIME", StringComparison.Ordinal) => typeof(TimeSpan),
        _ when sqlType.StartsWith("DECIMAL", StringComparison.Ordinal) => typeof(decimal),
        _ => typeof(string)
    };

    /// <summary>One computed value as the stored column holds it; null when it is empty or does not convert.</summary>
    internal static object? ToStoredValue(object? raw, string sqlType, Type clr)
    {
        if (raw is null || raw is DBNull) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            if (clr == typeof(string))
            {
                var text = raw is DateTime dt ? dt.ToString("o", inv) : Convert.ToString(raw, inv);
                if (string.IsNullOrEmpty(text)) return null;
                return !SqlColumnType.IsFullText(sqlType) && text.Length > SqlColumnType.TextSortKeyLength ? text[..SqlColumnType.TextSortKeyLength] : text;
            }
            if (clr == typeof(bool))
                return raw is string sb ? (bool.TryParse(sb, out var b) ? b : sb.Trim() == "1") : Convert.ToBoolean(raw, inv);
            if (clr == typeof(DateTime))
                return raw switch
                {
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    DateTimeOffset o => o.UtcDateTime,
                    string sd => DateTime.Parse(sd, inv, System.Globalization.DateTimeStyles.RoundtripKind),
                    _ => Convert.ToDateTime(raw, inv)
                };
            if (clr == typeof(TimeSpan))
                return raw switch { TimeSpan ts => ts, TimeOnly t => t.ToTimeSpan(), DateTime d => d.TimeOfDay, string st => TimeSpan.Parse(st, inv), _ => null };
            if (clr == typeof(decimal))
            {
                var d = raw is string sdec ? decimal.Parse(sdec, System.Globalization.NumberStyles.Any, inv) : Convert.ToDecimal(raw, inv);
                var scale = sqlType.LastIndexOf(',') is var comma and > 0 && int.TryParse(sqlType[(comma + 1)..].TrimEnd(')'), out var sc) ? sc : 4;
                return Math.Round(d, scale, MidpointRounding.AwayFromZero);
            }
            return Convert.ChangeType(raw is string s ? decimal.Parse(s, System.Globalization.NumberStyles.Any, inv) : raw, clr, inv);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
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

            case WriteScope.Range range:
                for (var after = range.After; ;)
                {
                    var ids = (await connection.QueryAsync<long>(new CommandDefinition(
                        $"SELECT TOP (@batch) Id FROM {table} WHERE Id > @after AND Id <= @upTo ORDER BY Id",
                        new { batch = batchSize, after, upTo = range.UpTo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToArray();
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
