using PowerBase.Application.Formulas;
using PowerBase.Application.Formulas.Queries;

namespace PowerBase.Application.Pipelines.Queries.ValidatePipelineFormula;

/// <summary>A field a step value's formula may read: the name the formula uses for it (in brackets) and its type.</summary>
public sealed record PipelineFormulaCheckField(string Name, string? Type);

/// <param name="Expression">The formula being typed.</param>
/// <param name="Fields">The earlier steps' fields the formula may read, by the names the editor shows.</param>
public sealed record PipelineFormulaCheck(string Expression, List<PipelineFormulaCheckField>? Fields);

/// <summary>Checks a PowerFlow step value's formula as it is typed, the way the step will run it: the same formula language and
/// functions as a table's formula fields, reading the earlier steps' fields with their types. Reads and writes nothing.</summary>
public sealed class ValidatePipelineFormulaHandler(PowerBase.Formula.FormulaEngine engine)
{
    public const int MaxFields = 2000;

    public ValidateFormulaResult Handle(PipelineFormulaCheck check)
    {
        var expression = check.Expression ?? string.Empty;
        var fields = (check.Fields ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Take(MaxFields)
            .Select(f => new PipelineFormulaValue.Field(f.Name.Trim(), f.Type))
            .ToList();
        var (compiled, _, diagnostics) = PipelineFormulaValue.Compile(new PipelineFormulaValue.Definition(expression, fields), engine);
        return new ValidateFormulaResult
        {
            Valid = compiled is not null,
            ResultType = compiled?.ResultType.ToString() ?? string.Empty,
            Diagnostics = diagnostics.Select(FormulaDiagnosticDto.From).ToList(),
        };
    }
}
