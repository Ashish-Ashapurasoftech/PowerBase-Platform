using System.Text.RegularExpressions;
using PowerBase.Application.Apps.Commands;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula.Binding;

namespace PowerBase.Application.Imports;

/// <summary>A named value that belongs to one import template and can be used anywhere in it: in a formula as <c>[var_name]</c>, and in a
/// filter value or a fixed value as <c>{{var_name}}</c>, for every table the import fills. The value is always text, like an app variable.</summary>
public sealed class ImportVariable
{
    /// <summary>Unique inside the import, ignoring case, and always starting with <see cref="ImportVariables.Prefix"/>.</summary>
    public string Name { get; set; } = "";
    public string Value { get => _value; set => _value = value ?? ""; }
    private string _value = "";
}

/// <summary>The import's variables as a formula sees them: <c>[var_name]</c> is a text constant (a formula converts it, <c>ToNumber([var_x])</c>).</summary>
public sealed class ImportVariableSchema : IVariableSchema
{
    private readonly Dictionary<string, VariableRef> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ImportVariableSchema(IEnumerable<ImportVariable> variables)
    {
        // Ids only have to differ from each other inside one compile; negative ones can never be mistaken for an app variable's id.
        var id = 0L;
        foreach (var v in variables) _byName[v.Name.Trim()] = new VariableRef(--id, v.Name.Trim(), v.Value);
    }

    public bool TryResolve(string name, out VariableRef variable) => _byName.TryGetValue(name.Trim(), out variable!);
}

public static class ImportVariables
{
    /// <summary>Every variable name starts with this, so a variable is never mistaken for a column or a field.</summary>
    public const string Prefix = "var_";
    public const int MaxCount = 50;
    public const int MaxNameLength = 50;
    public const int MaxValueLength = 1000;

    private static readonly Regex NameShape = new(@"^[vV][aA][rR]_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Token = new(@"\{\{\s*([^{}\s]+)\s*\}\}", RegexOptions.CultureInvariant);

    /// <summary>The schema formulas compile against, or null when the import has no variables (so nothing changes for such an import).</summary>
    public static IVariableSchema? Schema(IReadOnlyList<ImportVariable> variables) => variables.Count == 0 ? null : new ImportVariableSchema(variables);

    /// <summary>What is wrong with the variables on their own (names, prefix, repeats, sizes), or null.</summary>
    public static string? Check(IReadOnlyList<ImportVariable> variables)
    {
        if (variables.Count > MaxCount) return $"An import can have at most {MaxCount} variables.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variables)
        {
            var name = v.Name?.Trim() ?? "";
            if (name.Length == 0) return "Give every variable a name.";
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return $"The variable name '{Shown(name)}' must start with {Prefix}";
            if (name.Length > MaxNameLength) return $"The variable name '{Shown(name)}' is too long (at most {MaxNameLength} characters).";
            if (!NameShape.IsMatch(name)) return $"The variable name '{Shown(name)}' can only have letters, numbers and underscores after {Prefix}, and at least one.";
            if (AppVariableNameRules.IsReserved(name)) return AppVariableNameRules.ReservedMessage(name);
            if (!seen.Add(name)) return $"There are two variables named '{name}'. Variable names must be unique.";
            if (v.Value.Length > MaxValueLength) return $"The value of '{name}' is too long (at most {MaxValueLength} characters).";
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

    /// <summary>The configuration with every <c>{{var_name}}</c> in a filter value or a fixed value replaced by the variable's text, for every table
    /// the import fills. The values are never searched again, so a variable cannot use another. Returns the same object when there is nothing to
    /// replace, so an import without variables is untouched. An unknown name is an error, never kept as text.</summary>
    public static ImportDefinitionConfig Apply(ImportDefinitionConfig cfg)
    {
        var error = TryApply(cfg, out var applied);
        return error is null ? applied : throw new ValidationException(new Dictionary<string, string[]> { ["Import"] = [error] });
    }

    /// <summary>Like <see cref="Apply"/> but says what is wrong instead of throwing, for the check that decides whether an import still works.</summary>
    public static string? TryApply(ImportDefinitionConfig cfg, out ImportDefinitionConfig applied)
    {
        applied = cfg;
        if (!ImportJson.Serialize(cfg).Contains("{{", StringComparison.Ordinal)) return null;

        var copy = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(cfg))!;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in copy.Variables) values[v.Name.Trim()] = v.Value;

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

    private static string Shown(string name) => name.Length <= 30 ? name : name[..30] + "...";
}
