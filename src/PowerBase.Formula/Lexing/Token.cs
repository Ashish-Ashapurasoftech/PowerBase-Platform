using PowerBase.Formula.Diagnostics;

namespace PowerBase.Formula.Lexing;

/// <summary>
/// A lexical token. <see cref="Value"/> carries the decoded payload for literals
/// (decimal for Number, unescaped string for String, field name for FieldRef);
/// it is null for operators and keywords.
/// </summary>
public readonly record struct Token(TokenKind Kind, string Text, TextSpan Span, object? Value = null);
