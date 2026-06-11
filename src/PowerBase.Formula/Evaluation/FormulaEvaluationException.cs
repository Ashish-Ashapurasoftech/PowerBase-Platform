namespace PowerBase.Formula.Evaluation;

/// <summary>
/// Thrown when evaluation is asked to do something impossible — e.g. evaluating a
/// formula that failed to compile, or an unknown function slipping past
/// compilation. Recoverable per-record conditions (divide-by-zero, bad
/// conversions) return a null <see cref="Types.FormulaValue"/> instead of throwing.
/// </summary>
public sealed class FormulaEvaluationException : Exception
{
    public FormulaEvaluationException(string message) : base(message) { }
}
