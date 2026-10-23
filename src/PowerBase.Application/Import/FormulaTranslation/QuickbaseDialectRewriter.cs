using System.Text.RegularExpressions;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Lexing;
using PowerBase.Formula.Syntax;

namespace PowerBase.Application.Import.FormulaTranslation;

/// <summary>
/// Rewrites the handful of places where Quickbase's formula dialect is looser than PowerBase's,
/// so a formula that only differs by dialect can be imported instead of flagged.
///
/// This lives in the importer rather than in <c>PowerBase.Formula</c> on purpose: PowerBase's
/// strictness is deliberate (there are tests asserting <c>[Text] &amp; 1</c> is an error), and a
/// formula the importer stores has to be valid under the same rules the authoring UI enforces —
/// otherwise the field would import "successfully" and then show as invalid the moment someone
/// opened it. Bridging the two dialects is the translator's job, and the rewritten expression is
/// what gets persisted, so what runs is exactly what the editor will re-validate.
///
/// Handled: a non-text operand of <c>&amp;</c> or a Text parameter (wrapped in <c>ToText</c>), a non-text side of
/// <c>=</c>/<c>&lt;&gt;</c> against text (Quickbase compares as text), a text value given where a User is expected
/// (<c>ToUser</c>), and Quickbase's multi-branch <c>If(c1,v1,c2,v2,…,else)</c> (nested <c>If</c>).
/// </summary>
internal static class QuickbaseDialectRewriter
{
    /// <summary>"Fn argument 2 expects Text but got Number." — the checker reports these against
    /// the whole call's span, so the index has to come from the message to know which argument to
    /// wrap. Anchored and narrow so an unrelated message can't be misread as one of these.</summary>
    private static readonly Regex TextArgMismatch =
        new(@"^(?<fn>\w+) argument (?<index>\d+) expects Text but got \w+\.$", RegexOptions.Compiled);

    private static readonly Regex UserArgMismatch =
        new(@"^(?<fn>\w+) argument (?<index>\d+) expects User but got Text\.$", RegexOptions.Compiled);

    private static readonly Regex EqualityMismatch =
        new(@"^Operator '(?<op>=|<>)' cannot compare (?<l>\w+) and (?<r>\w+)\.$", RegexOptions.Compiled);

    /// <summary>Text compared with a number by &lt; &gt; &lt;= &gt;=: Quickbase reads the text as a number.</summary>
    private static readonly Regex OrderingMismatch =
        new(@"^Operator '(?<op><|>|<=|>=)' cannot compare (?<l>\w+) and (?<r>\w+)\.$", RegexOptions.Compiled);

    /// <summary>PowerBase's Min/Max take numbers; Quickbase's also take dates and times.</summary>
    private static readonly Regex MinMaxNonNumber =
        new(@"^(?<fn>Min|Max) argument \d+ expects Number but got (?<t>Date|DateTime|Time|Duration)\.$", RegexOptions.Compiled);

    private static readonly Regex IfArity =
        new(@"^If expects 2[–-]3 argument\(s\) but got (?<n>\d+)\.$", RegexOptions.Compiled);

    private static readonly HashSet<string> TextCoercible = new(StringComparer.Ordinal)
        { "Number", "Date", "DateTime", "Time", "Duration", "Bool" };

    private enum Wrap { ToText, ToUser }

    /// <summary>Attempts to rewrite <paramref name="expression"/> to fix the dialect differences
    /// implied by <paramref name="diagnostics"/>. Returns null when nothing was rewritten, so the
    /// caller can keep the original diagnostics rather than reporting a pointless retry.</summary>
    public static string? TryRewrite(string expression, IReadOnlyList<FormulaDiagnostic> diagnostics)
    {
        var concatSpans = diagnostics
            .Where(d => d.Code == FormulaErrorCode.TypeMismatch && d.Message.Contains("'&' requires text"))
            .Select(d => d.Span)
            .ToHashSet();

        // Calls where a parameter was handed something of the wrong type — Quickbase coerces here too
        // (URLEncode([Record ID#]) is the common one), so the fix is the same wrap.
        var argWraps = new Dictionary<(TextSpan Span, int Index), Wrap>();
        var equalityFixes = new Dictionary<TextSpan, (string Left, string Right)>();
        var ifCalls = new HashSet<TextSpan>();
        var orderingFixes = new Dictionary<TextSpan, (string Left, string Right)>();
        var minMaxCalls = new HashSet<TextSpan>();
        foreach (var d in diagnostics)
        {
            if (d.Code == FormulaErrorCode.TypeMismatch)
            {
                var m = TextArgMismatch.Match(d.Message);
                if (m.Success) { argWraps[(d.Span, int.Parse(m.Groups["index"].Value) - 1)] = Wrap.ToText; continue; }
                m = UserArgMismatch.Match(d.Message);
                if (m.Success) { argWraps[(d.Span, int.Parse(m.Groups["index"].Value) - 1)] = Wrap.ToUser; continue; }
                m = EqualityMismatch.Match(d.Message);
                if (m.Success) { equalityFixes[d.Span] = (m.Groups["l"].Value, m.Groups["r"].Value); continue; }
                m = OrderingMismatch.Match(d.Message);
                if (m.Success) { orderingFixes[d.Span] = (m.Groups["l"].Value, m.Groups["r"].Value); continue; }
                m = MinMaxNonNumber.Match(d.Message);
                if (m.Success) { minMaxCalls.Add(d.Span); continue; }
            }
            if (d.Code == FormulaErrorCode.WrongArgumentCount && IfArity.IsMatch(d.Message))
                ifCalls.Add(d.Span);
        }

        if (concatSpans.Count == 0 && argWraps.Count == 0 && equalityFixes.Count == 0 && ifCalls.Count == 0 && orderingFixes.Count == 0 && minMaxCalls.Count == 0)
            return null;

        var parsed = Parser.Parse(expression);
        if (parsed.HasErrors)
            return null; // can't trust spans from a tree that didn't parse

        // Only the operands/arguments the checker actually complained about — leaving everything
        // else alone keeps the stored formula close to what the author wrote.
        var edits = new List<(TextSpan Span, string Text)>();
        var flags = new Flags(concatSpans, argWraps, equalityFixes, ifCalls, orderingFixes, minMaxCalls);
        Collect(parsed.Root, expression, flags, edits);
        if (edits.Count == 0)
            return null;

        // Right-to-left so each edit leaves earlier offsets untouched.
        var rewritten = expression;
        foreach (var (span, text) in edits.OrderByDescending(e => e.Span.Start))
            rewritten = rewritten[..span.Start] + text + rewritten[span.End..];

        return rewritten;
    }

    private sealed record Flags(
        HashSet<TextSpan> Concats,
        Dictionary<(TextSpan Span, int Index), Wrap> Args,
        Dictionary<TextSpan, (string Left, string Right)> Equality,
        HashSet<TextSpan> Ifs,
        Dictionary<TextSpan, (string Left, string Right)> Ordering,
        HashSet<TextSpan> MinMax);

    private static void Collect(Expr? node, string source, Flags flags, List<(TextSpan Span, string Text)> edits)
    {
        var flaggedConcats = flags.Concats;
        var flaggedArgs = flags.Args;
        var flaggedEquality = flags.Equality;
        var flaggedIfs = flags.Ifs;
        switch (node)
        {
            case null:
                return;

            case BinaryExpr b:
                if (b.Op == BinaryOp.Concat && flaggedConcats.Contains(b.Span))
                {
                    // Which side is the non-text one is only knowable from the bound tree, which
                    // the engine keeps internal — so wrap both. ToText of text is the identity, so
                    // the extra call costs nothing beyond a little verbosity, and wrapping is
                    // skipped where the author already did it.
                    AddWrap(b.Left, source, "ToText", edits);
                    AddWrap(b.Right, source, "ToText", edits);
                    return; // operands are being wrapped wholesale; don't also rewrite inside them
                }
                if (b.Op is BinaryOp.Equal or BinaryOp.NotEqual && flaggedEquality.TryGetValue(b.Span, out var types))
                {
                    // Quickbase compares text with another type as text: wrap the non-text side.
                    if (types.Left == "Text" && TextCoercible.Contains(types.Right)) { AddWrap(b.Right, source, "ToText", edits); return; }
                    if (types.Right == "Text" && TextCoercible.Contains(types.Left)) { AddWrap(b.Left, source, "ToText", edits); return; }
                }
                if (b.Op is BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual && flags.Ordering.TryGetValue(b.Span, out var ordering))
                {
                    // Text against a number: Quickbase reads the text as a number.
                    if (ordering.Left == "Text" && ordering.Right == "Number") { AddWrap(b.Left, source, "ToNumber", edits); return; }
                    if (ordering.Right == "Text" && ordering.Left == "Number") { AddWrap(b.Right, source, "ToNumber", edits); return; }
                }
                Collect(b.Left, source, flags, edits);
                Collect(b.Right, source, flags, edits);
                return;

            case LetExpr l:
                // A formula that starts with "var ... ;" is a LetExpr: everything below it needs the same fixes.
                foreach (var declaration in l.Declarations)
                    Collect(declaration.Value, source, flags, edits);
                Collect(l.Body, source, flags, edits);
                return;

            case UnaryExpr u:
                Collect(u.Operand, source, flags, edits);
                return;

            case FunctionCallExpr f:
                if (string.Equals(f.Name, "If", StringComparison.OrdinalIgnoreCase) && flaggedIfs.Contains(f.Span) && f.Args.Count >= 4)
                {
                    // If(c1, v1, c2, v2, …, else) → If(c1, v1, If(c2, v2, …, else)). Nested calls are rewritten too.
                    edits.Add((f.Span, NestIf(f, source, 0)));
                    return;
                }
                if ((string.Equals(f.Name, "Min", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Name, "Max", StringComparison.OrdinalIgnoreCase))
                    && flags.MinMax.Contains(f.Span) && f.Args.Count >= 2)
                {
                    // Min/Max of dates becomes a comparison, which PowerBase accepts on dates.
                    edits.Add((f.Span, MinMaxAsIf(f, source, string.Equals(f.Name, "Max", StringComparison.OrdinalIgnoreCase) ? ">" : "<")));
                    return;
                }
                for (var i = 0; i < f.Args.Count; i++)
                {
                    // Wrap only the argument position the checker named — other positions may
                    // legitimately want a non-text type.
                    if (flaggedArgs.TryGetValue((f.Span, i), out var wrap))
                    {
                        AddWrap(f.Args[i], source, wrap == Wrap.ToUser ? "ToUser" : "ToText", edits);
                        continue;
                    }
                    Collect(f.Args[i], source, flags, edits);
                }
                return;

            default:
                return; // literals / field refs / error nodes have nothing to descend into
        }
    }

    /// <summary>Max(a, b, c) becomes If(If(a > b, a, b) > c, If(a > b, a, b), c).</summary>
    private static string MinMaxAsIf(FunctionCallExpr call, string source, string op)
    {
        string Text(int i) => source[call.Args[i].Span.Start..call.Args[i].Span.End];
        var current = Text(0);
        for (var i = 1; i < call.Args.Count; i++)
        {
            var next = Text(i);
            current = $"If({current} {op} {next}, {current}, {next})";
        }
        return current;
    }

    /// <summary>Quickbase allows "var" declarations inside a branch of an If (or any call argument); PowerBase only at the top.
    /// Moves every nested declaration to the front of the formula, in order. Returns null when there is nothing to move.</summary>
    public static string? HoistNestedVariables(string expression)
    {
        var (tokens, diagnostics) = new Lexer(expression).Tokenize();
        if (diagnostics.Count > 0) return null;

        var removed = new List<(int Start, int End)>();
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.LParen) depth++;
            else if (t.Kind == TokenKind.RParen) depth--;
            else if (t.Kind == TokenKind.Var && depth > 0)
            {
                var declarationDepth = depth;
                var d = depth;
                var j = i + 1;
                for (; j < tokens.Count; j++)
                {
                    if (tokens[j].Kind == TokenKind.LParen) d++;
                    else if (tokens[j].Kind == TokenKind.RParen) d--;
                    else if (tokens[j].Kind == TokenKind.Semicolon && d == declarationDepth) break;
                    if (d < declarationDepth) return null; // unbalanced
                }
                if (j >= tokens.Count) return null;
                removed.Add((t.Span.Start, tokens[j].Span.Start + tokens[j].Span.Length));
                i = j;
            }
        }
        if (removed.Count == 0) return null;

        var hoisted = string.Join("\n", removed.Select(r => expression[r.Start..r.End]));
        var rest = expression;
        foreach (var (start, end) in removed.OrderByDescending(r => r.Start))
            rest = rest[..start] + rest[end..];
        return hoisted + "\n" + rest;
    }

    private static string NestIf(FunctionCallExpr call, string source, int from)
    {
        string Text(int i) => source[call.Args[i].Span.Start..call.Args[i].Span.End];
        var remaining = call.Args.Count - from;
        if (remaining == 1) return Text(from);
        if (remaining == 2) return $"If({Text(from)}, {Text(from + 1)})";
        return $"If({Text(from)}, {Text(from + 1)}, {NestIf(call, source, from + 2)})";
    }

    private static void AddWrap(Expr operand, string source, string function, List<(TextSpan Span, string Text)> edits)
    {
        if (operand is FunctionCallExpr { Name: var name } && string.Equals(name, function, StringComparison.OrdinalIgnoreCase))
            return;

        edits.Add((operand.Span, $"{function}({source[operand.Span.Start..operand.Span.End]})"));
    }
}
