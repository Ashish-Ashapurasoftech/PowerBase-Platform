using PowerBase.Formula.Diagnostics;

namespace PowerBase.Application.Formulas;

/// <summary>A formula compile/validation problem, flattened for the API and authoring UI.</summary>
public sealed class FormulaDiagnosticDto
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int Start { get; init; }
    public int Length { get; init; }
    public string Severity { get; init; } = string.Empty;

    public static FormulaDiagnosticDto From(FormulaDiagnostic d) => new()
    {
        Code = d.Code.ToString(),
        Message = d.Message,
        Start = d.Span.Start,
        Length = d.Span.Length,
        Severity = d.Severity.ToString(),
    };
}
