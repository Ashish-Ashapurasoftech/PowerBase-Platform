using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Imports;

/// <summary>Applies the destination table's Custom Data Rule to imported rows. Same semantics as
/// CustomDataRuleValidator (a non-blank text result rejects the row), but the rule is compiled once per run
/// instead of once per row.</summary>
public sealed class ImportDataRuleGate
{
    private readonly FormulaEngine _engine;
    private readonly CompiledFormula _rule;
    private readonly CrossTableQueryContext _crossTable;
    private readonly EvaluationOptions _options;

    private ImportDataRuleGate(FormulaEngine engine, CompiledFormula rule, CrossTableQueryContext crossTable, EvaluationOptions options)
    {
        _engine = engine; _rule = rule; _crossTable = crossTable; _options = options;
    }

    /// <summary>Null when the table has no active (and valid) rule — nothing to enforce.</summary>
    public static async Task<ImportDataRuleGate?> CreateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IAppTableRepository tables, IAppFieldRepository fieldRepo,
        IRecordRepository records, FormulaEngine engine, CancellationToken ct)
    {
        if (!table.IsCustomDataRuleEnabled || string.IsNullOrWhiteSpace(table.CustomDataRule)) return null;
        var aliases = await AppTableAliasSchema.BuildAsync(tables, table.AppId, ct);
        var compiled = engine.Compile(table.CustomDataRule, new AppFieldSchema(fields), FormulaType.Text, aliases);
        if (compiled.HasErrors) return null; // stale rule: same fail-safe as CustomDataRuleValidator
        return new ImportDataRuleGate(engine, compiled, new CrossTableQueryContext(tables, fieldRepo, records, table),
            new EvaluationOptions { TableId = table.PublicId.ToString() });
    }

    /// <summary>The rule's violation message, or null when the row passes.</summary>
    public string? Check(IReadOnlyDictionary<long, object?> values)
    {
        var context = new CrossTableRecordContext(new ValuesRecordContext(values), _crossTable);
        try
        {
            var message = _engine.Evaluate(_rule, context, _options).AsText();
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch (FormulaEvaluationException) { return null; }
    }
}
