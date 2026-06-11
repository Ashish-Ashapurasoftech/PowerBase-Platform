namespace PowerBase.Formula.Diagnostics;

public enum FormulaSeverity
{
    Error,
    Warning,
}

/// <summary>Stable machine-readable cause for a formula problem (drives UI messaging/i18n).</summary>
public enum FormulaErrorCode
{
    // Lexing
    UnexpectedCharacter,
    UnterminatedString,
    UnterminatedFieldReference,
    // Parsing
    UnexpectedToken,
    ExpectedToken,
    EmptyExpression,
    // Binding
    UnknownField,
    UnsupportedFieldType,
    UnsupportedReference,
    // Functions / types
    UnknownFunction,
    WrongArgumentCount,
    TypeMismatch,
    ResultTypeMismatch,
    // Evaluation (runtime)
    DivisionByZero,
    InvalidConversion,
    InvalidArgument,
}

/// <summary>
/// A single problem produced while compiling or evaluating a formula, anchored to
/// a <see cref="TextSpan"/> so the authoring UI can underline the offending text.
/// </summary>
public sealed record FormulaDiagnostic(
    FormulaErrorCode Code,
    string Message,
    TextSpan Span,
    FormulaSeverity Severity = FormulaSeverity.Error)
{
    public override string ToString() => $"{Severity} {Code} @ {Span.Start}: {Message}";
}
