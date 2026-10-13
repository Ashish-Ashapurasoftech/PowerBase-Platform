using System.Text;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Lexing;

namespace PowerBase.Formula.Binding;

/// <summary>
/// Rewrites the <c>[Field Name]</c> tokens of an expression in place, leaving every other
/// character — string literals, comments, operators, spacing — exactly as written. Works on the
/// lexer's token spans, so a <c>"[not a field]"</c> inside a text literal is never touched.
/// </summary>
public static class FieldReferenceRewriter
{
    /// <summary>
    /// Calls <paramref name="map"/> with each field reference's decoded name. A non-null result
    /// replaces that token with <c>[result]</c> (re-escaped); null leaves it as typed. An
    /// expression with an unterminated reference is returned unchanged — it is malformed and
    /// there is no safe span to rewrite.
    /// </summary>
    public static string Rewrite(string? expression, Func<string, string?> map)
    {
        if (string.IsNullOrEmpty(expression)) return expression ?? string.Empty;

        var (tokens, diagnostics) = new Lexer(expression).Tokenize();
        if (diagnostics.Any(d => d.Code == FormulaErrorCode.UnterminatedFieldReference)) return expression;

        StringBuilder? sb = null;
        var copiedUpTo = 0;
        foreach (var token in tokens)
        {
            if (token.Kind != TokenKind.FieldRef || token.Value is not string name) continue;

            var replacement = map(name);
            if (replacement is null) continue;

            var rewritten = "[" + Escape(replacement) + "]";
            if (string.Equals(rewritten, token.Text, StringComparison.Ordinal)) continue;

            sb ??= new StringBuilder(expression.Length + 16);
            sb.Append(expression, copiedUpTo, token.Span.Start - copiedUpTo);
            sb.Append(rewritten);
            copiedUpTo = token.Span.End;
        }

        if (sb is null) return expression;
        sb.Append(expression, copiedUpTo, expression.Length - copiedUpTo);
        return sb.ToString();
    }

    /// <summary>Backslash-escapes the characters that end or escape a field reference.</summary>
    public static string Escape(string name)
    {
        if (name.IndexOfAny(['\\', '[', ']']) < 0) return name;

        var sb = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (c is '\\' or '[' or ']') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
