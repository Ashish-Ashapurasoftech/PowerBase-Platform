using System.Text.RegularExpressions;
using PowerBase.Formula.Diagnostics;
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
/// </summary>
internal static class QuickbaseDialectRewriter
{
    /// <summary>Attempts to rewrite <paramref name="expression"/> to fix the dialect differences
    /// implied by <paramref name="diagnostics"/>. Returns null when nothing was rewritten, so the
    /// caller can keep the original diagnostics rather than reporting a pointless retry.</summary>
    /// <summary>"Fn argument 2 expects Text but got Number." — the checker reports these against
    /// the whole call's span, so the index has to come from the message to know which argument to
    /// wrap. Anchored and narrow so an unrelated message can't be misread as one of these.</summary>
    private static readonly Regex TextArgMismatch =
        new(@"^(?<fn>\w+) argument (?<index>\d+) expects Text but got \w+\.$", RegexOptions.Compiled);

    public static string? TryRewrite(string expression, IReadOnlyList<FormulaDiagnostic> diagnostics)
    {
        var concatSpans = diagnostics
            .Where(d => d.Code == FormulaErrorCode.TypeMismatch && d.Message.Contains("'&' requires text"))
            .Select(d => d.Span)
            .ToHashSet();

        // Calls where a Text parameter was handed something else — Quickbase coerces here too
        // (URLEncode([Record ID#]) is the common one), so the fix is the same ToText() wrap.
        var textArgs = new HashSet<(TextSpan Span, int Index)>();
        foreach (var d in diagnostics.Where(d => d.Code == FormulaErrorCode.TypeMismatch))
        {
            var m = TextArgMismatch.Match(d.Message);
            if (m.Success)
                textArgs.Add((d.Span, int.Parse(m.Groups["index"].Value) - 1)); // message is 1-based
        }

        if (concatSpans.Count == 0 && textArgs.Count == 0)
            return null;

        var parsed = Parser.Parse(expression);
        if (parsed.HasErrors)
            return null; // can't trust spans from a tree that didn't parse

        // Only the operands/arguments the checker actually complained about — leaving everything
        // else alone keeps the stored formula close to what the author wrote.
        var operands = new List<TextSpan>();
        Collect(parsed.Root, concatSpans, textArgs, operands);
        if (operands.Count == 0)
            return null;

        // Right-to-left so each edit leaves earlier offsets untouched.
        var rewritten = expression;
        foreach (var span in operands.OrderByDescending(s => s.Start))
        {
            var inner = rewritten[span.Start..span.End];
            rewritten = rewritten[..span.Start] + $"ToText({inner})" + rewritten[span.End..];
        }

        return rewritten;
    }

    private static void Collect(
        Expr? node,
        HashSet<TextSpan> flaggedConcats,
        HashSet<(TextSpan Span, int Index)> flaggedTextArgs,
        List<TextSpan> operands)
    {
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
                    AddIfNotAlreadyToText(b.Left, operands);
                    AddIfNotAlreadyToText(b.Right, operands);
                    return; // operands are being wrapped wholesale; don't also rewrite inside them
                }
                Collect(b.Left, flaggedConcats, flaggedTextArgs, operands);
                Collect(b.Right, flaggedConcats, flaggedTextArgs, operands);
                return;

            case UnaryExpr u:
                Collect(u.Operand, flaggedConcats, flaggedTextArgs, operands);
                return;

            case FunctionCallExpr f:
                for (var i = 0; i < f.Args.Count; i++)
                {
                    // Wrap only the argument position the checker named — other positions may
                    // legitimately want a non-text type.
                    if (flaggedTextArgs.Contains((f.Span, i)))
                    {
                        AddIfNotAlreadyToText(f.Args[i], operands);
                        continue;
                    }
                    Collect(f.Args[i], flaggedConcats, flaggedTextArgs, operands);
                }
                return;

            default:
                return; // literals / field refs / error nodes have nothing to descend into
        }
    }

    private static void AddIfNotAlreadyToText(Expr operand, List<TextSpan> operands)
    {
        if (operand is FunctionCallExpr { Name: var name } && string.Equals(name, "ToText", StringComparison.OrdinalIgnoreCase))
            return;

        operands.Add(operand.Span);
    }
}
