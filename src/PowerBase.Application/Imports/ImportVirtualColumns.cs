using PowerBase.Application.Formulas;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Imports;

/// <summary>A column that exists only inside an import: a fixed text or a formula over the source's columns (and other virtual columns),
/// calculated for every row as it is read. It is never created in any table. A mapping can use it as its source like any column, and
/// other virtual columns' formulas can refer to it by name.</summary>
public sealed class ImportVirtualColumn
{
    /// <summary>Identifies the column inside the import, so a rename never breaks a mapping. At or above <see cref="ImportVirtual.FidBase"/>,
    /// which no table field and no file column reaches.</summary>
    public int Fid { get; set; }
    public string Name { get; set; } = "";
    /// <summary>"formula" or "fixed".</summary>
    public string Kind { get; set; } = ImportVirtual.Formula;
    public string? Formula { get; set; }
    public string? StaticValue { get; set; }
}

/// <summary>A virtual column checked and compiled for a run: the field the rest of the engine sees it through, and how each row's value is made.</summary>
public sealed record ResolvedVirtual(AppField Field, string Column, CompiledFormula? Formula, string? Text);

public static class ImportVirtual
{
    public const string Formula = "formula";
    public const string Fixed = "fixed";
    public const int FidBase = 1_000_000;
    public const int MaxColumns = 50;
    public const int MaxNameLength = 100;
    public const int MaxFixedLength = 1000;

    public static bool IsVirtualFid(long fid) => fid >= FidBase;

    /// <summary>Checks the virtual columns and compiles them, each against the source's fields and the virtual columns before it. The order
    /// returned is the order to calculate them in: a column always comes after every column its formula uses. A cycle, an unknown column and a
    /// result a column cannot hold are reported with the column's name. Null error means all is well. With <paramref name="lenient"/> a
    /// column that has a problem is left out instead of failing the lot: used to see what a formula being typed may refer to.</summary>
    public static (IReadOnlyList<ResolvedVirtual> Items, string? Error) Resolve(
        IReadOnlyList<ImportVirtualColumn> columns, IReadOnlyList<AppField> sourceFields, FormulaEngine engine, bool lenient = false)
    {
        if (columns.Count == 0) return ([], null);
        if (columns.Count > MaxColumns)
        {
            if (!lenient) return ([], $"An import can have at most {MaxColumns} virtual columns.");
            columns = columns.Take(MaxColumns).ToList();
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in sourceFields)
        {
            if (!string.IsNullOrWhiteSpace(f.Label)) taken.Add(f.Label.Trim());
            if (!string.IsNullOrWhiteSpace(f.Name)) taken.Add(f.Name.Trim());
        }
        var fids = new HashSet<int>();
        var valid = new List<ImportVirtualColumn>(columns.Count);
        foreach (var c in columns)
        {
            var problem = Check(c, taken, fids);
            if (problem is null) valid.Add(c);
            else if (!lenient) return ([], problem);
        }

        var known = new List<AppField>(sourceFields);
        var resolved = new List<ResolvedVirtual>(valid.Count);
        foreach (var c in valid.Where(c => c.Kind == Fixed))
        {
            var field = FieldOf(c, "Text");
            known.Add(field);
            resolved.Add(new ResolvedVirtual(field, PhysicalNaming.ColumnName(c.Fid), null, c.StaticValue));
        }

        // Each pass compiles what can be compiled with what is known so far; a column that uses another virtual column waits for it.
        var pending = valid.Where(c => c.Kind == Formula).ToList();
        while (pending.Count > 0)
        {
            var schema = new AppFieldSchema(known);
            var progressed = false;
            string? firstError = null;
            foreach (var c in pending.ToList())
            {
                var compiled = engine.Compile(c.Formula!, schema);
                if (compiled.Diagnostics.FirstOrDefault(d => d.Severity == FormulaSeverity.Error) is { } error)
                {
                    firstError ??= $"The formula for the virtual column '{c.Name.Trim()}' has an error: {error.Message}";
                    continue;
                }
                var code = FieldTypeOf(compiled.ResultType);
                if (code is null)
                {
                    if (lenient) { pending.Remove(c); progressed = true; continue; }
                    return ([], $"The virtual column '{c.Name.Trim()}' returns {compiled.ResultType}. Make it return text, a number, a checkbox, a date or a date and time.");
                }
                var field = FieldOf(c, code);
                known.Add(field);
                resolved.Add(new ResolvedVirtual(field, PhysicalNaming.ColumnName(c.Fid), compiled, null));
                pending.Remove(c);
                progressed = true;
            }
            if (progressed) continue;
            if (lenient) break;

            // Nothing more can be compiled: either a column refers to something that does not exist, or the rest refer to each other.
            var cycle = InCycle(pending);
            return cycle.Count > 0
                ? ([], $"The virtual columns {string.Join(", ", cycle.Select(c => "'" + c.Name.Trim() + "'"))} use each other, so none of them can be calculated first. Remove the circular reference.")
                : ([], firstError ?? "A virtual column could not be calculated.");
        }
        return (resolved, null);
    }

    /// <summary>The type a virtual column of this result gets, or null when an import cannot hold that result.</summary>
    public static string? FieldTypeOf(FormulaType resultType) => resultType switch
    {
        FormulaType.Text or FormulaType.Null => "Text",
        FormulaType.Number => "Number",
        FormulaType.Bool => "Boolean",
        FormulaType.Date => "Date",
        FormulaType.DateTime => "DateTime",
        _ => null
    };

    /// <summary>What is wrong with one column on its own (name, id, fixed value or formula text), or null; reserves its name and id.</summary>
    private static string? Check(ImportVirtualColumn c, HashSet<string> taken, HashSet<int> fids)
    {
        var name = c.Name?.Trim() ?? "";
        if (name.Length == 0) return "Give every virtual column a name.";
        if (name.Length > MaxNameLength) return $"The virtual column name '{name[..30]}...' is too long (at most {MaxNameLength} characters).";
        if (name.Any(char.IsControl) || name.Contains('[') || name.Contains(']'))
            return $"The virtual column name '{name}' cannot contain square brackets or control characters.";
        if (c.Fid < FidBase || !fids.Add(c.Fid)) return $"The virtual column '{name}' is not valid.";
        if (!taken.Add(name)) return $"There is already a column named '{name}'. Give the virtual column another name.";
        if (c.Kind == Fixed)
        {
            if (c.StaticValue is null) return $"Enter the fixed value for '{name}'.";
            if (c.StaticValue.Length > MaxFixedLength) return $"The fixed value of '{name}' is too long (at most {MaxFixedLength} characters).";
        }
        else if (c.Kind == Formula)
        {
            if (string.IsNullOrWhiteSpace(c.Formula)) return $"Enter the formula for '{name}'.";
            if (c.Formula.Length > ImportPlanBuilder.MaxFormulaLength) return $"The formula for '{name}' is too long (at most {ImportPlanBuilder.MaxFormulaLength} characters).";
            if (ImportFormulaGuard.IsTooDeep(c.Formula)) return $"The formula for '{name}' is nested too deeply (at most {ImportFormulaGuard.MaxNesting} levels).";
        }
        else return $"The virtual column '{name}' must be a fixed value or a formula.";
        return null;
    }

    /// <summary>The columns that, through the others, end up using themselves (a column using itself counts).</summary>
    private static List<ImportVirtualColumn> InCycle(List<ImportVirtualColumn> columns)
    {
        bool Uses(ImportVirtualColumn a, ImportVirtualColumn b) => a.Formula!.Contains("[" + b.Name.Trim() + "]", StringComparison.OrdinalIgnoreCase);
        var reach = columns.ToDictionary(c => c, c => columns.Where(o => Uses(c, o)).ToHashSet());
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var c in columns)
                foreach (var via in reach[c].ToList())
                    foreach (var next in reach[via])
                        if (reach[c].Add(next)) changed = true;
        }
        return columns.Where(c => reach[c].Contains(c)).ToList();
    }

    private static AppField FieldOf(ImportVirtualColumn c, string typeCode) =>
        new() { Id = -c.Fid, AppTableId = 0, Fid = c.Fid, Name = c.Name.Trim(), Label = c.Name.Trim(), TypeCode = typeCode };
}

/// <summary>Adds the virtual columns to every row a source hands over, so the rest of the engine reads them like any other column.
/// Used only when the import has virtual columns; an import without them reads the source directly.</summary>
public sealed class ImportVirtualColumnReader(IImportChunkReader inner, ImportPlan plan, FormulaEngine engine) : IImportChunkReader
{
    private readonly IReadOnlyDictionary<long, string> _fidToColumn = plan.SourceFields.Where(f => f.Fid.HasValue)
        .Select(f => (Fid: (long)f.Fid!.Value, Column: PhysicalNaming.GetPhysicalColumnName(f)))
        .Concat(plan.Virtuals.Select(v => (Fid: (long)v.Field.Fid!.Value, v.Column)))
        .ToDictionary(x => x.Fid, x => x.Column);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    public async Task<ImportSourceReader.Chunk> ReadChunkAsync(ImportPlan readPlan, long afterId, long maxId, CancellationToken ct)
    {
        var chunk = await inner.ReadChunkAsync(readPlan, afterId, maxId, ct);
        if (chunk.Rows.Count == 0) return chunk;
        var rows = new List<IReadOnlyDictionary<string, object?>>(chunk.Rows.Count);
        foreach (var source in chunk.Rows)
        {
            var row = new Dictionary<string, object?>(source);
            var context = new RowRecordContext(row, _fidToColumn);
            foreach (var v in plan.Virtuals)
            {
                if (v.Formula is null) { row[v.Column] = v.Text; continue; }
                try { row[v.Column] = FormulaRawValue.ToRaw(engine.Evaluate(v.Formula, context, plan.FormulaOptions)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { row[v.Column] = null; } // a row the formula cannot calculate gets a blank
            }
            rows.Add(row);
        }
        return chunk with { Rows = rows };
    }
}
