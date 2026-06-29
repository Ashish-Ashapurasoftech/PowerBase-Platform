using System.Globalization;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Querying;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

/// <summary>
/// QuickBase-style cross-table record functions. They reach other tables through the
/// <see cref="IRecordContext"/> (the host supplies a DB-backed implementation); in a
/// context without cross-table support they resolve to empty results.
///   • GetRecords(query, [tableId])        → record list matching a QuickBase query string
///   • GetRecordByUniqueField(tableId, fid, value) → the matching record (0 or 1)
///   • GetRecord(tableId, recordId)         → a single-record list (if it exists)
///   • GetFieldValues(records, fid)         → text list of a field's values across the records
///   • SumValues(records, fid)              → numeric sum of a field across the records
/// Field ids are the target table's numeric Fids, matching QuickBase.
/// </summary>
internal static class RecordFunctions
{
    public static void Register(FunctionRegistry r)
    {
        r.Add(Fn.RangeCtx("GetRecords", FormulaType.RecordList, new[] { P.Text }, new[] { P.Text },
            (a, _, ctx) => GetRecords(a, ctx)));

        r.Add(Fn.ExactCtx("GetRecordByUniqueField", FormulaType.RecordList, new[] { P.Text, P.Number, P.Text },
            (a, _, ctx) =>
            {
                var tableId = a[0].AsText();
                var id = ctx.FindRecordByField(tableId, ToFid(a[1]), a[2].AsText());
                return FormulaValue.RecordList(new RecordSet(tableId, id is null ? System.Array.Empty<long>() : new[] { id.Value }));
            }));

        r.Add(Fn.ExactCtx("GetRecord", FormulaType.RecordList, new[] { P.Text, P.Number },
            (a, _, ctx) =>
            {
                var tableId = a[0].AsText();
                var rid = (long)System.Math.Truncate(a[1].AsNumber());
                var exists = ctx.RecordExists(tableId, rid);
                return FormulaValue.RecordList(new RecordSet(tableId, exists ? new[] { rid } : System.Array.Empty<long>()));
            }));

        r.Add(Fn.ExactCtx("GetFieldValues", FormulaType.TextList, new[] { P.RecordList, P.Number },
            (a, _, ctx) =>
            {
                var set = a[0].AsRecordList();
                var raw = ctx.GetFieldValues(set.TableId, set.RecordIds, ToFid(a[1]));
                var texts = raw.Select(v => v is null ? string.Empty : RawText(v)).ToList();
                return FormulaValue.TextList(texts);
            }));

        r.Add(Fn.ExactCtx("SumValues", FormulaType.Number, new[] { P.RecordList, P.Number },
            (a, _, ctx) =>
            {
                var set = a[0].AsRecordList();
                var raw = ctx.GetFieldValues(set.TableId, set.RecordIds, ToFid(a[1]));
                decimal sum = 0m;
                foreach (var v in raw)
                    if (TryDecimal(v, out var d)) sum += d;
                return FormulaValue.Number(sum);
            }));
    }

    private static FormulaValue GetRecords(IReadOnlyList<FormulaValue> a, IRecordContext ctx)
    {
        var tableId = a.Count >= 2 ? a[1].AsText() : string.Empty;
        if (!RecordQueryParser.TryParse(a[0].AsText(), out var query))
            return FormulaValue.RecordList(new RecordSet(tableId, System.Array.Empty<long>()));
        var ids = ctx.QueryRecords(tableId, query);
        return FormulaValue.RecordList(new RecordSet(tableId, ids));
    }

    private static int ToFid(FormulaValue v) => (int)System.Math.Truncate(v.AsNumber());

    private static string RawText(object v) => v switch
    {
        string s => s,
        decimal d => d.ToString("0.###############", CultureInfo.InvariantCulture),
        double db => db.ToString("0.###############", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    };

    private static bool TryDecimal(object? v, out decimal value)
    {
        switch (v)
        {
            case null: value = 0; return false;
            case decimal d: value = d; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            case short sh: value = sh; return true;
            case byte by: value = by; return true;
            case double db: value = (decimal)db; return true;
            case float f: value = (decimal)f; return true;
            case bool b: value = b ? 1 : 0; return true;
            case string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p):
                value = p; return true;
            default: value = 0; return false;
        }
    }
}
