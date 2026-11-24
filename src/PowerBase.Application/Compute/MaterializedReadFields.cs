using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Compute;

/// <summary>
/// What the read side knows about stored columns: which fields are Ready right now, whether work is waiting, and the hook every field
/// definition change calls. Independent of the field metadata cache (which lives for hours): the worker moves fields to Ready, and the
/// API must see that within seconds, and stop using a column the moment a definition it reads changes.
/// </summary>
public interface IMaterializedFieldState
{
    /// <summary>AppField.Id of every field whose stored column may be read now: switched on, Ready, and compute on for the tenant (the global
    /// switch and the tenant's flag). Empty when any of that is off, so turning a flag off restores today's reads at once. Cached a few seconds.</summary>
    Task<IReadOnlySet<long>> ReadyFieldIdsAsync(CancellationToken ct = default);

    /// <summary>True when stored-column work is still waiting for any of these tables (the report then says "Values are updating").</summary>
    Task<bool> HasPendingWorkAsync(IReadOnlyCollection<long> tableIds, CancellationToken ct = default);

    /// <summary>
    /// A field's definition changed (settings, type, deleted, encryption): its own stored column and every stored column that reads it stop
    /// being used — back to "not built" with a new version — and the worker rebuilds them. Run it in the transaction of the change when
    /// there is one.
    /// </summary>
    Task InvalidateAsync(IReadOnlyCollection<long> fieldIds, IDbTransaction? transaction, CancellationToken ct = default);

    /// <summary>What the field page shows about the field's stored column; null for a field that is not a Lookup / Summary / Formula.</summary>
    Task<StoredColumnStatus?> GetStatusAsync(AppField field, CancellationToken ct = default) => Task.FromResult<StoredColumnStatus?>(null);

    /// <summary>
    /// The stored values (c_{fid}) of these rows for these Ready fields, read in ONE query: row Id → (Fid → value). What a list shows for a
    /// Ready Lookup / Summary instead of working it out again on every read (each summary would otherwise read its child rows).
    /// Empty when nothing can be read; the caller then works the values out the usual way.
    /// </summary>
    Task<IReadOnlyDictionary<long, IReadOnlyDictionary<int, object?>>> ReadStoredValuesAsync(long tableId, IReadOnlyCollection<int> fids,
        IReadOnlyCollection<long> rowIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<long, IReadOnlyDictionary<int, object?>>>(new Dictionary<long, IReadOnlyDictionary<int, object?>>());
}

/// <summary>The field page's view of a stored column (read fresh, never from the field cache).</summary>
/// <param name="Status">One of <see cref="StoredColumnStatuses"/>.</param>
/// <param name="BuiltOn">When the column was last built (Ready), UTC.</param>
/// <param name="Updating">Changes are waiting for the worker on a table the value depends on (sorting may lag for a few seconds).</param>
public sealed record StoredColumnStatus(string Status, DateTime? BuiltOn, bool Updating);

public static class StoredColumnStatuses
{
    /// <summary>Stored columns are off for the tenant (or everywhere): the field is worked out the usual way.</summary>
    public const string Disabled = "disabled";
    /// <summary>Switched on (or about to be, decision Q3), not built yet.</summary>
    public const string Waiting = "waiting";
    public const string Building = "building";
    public const string Ready = "ready";
    public const string Failed = "failed";
    /// <summary>The rules refuse it (Today()/User() formulas, encrypted data, a loop, …): always worked out the usual way.</summary>
    public const string NotAllowed = "not-allowed";
    /// <summary>Switched off on purpose by an administrator.</summary>
    public const string Excluded = "excluded";

    /// <summary>Not switched on (only when the automatic switch-on of decision Q3 is turned off in config).</summary>
    public const string Off = "off";

    public static string From(bool computeEnabled, byte mode, byte state, bool storeAll = true) => !computeEnabled ? Disabled : mode switch
    {
        MaterializeModes.NotAllowed => NotAllowed,
        MaterializeModes.Excluded => Excluded,
        MaterializeModes.On => state switch
        {
            MaterializeStates.Ready => Ready,
            MaterializeStates.Building => Building,
            MaterializeStates.Failed => Failed,
            _ => Waiting
        },
        _ => storeAll ? Waiting : Off   // Off = not decided yet: the worker switches it on (decision Q3)
    };
}

/// <summary>What a Summary / Chart report hands SQL: its fields, Rows levels and series with every Ready stored field as a stand-in.</summary>
public sealed record SummarySqlFields(IReadOnlyList<AppField> Fields, IReadOnlyList<(AppField Field, string Mode)> RowGroups, AppField? Series)
{
    public static SummarySqlFields For(IReadOnlySet<long> readyFieldIds, IReadOnlyList<AppField> fields,
        IReadOnlyList<(AppField Field, string Mode)> rowGroups, AppField? series)
    {
        AppField Swap(AppField f) => readyFieldIds.Contains(f.Id) && MaterializedReadFields.StandIn(f) is { } s ? s : f;
        return new SummarySqlFields(
            MaterializedReadFields.Substitute(fields, readyFieldIds),
            rowGroups.Select(g => (Swap(g.Field), g.Mode)).ToList(),
            series is null ? null : Swap(series));
    }
}

/// <summary>
/// Lets the SQL read paths sort, filter, count and total a Ready Lookup / Summary through its stored column without changing them: the
/// field is handed to the repository as a stand-in of its value type (a Number, a Date, a Text…) whose column is c_{fid}. Only the copy
/// given to SQL is replaced; the shown value is still worked out by the projectors from the original fields.
/// </summary>
public static class MaterializedReadFields
{
    /// <summary>The fields with every Ready one replaced by its stand-in; the same list when none is.</summary>
    public static IReadOnlyList<AppField> Substitute(IReadOnlyList<AppField> fields, IReadOnlySet<long> readyFieldIds)
    {
        if (readyFieldIds.Count == 0 || !fields.Any(f => readyFieldIds.Contains(f.Id))) return fields;
        return fields.Select(f => readyFieldIds.Contains(f.Id) && StandIn(f) is { } s ? s : f).ToList();
    }

    /// <summary>The Fids of the Ready fields among <paramref name="fields"/> that have a stand-in (those SQL can now read).</summary>
    public static IReadOnlySet<long> ReadyFids(IReadOnlyList<AppField> fields, IReadOnlySet<long> readyFieldIds) =>
        fields.Where(f => f.Fid.HasValue && readyFieldIds.Contains(f.Id) && ValueTypeOf(f) is not null)
            .Select(f => (long)f.Fid!.Value).ToHashSet();

    /// <summary>The stand-in: the value type the field shows, read from c_{fid}. Null for a field with no known value type.</summary>
    public static AppField? StandIn(AppField field)
    {
        if (field.Fid is not int fid || ValueTypeOf(field) is not { } valueType) return null;
        var s = field.Clone();
        s.TypeCode = valueType;
        s.IsSystem = true;   // the repository reads a system field from PhysicalColumnName and never SELECTs it as f_{fid}
        s.PhysicalColumnName = PhysicalNaming.MaterializedColumnName(fid);
        s.IsEncrypted = false;
        s.Settings = null;
        return s;
    }

    /// <summary>The field type of the value a Lookup / Summary shows (what conditions compare and sorts order).</summary>
    public static string? ValueTypeOf(AppField field)
    {
        switch (field.TypeCode)
        {
            case "Lookup":
                var l = FormulaTypeMap.ParseLookupSettings(field.Settings);
                return NonEmpty(l?.ResultTypeCode) ?? NonEmpty(l?.SourceTypeCode);
            case "Summary":
                var s = FormulaTypeMap.ParseSummarySettings(field.Settings);
                if (NonEmpty(s?.ResultTypeCode) is { } result) return result;
                return SummaryFunctions.Normalize(s?.Function) switch
                {
                    SummaryFunctions.Count or SummaryFunctions.DistinctCount => "Number",
                    SummaryFunctions.Exists => "Boolean",
                    SummaryFunctions.Sum or SummaryFunctions.Avg => NonEmpty(s?.TargetTypeCode) ?? "Number",
                    SummaryFunctions.Min or SummaryFunctions.Max => NonEmpty(s?.TargetTypeCode),
                    _ => null
                };
            default:
                // Formula and Formula_*: the type the formula returns.
                return FormulaTypeMap.ExpressionAndType(field.TypeCode, field.Settings) is { } et ? FormulaAnalysis.ValueTypeCode(et.Type) : null;
        }
    }

    /// <summary>The tables whose changes make the field's stored value stale (for "Values are updating").</summary>
    public static IEnumerable<long> InputTables(AppField field) => field.TypeCode switch
    {
        "Lookup" when FormulaTypeMap.ParseLookupSettings(field.Settings)?.SourceTableId is long parent => [field.AppTableId, parent],
        "Summary" when FormulaTypeMap.ParseSummarySettings(field.Settings)?.ChildTableId is long child => [field.AppTableId, child],
        _ => [field.AppTableId]
    };

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// True when a Ready field's stored column holds exactly the value shown, never cut: everything but text kept as the 400-character sort
    /// key. A report showing such a formula reads it from c_ and does not work out what the formula reads.
    /// </summary>
    public static bool StoresExactValue(AppField field) =>
        MaterializeRules.ProjectedSqlType(field) is { } sqlType && sqlType != "NVARCHAR(400)";

    /// <summary>Fields whose stored text is kept whole (NVARCHAR(MAX), shown, never indexed): Combined Text summaries and rich-text formulas.
    /// Every other stored text is a 400-character sort key, which may be cut.</summary>
    public static bool IsFullTextStored(AppField field) =>
        field.TypeCode == "Formula_RichText"
        || (field.TypeCode == "Summary" && SummaryFunctions.Normalize(FormulaTypeMap.ParseSummarySettings(field.Settings)?.Function) == SummaryFunctions.CombinedText);
}

/// <summary>
/// The field the compute worker is working out right now: the read code it runs must not show that field from its own (old) stored column.
/// Flows with the async call (AsyncLocal), so builds side by side each exclude their own field.
/// </summary>
public static class StoredReadExclusion
{
    private static readonly System.Threading.AsyncLocal<long?> Current = new();
    private static readonly System.Threading.AsyncLocal<bool> AllFields = new();

    public static long? FieldId => Current.Value;

    /// <summary>True inside <see cref="Everything"/>: no stored value is read, every field is worked out from the data as it is now.</summary>
    public static bool IsEverything => AllFields.Value;

    /// <summary>
    /// For code that must see values exactly as they are at this moment, not a stored copy that may lag a few seconds behind a save:
    /// snapshots taken in a save, a field type conversion copying values, an import reading a table, a modify-permission check.
    /// </summary>
    public static System.IDisposable Everything()
    {
        var previous = AllFields.Value;
        AllFields.Value = true;
        return new RestoreAll(previous);
    }

    private sealed class RestoreAll(bool previous) : System.IDisposable
    {
        public void Dispose() => AllFields.Value = previous;
    }

    public static System.IDisposable Exclude(long fieldId)
    {
        var previous = Current.Value;
        Current.Value = fieldId;
        return new Restore(previous);
    }

    private sealed class Restore(long? previous) : System.IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
