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
    VariableTypeMismatch,
    ResultTypeMismatch,
    // Evaluation (runtime)
    DivisionByZero,
    InvalidConversion,
    InvalidArgument,
    // Added later — appended so the numeric values above don't shift.
    /// <summary>A name that is both an app variable and a field; the variable is used (a warning, not an error).</summary>
    AmbiguousReference,
    /// <summary>The formula is nested more deeply than the parser can safely follow.</summary>
    FormulaTooComplex,
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
