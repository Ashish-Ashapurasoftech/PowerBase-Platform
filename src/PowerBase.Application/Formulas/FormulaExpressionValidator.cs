using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Compile-checks a formula expression against a set of fields and returns error
/// messages. Used to reject invalid expressions at author time (e.g. when saving an
/// expression-mode form rule).
/// </summary>
public interface IFormulaExpressionValidator
{
    IReadOnlyList<string> Validate(string? expression, IReadOnlyList<AppField> fields, FormulaType? expectedType);
}

public sealed class FormulaExpressionValidator : IFormulaExpressionValidator
{
    private readonly FormulaEngine _engine;

    public FormulaExpressionValidator(FormulaEngine engine) => _engine = engine;

    public IReadOnlyList<string> Validate(string? expression, IReadOnlyList<AppField> fields, FormulaType? expectedType)
    {
        var compiled = _engine.Compile(expression ?? string.Empty, new AppFieldSchema(fields), expectedType);
        return compiled.Diagnostics
            .Where(d => d.Severity == FormulaSeverity.Error)
            .Select(d => d.Message)
            .ToList();
    }
}
