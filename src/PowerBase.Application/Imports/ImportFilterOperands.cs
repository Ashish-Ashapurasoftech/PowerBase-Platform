using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;

namespace PowerBase.Application.Imports;

/// <summary>Lets either side of an import filter condition be an import variable or a formula, not only a field (or, on the right, a typed value).
/// A variable, or a formula that uses only functions and variables, is the same for every row, so it is worked out once per run and the condition is
/// rewritten into an ordinary one before anything else reads it:
/// <list type="bullet">
/// <item>On the right, a formula (<c>ValueMode "formula"</c>) becomes the typed value it gives; a variable on the right already was one (<c>{{var_x}}</c>).</item>
/// <item>On the left (<see cref="FilterCondition.LeftKind"/> "variable" or "formula") against a field, the two sides swap and the operator turns round
/// (greater than becomes less than), so <c>var_start &lt; [Date]</c> reads <c>[Date] &gt; start</c> and still runs in the database.</item>
/// <item>On the left against another constant (a typed value, a variable, a formula) the answer is the same for every row: the condition is replaced by
/// one that always holds or never holds.</item>
/// </list>
/// What cannot be turned into either (a constant that must contain or start with a field's value, for instance) is refused with a message that says so.</summary>
public static class ImportFilterOperands
{
    public const string KindVariable = "variable";
    public const string KindFormula = "formula";
    public const string FormulaMode = "formula";

    /// <summary>The Record ID# (the row number of a file) is in every source, so a condition on it can stand for "always" and "never": it is always above 0.</summary>
    private const long RecordIdFid = 3;
    /// <summary>A field no table has, to compare one constant against another with the row filter's own rules.</summary>
    private const int SyntheticFid = 9_000_000;
    private const string HiddenPrefix = "var___op";

    private static readonly IReadOnlyDictionary<string, string> Turned = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["eq"] = "eq", ["ne"] = "ne", ["gt"] = "lt", ["gte"] = "lte", ["lt"] = "gt", ["lte"] = "gte", ["date_eq"] = "date_eq", ["date_ne"] = "date_ne"
    };

    private static bool LeftIsConstant(FilterCondition c) => c.LeftKind is KindVariable or KindFormula;

    /// <summary>True when some condition of the import has a variable or a formula as an operand, so there is something to rewrite.</summary>
    public static bool Uses(ImportDefinitionConfig cfg)
    {
        var found = false;
        Walk(cfg, c => { if (LeftIsConstant(c) || string.Equals(c.ValueMode, FormulaMode, StringComparison.OrdinalIgnoreCase)) found = true; });
        return found;
    }

    /// <summary>The configuration with every such condition rewritten, or what is wrong. Returns the same object when there is nothing to rewrite.</summary>
    public static string? TryApply(ImportDefinitionConfig cfg, FormulaEngine engine, EvaluationOptions options, out ImportDefinitionConfig applied)
    {
        applied = cfg;
        if (!Uses(cfg)) return null;

        var copy = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(cfg))!;

        // Each distinct formula is worked out as one more variable (they may use the import's variables), then every condition reads its value.
        var formulaNames = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(copy, c =>
        {
            if (c.LeftKind == KindFormula && !string.IsNullOrWhiteSpace(c.LeftValue)) Name(c.LeftValue!);
            if (string.Equals(c.ValueMode, FormulaMode, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value)) Name(c.Value!);
        });
        string Name(string formula) => formulaNames.TryGetValue(formula, out var n) ? n : formulaNames[formula] = HiddenPrefix + (formulaNames.Count + 1);

        var all = copy.Variables.Concat(formulaNames.Select(p => new ImportVariable { Name = p.Value, Kind = ImportVariables.Formula, Formula = p.Key })).ToList();
        var (typed, error) = ImportVariables.EvaluateTyped(all, engine, options);
        if (error is not null) return Wording(error, formulaNames);

        string? problem = null;
        object? Constant(string? kind, string? value)
        {
            var key = kind == KindFormula ? (formulaNames.TryGetValue(value ?? "", out var n) ? n : "") : (value ?? "").Trim();
            if (key.Length > 0 && typed.TryGetValue(key, out var raw)) return raw;
            problem ??= kind == KindFormula ? "A formula in a filter is empty." : $"Unknown variable {value}. Add it to the import's variables or fix the name.";
            return null;
        }

        FilterCondition Rewrite(FilterCondition c)
        {
            // A formula on the right is the value it gives, as the same invariant text a typed value or a variable has.
            if (string.Equals(c.ValueMode, FormulaMode, StringComparison.OrdinalIgnoreCase))
            {
                c.Value = ImportVariables.TextOf(Constant(KindFormula, c.Value));
                c.ValueMode = null;
            }
            if (!LeftIsConstant(c)) return c;

            var left = Constant(c.LeftKind, c.LeftValue);
            var hasField = string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase) && c.ValueFieldId.HasValue;
            if (problem is not null) return c;
            if (c.Operator == ImportRowFilter.NoDuplicateOperator) { problem ??= "\"Does not have a duplicate\" needs a field on the left."; return c; }
            if (string.Equals(c.ValueMode, "ask", StringComparison.OrdinalIgnoreCase)) { problem ??= "\"Ask the user\" values are not available in an import, which runs without anyone to ask."; return c; }

            if (hasField)
            {
                // The constant stands on the left of a field: swap the sides, turning the operator round.
                if (!Turned.TryGetValue(c.Operator, out var turned))
                {
                    problem ??= "A variable or formula on the left can be compared with a field using is equal to, is not equal to, is greater than or less than, "
                                + "but not with the operator you chose. Put the field on the left, or compare the variable with a typed value.";
                    return c;
                }
                return new FilterCondition { FieldId = c.ValueFieldId!.Value, Operator = turned, Value = ImportVariables.TextOf(left), ValueMode = null };
            }

            // Constant against constant: the same answer for every row, found once with the row filter's own rules.
            var holds = Evaluate(left, c.Operator, c.Value);
            return new FilterCondition { FieldId = RecordIdFid, Operator = holds ? "gt" : "lt", Value = "0" };
        }

        void Fix(FilterGroup? group)
        {
            if (group is null) return;
            foreach (var node in group.Nodes)
            {
                if (node.Condition is { } c) node.Condition = Rewrite(c);
                Fix(node.Group);
            }
        }
        FixAll(copy, Fix);

        if (problem is not null) return problem;
        applied = copy;
        return null;
    }

    /// <summary>Like <see cref="TryApply"/> but throws a validation error, for the plan builder.</summary>
    public static ImportDefinitionConfig Apply(ImportDefinitionConfig cfg, FormulaEngine engine, EvaluationOptions options)
    {
        var error = TryApply(cfg, engine, options, out var applied);
        return error is null ? applied : throw new ValidationException(new Dictionary<string, string[]> { ["Import"] = [error] });
    }

    /// <summary>Whether a constant satisfies a condition against another typed value, by the rules a row is filtered with (a blank value ignores the condition).</summary>
    private static bool Evaluate(object? left, string op, string? value)
    {
        var field = new AppField { Id = -SyntheticFid, AppTableId = 0, Fid = SyntheticFid, Name = "constant", Label = "constant", TypeCode = "Text" };
        var tree = new FilterGroup { Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = SyntheticFid, Operator = op, Value = value } }] };
        var row = new Dictionary<string, object?> { [PowerBase.Domain.Constants.PhysicalNaming.ColumnName(SyntheticFid)] = left };
        return new ImportRowFilter(tree, [field]).Matches(row);
    }

    private static string Wording(string message, IReadOnlyDictionary<string, string> formulaNames)
    {
        var aboutFormula = false;
        foreach (var (_, hidden) in formulaNames)
        {
            if (!message.Contains(hidden, StringComparison.Ordinal)) continue;
            message = message.Replace($"The formula for the variable '{hidden}'", "A filter formula").Replace($"'{hidden}'", "a filter formula").Replace(hidden, "a filter formula");
            aboutFormula = true;
        }
        return aboutFormula ? message + " A filter formula can use functions and the import's variables, not the fields of the table." : message;
    }

    private static void Walk(ImportDefinitionConfig cfg, Action<FilterCondition> visit)
    {
        void Group(FilterGroup? g)
        {
            if (g is null) return;
            foreach (var n in g.Nodes) { if (n.Condition is { } c) visit(c); Group(n.Group); }
        }
        FixAll(cfg, Group);
    }

    /// <summary>Every filter of the import: its conditions, its table's, each sheet's, and the same for every extra table.</summary>
    private static void FixAll(ImportDefinitionConfig cfg, Action<FilterGroup?> each)
    {
        each(cfg.Conditions); each(cfg.TableConditions);
        foreach (var s in cfg.SheetMappings) each(s.Conditions);
        foreach (var t in cfg.AdditionalTargets)
        {
            each(t.Conditions);
            foreach (var s in t.SheetMappings) each(s.Conditions);
        }
    }
}
