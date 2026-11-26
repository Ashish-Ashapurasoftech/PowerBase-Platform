using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Querying;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Number formulas that SQL can compute exactly (<see cref="FormulaSqlTranslator"/>): handed to the repository as stand-ins whose
/// "column" is the SQL expression, so a report filters, sorts, counts and totals by them in the database over every record —
/// the same mechanism Ready stored columns (c_{fid}) use (<see cref="Compute.MaterializedReadFields"/>). Only the copy given to SQL
/// is replaced: the value a report shows is still worked out by the formula projector, so it is exactly what it always was.
/// <para>A formula is translated only when every field it reads (directly or through other translated formulas) has a plain,
/// unencrypted SQL column: Number / Currency / Percent / Rating, or Record ID#. Anything else keeps today's path.</para>
/// </summary>
public static class SqlFormulaFields
{
    private static readonly FormulaEngine Engine = new();

    private static readonly HashSet<string> NumberColumnTypes = new(StringComparer.Ordinal) { "Number", "Currency", "Percent", "Rating" };

    /// <summary>Field Id → stand-in, for every Number formula of <paramref name="fields"/> that translates (and is not in <paramref name="skipFieldIds"/>,
    /// e.g. a Ready stored one, which reads its own indexed column instead).</summary>
    public static IReadOnlyDictionary<long, AppField> Build(IReadOnlyList<AppField> fields, bool isAppEncrypted, IReadOnlySet<long>? skipFieldIds = null)
    {
        var result = new Dictionary<long, AppField>();
        var byFid = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        if (!byFid.Values.Any(IsNumberFormula)) return result;

        var schema = new AppFieldSchema(fields);
        var expressions = new Dictionary<long, string?>();   // fid → SQL (null = not translatable)
        var visiting = new HashSet<long>();

        string? ColumnOf(long fid)
        {
            if (!byFid.TryGetValue(fid, out var f)) return null;
            if (fid == 3 && f.IsSystem) return "Id";                       // Record ID# (the system field, never a user field)
            if (Relationships.EncryptedRowFilter.IsEncrypted(f, isAppEncrypted)) return null;
            if (!f.IsSystem && NumberColumnTypes.Contains(f.TypeCode)) return PhysicalNaming.ColumnName((int)fid);
            if (IsNumberFormula(f)) return ExpressionOf(fid);              // another formula, inlined
            return null;
        }

        string? ExpressionOf(long fid)
        {
            if (expressions.TryGetValue(fid, out var known)) return known;
            if (!visiting.Add(fid)) return null;                           // a cycle: never translated
            string? sql = null;
            var f = byFid[fid];
            if (FormulaTypeMap.ExpressionAndType(f.TypeCode, f.Settings) is { } et)
            {
                var compiled = Engine.Compile(et.Expression, schema, et.Type);
                if (!compiled.HasErrors) sql = FormulaSqlTranslator.TryTranslate(compiled, ColumnOf);
            }
            visiting.Remove(fid);
            expressions[fid] = sql;
            return sql;
        }

        foreach (var (fid, f) in byFid)
        {
            if (!IsNumberFormula(f) || skipFieldIds?.Contains(f.Id) == true) continue;
            if (Relationships.EncryptedRowFilter.IsEncrypted(f, isAppEncrypted)) continue;
            if (ExpressionOf(fid) is not { } sql) continue;
            var s = f.Clone();
            s.TypeCode = "Number";
            s.IsSystem = true;            // the repository reads a system field's PhysicalColumnName and never SELECTs it
            s.PhysicalColumnName = sql;   // not "c_…": BuildFieldColumnList leaves it out of the SELECT list
            s.IsEncrypted = false;
            s.Settings = null;
            result[f.Id] = s;
        }
        return result;
    }

    /// <summary>The fields with every translated formula replaced by its stand-in; the same list when none is.</summary>
    public static IReadOnlyList<AppField> Substitute(IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, AppField> standIns)
    {
        if (standIns.Count == 0 || !fields.Any(f => standIns.ContainsKey(f.Id))) return fields;
        return fields.Select(f => standIns.TryGetValue(f.Id, out var s) ? s : f).ToList();
    }

    /// <summary>The Fids of the translated formulas (they are now SQL-readable, like stored columns).</summary>
    public static IReadOnlySet<long> Fids(IReadOnlyDictionary<long, AppField> standIns) =>
        standIns.Values.Where(s => s.Fid.HasValue).Select(s => (long)s.Fid!.Value).ToHashSet();

    private static bool IsNumberFormula(AppField f) =>
        FormulaTypeMap.IsFormulaComputed(f.TypeCode, f.Settings)
        && FormulaTypeMap.ExpressionAndType(f.TypeCode, f.Settings) is { Type: PowerBase.Formula.Types.FormulaType.Number };
}
