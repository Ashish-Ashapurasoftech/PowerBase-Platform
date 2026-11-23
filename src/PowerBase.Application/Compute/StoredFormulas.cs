using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Binding;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Compute;

/// <summary>What a formula is, as far as storing it goes: its expression and type, and exactly which fields of its table it reads.</summary>
public sealed record FormulaInfo(string Expression, FormulaType ResultType, IReadOnlyList<long> Reads, bool UsesDynamicFieldReference,
    bool UsesVariables, bool HasErrors);

/// <summary>Compiles a formula with the same engine and schema the API uses (FormulaProjector), only to see what it reads.</summary>
public static class FormulaAnalysis
{
    private static readonly FormulaEngine Engine = new();

    public static FormulaInfo? Analyze(AppField formula, IReadOnlyList<AppField> tableFields)
    {
        if (FormulaTypeMap.ExpressionAndType(formula.TypeCode, formula.Settings) is not { } et) return null;
        var usesVariables = et.Expression.Contains("[" + VariableIdReference.Prefix, StringComparison.OrdinalIgnoreCase);
        if (usesVariables) return new FormulaInfo(et.Expression, et.Type, [], false, true, false);
        var compiled = Engine.Compile(et.Expression, new AppFieldSchema(tableFields), et.Type);
        return new FormulaInfo(et.Expression, et.Type, compiled.ReferencedFieldIds, compiled.UsesDynamicFieldReference, false, compiled.HasErrors);
    }

    /// <summary>The field type code of the value a formula of this type shows; null for the types a stored column cannot hold.</summary>
    public static string? ValueTypeCode(FormulaType type) => type switch
    {
        FormulaType.Text => "Text",
        FormulaType.Number => "Number",
        FormulaType.Date => "Date",
        FormulaType.DateTime => "DateTime",
        FormulaType.Bool => "Boolean",
        _ => null
    };

    /// <summary>The c_ column type for a formula result.</summary>
    public static string? StoredSqlType(FormulaType type) => type switch
    {
        FormulaType.Text => "NVARCHAR(400)",
        FormulaType.Number => "DECIMAL(38,8)",
        FormulaType.Date => "DATE",
        FormulaType.DateTime => "DATETIME2(0)",
        FormulaType.Bool => "BIT",
        _ => null
    };
}

/// <summary>
/// Evaluates a stored formula for a batch of rows exactly as FormulaProjector does for a report (same engine, same row contexts), with the
/// stored (c_) values of the calculated fields it reads seeded in — the way the projector seeds Lookup / Summary values. Only formulas the
/// rules allowed get here: no clock, no viewer, no cross-table reads, no app variables, so nothing else in the projector applies.
/// </summary>
public sealed class StoredFormulaEvaluator
{
    private static readonly FormulaEngine Engine = new();
    private readonly ConcurrentDictionary<string, CompiledFormula?> _compiled = new();

    public IReadOnlyList<(long Id, object? Value)> Evaluate(FormulaPlan plan, IReadOnlyList<AppField> tableFields, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var compiled = _compiled.GetOrAdd($"{plan.FieldId}:{plan.Expression}", _ =>
        {
            var c = Engine.Compile(plan.Expression, new AppFieldSchema(tableFields), plan.ResultType);
            return c.HasErrors ? null : c;
        });
        var fidToColumn = tableFields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First().PhysicalColumnName ?? string.Empty);
        var options = new EvaluationOptions { UtcNow = DateTime.UtcNow, FieldTypes = FormulaTypeMap.FieldTypesByFid(tableFields) };

        var result = new List<(long, object?)>(rows.Count);
        foreach (var row in rows)
        {
            var id = Convert.ToInt64(row["Id"], CultureInfo.InvariantCulture);
            object? value = null;
            if (compiled is not null)
            {
                var seed = new Dictionary<long, object?>();
                foreach (var fid in plan.StoredReads)
                    seed[fid] = row.TryGetValue(PhysicalNaming.MaterializedColumnName(fid), out var v) && v is not DBNull ? v : null;
                var context = new SeededContext(new StructuredValueContext(new RowRecordContext(row, fidToColumn), tableFields), seed);
                try { value = FormulaRawValue.ToRaw(Engine.Evaluate(compiled, context, options)); }
                catch (FormulaEvaluationException) { value = null; }
            }
            result.Add((id, value));
        }
        return result;
    }

    /// <summary>A raw formula value as the c_ column stores it (text cut to the sort-key length, dates as dates).</summary>
    public static object? ToStored(object? raw, FormulaType type)
    {
        if (raw is null) return null;
        switch (type)
        {
            case FormulaType.Text:
                var s = Convert.ToString(raw, CultureInfo.InvariantCulture)!;
                return s.Length > 400 ? s[..400] : s;
            case FormulaType.Number:
                return Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
            case FormulaType.Bool:
                return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
            case FormulaType.Date:
            case FormulaType.DateTime:
                return raw is DateTime dt ? dt : DateTime.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            default:
                return null;
        }
    }

    /// <summary>The value a unique formula is compared by: trimmed (the column's collation decides case, as for every unique field).</summary>
    public static object? Normalize(object? stored) => stored is string s ? (s.Trim() is { Length: > 0 } t ? t : null) : stored;

    private sealed class SeededContext(IRecordContext inner, IReadOnlyDictionary<long, object?> seed) : IRecordContext
    {
        public object? GetValue(long fid) => seed.TryGetValue(fid, out var v) ? v : inner.GetValue(fid);
    }
}
