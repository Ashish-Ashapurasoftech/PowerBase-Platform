using System.Globalization;
using System.Text.RegularExpressions;
using PowerBase.Application.Apps.Commands;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;

namespace PowerBase.Application.Imports;

/// <summary>A named value that belongs to one import and can be used anywhere in it, for every table it fills: the same idea as a virtual column, but global.
/// It is either a fixed text or a formula worked out once per run (so "today minus 30 days" is the same for every table and every row). It has the type its
/// formula returns: text, number, checkbox, date or date and time. In a formula write <c>[var_name]</c> (typed, like any column); in a filter value or a fixed
/// value write <c>{{var_name}}</c>; a mapping may also take it as its source. A variable's formula can use functions and other variables but no source columns.</summary>
public sealed class ImportVariable
{
    /// <summary>Unique inside the import, ignoring case, and always starting with <see cref="ImportVariables.Prefix"/>.</summary>
    public string Name { get; set; } = "";
    /// <summary>"fixed" (the text in <see cref="Value"/>) or "formula" (the result of <see cref="Formula"/>). Saved without it, it is fixed.</summary>
    public string Kind { get; set; } = ImportVariables.Fixed;
    public string Value { get => _value; set => _value = value ?? ""; }
    private string _value = "";
    public string? Formula { get; set; }
    /// <summary>The id a mapping names this variable by when it takes it as its source: handed out by the screen once, in <see cref="ImportVariables.FidBase"/> up to
    /// <see cref="ImportVariables.FidFallback"/>, and kept, so deleting another variable never makes a mapping point at a different one. Saved without one, it is
    /// numbered by position above that range.</summary>
    public int? Fid { get; set; }
}

public static class ImportVariables
{
    /// <summary>Every variable name starts with this, so a variable is never mistaken for a column or a field.</summary>
    public const string Prefix = "var_";
    public const string Fixed = "fixed";
    public const string Formula = "formula";
    public const int MaxCount = 50;
    public const int MaxNameLength = 50;
    public const int MaxValueLength = 1000;
    /// <summary>Variables are calculated like virtual columns, in a range of their own above the ones a screen hands out.</summary>
    public const int FidBase = 2_000_000;
    /// <summary>Variables saved without an id of their own are numbered from here, by position.</summary>
    public const int FidFallback = FidBase + 500_000;

    private static readonly Regex NameShape = new(@"^[vV][aA][rR]_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Token = new(@"\{\{\s*([^{}\s]+)\s*\}\}", RegexOptions.CultureInvariant);

    /// <summary>What is wrong with the variables on their own (names, prefix, repeats, sizes, a missing formula), or null.</summary>
    public static string? Check(IReadOnlyList<ImportVariable> variables)
    {
        if (variables.Count > MaxCount) return $"An import can have at most {MaxCount} variables.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<int>();
        foreach (var v in variables)
        {
            var name = v.Name?.Trim() ?? "";
            if (name.Length == 0) return "Give every variable a name.";
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return $"The variable name '{Shown(name)}' must start with {Prefix}";
            if (name.Length > MaxNameLength) return $"The variable name '{Shown(name)}' is too long (at most {MaxNameLength} characters).";
            if (!NameShape.IsMatch(name)) return $"The variable name '{Shown(name)}' can only have letters, numbers and underscores after {Prefix}, and at least one.";
            if (AppVariableNameRules.IsReserved(name)) return AppVariableNameRules.ReservedMessage(name);
            if (!seen.Add(name)) return $"There are two variables named '{name}'. Variable names must be unique.";
            if (v.Fid is { } own && (own < FidBase || own >= FidFallback || !ids.Add(own))) return $"The variable '{name}' is not valid.";
            if (v.Kind == Formula)
            {
                if (string.IsNullOrWhiteSpace(v.Formula)) return $"Enter the formula for '{name}'.";
                if (v.Formula.Length > ImportPlanBuilder.MaxFormulaLength) return $"The formula for '{name}' is too long (at most {ImportPlanBuilder.MaxFormulaLength} characters).";
                if (ImportFormulaGuard.IsTooDeep(v.Formula)) return $"The formula for '{name}' is nested too deeply (at most {ImportFormulaGuard.MaxNesting} levels).";
            }
            else if (v.Kind != Fixed) return $"The variable '{name}' must be a fixed value or a formula.";
            else if (v.Value.Length > MaxValueLength) return $"The value of '{name}' is too long (at most {MaxValueLength} characters).";
        }
        return null;
    }

    /// <summary>A variable may not share its name with a source column or a virtual column: a formula would not know which one is meant.</summary>
    public static string? Collision(IReadOnlyList<ImportVariable> variables, IEnumerable<AppField> sourceFields, IEnumerable<ImportVirtualColumn> virtuals)
    {
        if (variables.Count == 0) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in sourceFields)
        {
            if (!string.IsNullOrWhiteSpace(f.Label)) names.Add(f.Label.Trim());
            if (!string.IsNullOrWhiteSpace(f.Name)) names.Add(f.Name.Trim());
        }
        foreach (var c in virtuals) if (!string.IsNullOrWhiteSpace(c.Name)) names.Add(c.Name.Trim());
        var clash = variables.FirstOrDefault(v => names.Contains(v.Name.Trim()));
        return clash is null ? null : $"There is already a column named '{clash.Name.Trim()}'. Give the variable another name.";
    }

    /// <summary>The variables as virtual columns, which is how every plan reads them: a formula or a fixed text, calculated for each row, typed by its result.
    /// They come before the import's own virtual columns, which may use them. The ids are stable (by position), so a variable keeps its id whatever is
    /// around it.</summary>
    public static List<ImportVirtualColumn> AsColumns(IReadOnlyList<ImportVariable> variables) =>
        variables.Select((v, i) => new ImportVirtualColumn
        {
            Fid = v.Fid ?? FidFallback + i, Name = v.Name.Trim(), Kind = v.Kind == Formula ? ImportVirtual.Formula : ImportVirtual.Fixed,
            Formula = v.Kind == Formula ? v.Formula : null, StaticValue = v.Kind == Formula ? null : v.Value
        }).ToList();

    /// <summary>Works every variable out once: its formula is compiled with no source columns (only functions and other variables), and calculated with the
    /// run's clock and user. Returns each variable's value as the text a filter value or a fixed value takes, or what is wrong.</summary>
    public static (IReadOnlyDictionary<string, string> Values, string? Error) Evaluate(IReadOnlyList<ImportVariable> variables, FormulaEngine engine, EvaluationOptions options)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (variables.Count == 0) return (values, null);

        var (resolved, error) = ImportVirtual.Resolve(AsColumns(variables), [], engine);
        if (error is not null) return (values, Wording(error));

        var row = new Dictionary<string, object?>();
        var fidToColumn = resolved.ToDictionary(r => (long)r.Field.Fid!.Value, r => r.Column);
        var context = new RowRecordContext(row, fidToColumn);
        foreach (var v in resolved)
        {
            object? raw;
            try { raw = v.Formula is null ? v.Text : FormulaRawValue.ToRaw(engine.Evaluate(v.Formula, context, options)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (values, $"The variable '{v.Field.Name}' could not be calculated: {ex.Message}"); }
            row[v.Column] = raw;
            values[v.Field.Name] = TextOf(raw);
        }
        return (values, null);
    }

    /// <summary>A value as text for a filter value or a fixed value: numbers and dates in the invariant form the rest of the import reads.</summary>
    public static string TextOf(object? raw) => raw switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        decimal d => d.ToString("0.############################", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => raw.ToString() ?? ""
    };

    /// <summary>The configuration with every <c>{{var_name}}</c> in a filter value or a fixed value replaced by the variable's text, for every table the import
    /// fills. The values are never searched again, so a variable cannot use another this way. Returns the same object when there is nothing to replace, so an
    /// import without variables is untouched. An unknown name is an error, never kept as text.</summary>
    public static ImportDefinitionConfig Apply(ImportDefinitionConfig cfg, IReadOnlyDictionary<string, string> values)
    {
        var error = TryApply(cfg, values, out var applied);
        return error is null ? applied : throw new ValidationException(new Dictionary<string, string[]> { ["Import"] = [error] });
    }

    /// <summary>Like <see cref="Apply"/> but says what is wrong instead of throwing, for the check that decides whether an import still works.</summary>
    public static string? TryApply(ImportDefinitionConfig cfg, IReadOnlyDictionary<string, string> values, out ImportDefinitionConfig applied)
    {
        applied = cfg;
        if (!ImportJson.Serialize(cfg).Contains("{{", StringComparison.Ordinal)) return null;

        var copy = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(cfg))!;
        string? problem = null;
        string? Replace(string? text)
        {
            if (text is null || !text.Contains("{{", StringComparison.Ordinal)) return text;
            return Token.Replace(text, match =>
            {
                var name = match.Groups[1].Value;
                if (values.TryGetValue(name, out var value)) return value;
                // Only a name that looks like a variable is one; any other {{text}} is left as the person typed it.
                if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) problem ??= $"Unknown variable {{{{{name}}}}}. Add it to the import's variables or fix the name.";
                return match.Value;
            });
        }
        void Mappings(IEnumerable<ImportFieldMapping> mappings) { foreach (var m in mappings) m.StaticValue = Replace(m.StaticValue); }
        void Virtuals(IEnumerable<ImportVirtualColumn> columns) { foreach (var c in columns) c.StaticValue = Replace(c.StaticValue); }
        void Conditions(FilterGroup? group)
        {
            if (group is null) return;
            foreach (var node in group.Nodes)
            {
                if (node.Condition is { } c) c.Value = Replace(c.Value);
                Conditions(node.Group);
            }
        }
        void Sheets(IEnumerable<ImportSheetMapping> sheets)
        {
            foreach (var s in sheets) { Mappings(s.Mappings); Virtuals(s.VirtualColumns); Conditions(s.Conditions); }
        }

        Conditions(copy.Conditions); Conditions(copy.TableConditions);
        Mappings(copy.Mappings); Virtuals(copy.VirtualColumns); Sheets(copy.SheetMappings);
        foreach (var t in copy.AdditionalTargets) { Conditions(t.Conditions); Mappings(t.Mappings); Sheets(t.SheetMappings); }

        if (problem is not null) return problem;
        applied = copy;
        return null;
    }

    /// <summary>The messages the virtual column code gives, said for a variable.</summary>
    private static string Wording(string message) => message.Replace("virtual columns", "variables").Replace("virtual column", "variable");

    private static string Shown(string name) => name.Length <= 30 ? name : name[..30] + "...";
}
