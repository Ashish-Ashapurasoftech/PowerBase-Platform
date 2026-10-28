using System.Text.RegularExpressions;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Lexing;
using PowerBase.Formula.Syntax;
using PowerBase.Formula.Types;

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

    /// <summary>"Operator 'and' requires boolean operands (got Bool and Number)": Quickbase reads a number as true when it is not zero.</summary>
    private static readonly Regex LogicalMismatch =
        new(@"^Operator '(?<op>and|or)' requires boolean operands \(got (?<l>\w+) and (?<r>\w+)\)\.$", RegexOptions.Compiled);

    private static readonly Regex IfArity =
        new(@"^If expects 2[–-]3 argument\(s\) but got (?<n>\d+)\.$", RegexOptions.Compiled);

    private static readonly HashSet<string> TextCoercible = new(StringComparer.Ordinal)
        { "Number", "Date", "DateTime", "Time", "Duration", "Bool" };

    /// <summary>"If branches must have the same type (got Text and User)": Quickbase converts the branch to the other's type.</summary>
    private static readonly Regex IfBranchMismatch =
        new(@"^If branches must have the same type \(got (?<a>\w+) and (?<b>\w+)\)\.$", RegexOptions.Compiled);

    /// <summary>"Formula returns Text but User is required.": the whole result has to be converted.</summary>
    private static readonly Regex ResultMismatch =
        new(@"^Formula returns (?<got>\w+) but (?<want>\w+) is required\.$", RegexOptions.Compiled);

    /// <summary>The conversion that makes a result of one type acceptable where another is required, or null when there is none.</summary>
    private static string? ResultConversion(string got, string want) => (got, want) switch
    {
        ("Text", "User") => "ToUser",
        ("Text", "Number") => "ToNumber",
        ("Number", "Text") => "ToText",
        ("Number", "Duration") => "Hours", // a Duration formula over a number: the number is hours, as the formulas that read it use ToHours
        ("Bool", "Number") => "ToNumber",
        _ => null,
    };

    /// <summary>"Expecting number but found text.": a typed <c>var</c> given another type — Quickbase converts the value on the way in.</summary>
    private static readonly Regex VariableMismatch =
        new(@"^Expecting (?<want>[\w-]+) but found (?<got>[\w -]+)\.$", RegexOptions.Compiled);

    private static readonly Regex SplitArity =
        new(@"^Split expects 2 argument\(s\) but got 1\.$", RegexOptions.Compiled);

    /// <summary>The conversion that lets a value of <paramref name="got"/> initialise a variable declared <paramref name="want"/> (the keywords an author writes).</summary>
    private static string? VariableConversion(string got, string want) => (got, want) switch
    {
        ("text", "number") => "ToNumber",
        ("number", "text") => "ToText",
        ("bool", "number") => "ToNumber",
        ("text", "user") => "ToUser",
        _ => null,
    };

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
        var logicalFixes = new Dictionary<TextSpan, (string Left, string Right)>();
        var ifBranchFixes = new Dictionary<TextSpan, (string Then, string Else)>();
        string? resultConversion = null;
        var variableFixes = new Dictionary<TextSpan, string>();
        var splitCalls = new HashSet<TextSpan>();
        foreach (var d in diagnostics)
        {
            if (d.Code == FormulaErrorCode.VariableTypeMismatch)
            {
                var vm = VariableMismatch.Match(d.Message);
                if (vm.Success && VariableConversion(vm.Groups["got"].Value, vm.Groups["want"].Value) is { } variableConversion)
                    variableFixes[d.Span] = variableConversion;
                continue;
            }
            if (d.Code == FormulaErrorCode.WrongArgumentCount && SplitArity.IsMatch(d.Message))
            {
                splitCalls.Add(d.Span);
                continue;
            }
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
                m = LogicalMismatch.Match(d.Message);
                if (m.Success) { logicalFixes[d.Span] = (m.Groups["l"].Value, m.Groups["r"].Value); continue; }
                m = IfBranchMismatch.Match(d.Message);
                if (m.Success) { ifBranchFixes[d.Span] = (m.Groups["a"].Value, m.Groups["b"].Value); continue; }
            }
            if (d.Code == FormulaErrorCode.ResultTypeMismatch)
            {
                var m = ResultMismatch.Match(d.Message);
                if (m.Success && ResultConversion(m.Groups["got"].Value, m.Groups["want"].Value) is { } conversion)
                    resultConversion = conversion;
            }
            if (d.Code == FormulaErrorCode.WrongArgumentCount && IfArity.IsMatch(d.Message))
                ifCalls.Add(d.Span);
        }

        if (concatSpans.Count == 0 && argWraps.Count == 0 && equalityFixes.Count == 0 && ifCalls.Count == 0 && orderingFixes.Count == 0 && minMaxCalls.Count == 0 && logicalFixes.Count == 0 && ifBranchFixes.Count == 0 && variableFixes.Count == 0 && splitCalls.Count == 0 && resultConversion is null)
            return null;

        var parsed = Parser.Parse(expression);
        if (parsed.HasErrors)
            return null; // can't trust spans from a tree that didn't parse

        // Only the operands/arguments the checker actually complained about — leaving everything
        // else alone keeps the stored formula close to what the author wrote.
        var edits = new List<(TextSpan Span, string Text)>();
        var flags = new Flags(concatSpans, argWraps, equalityFixes, ifCalls, orderingFixes, minMaxCalls, logicalFixes, ifBranchFixes, variableFixes, splitCalls);
        Collect(parsed.Root, expression, flags, edits);
        if (edits.Count == 0 && resultConversion is not null)
        {
            // Only once the inner fixes are done (the edit would overlap them): convert the value the whole formula returns.
            var result = parsed.Root is LetExpr let ? let.Body : parsed.Root;
            AddWrap(result, expression, resultConversion, edits);
        }
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
        HashSet<TextSpan> MinMax,
        Dictionary<TextSpan, (string Left, string Right)> Logical,
        Dictionary<TextSpan, (string Then, string Else)> IfBranches,
        Dictionary<TextSpan, string> Variables,
        HashSet<TextSpan> Splits);

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
                    // A long chain a & b & c & … nests to the left: an operand that is itself a concatenation is rewritten on its own
                    // (wrapping it wholesale would leave the non-text operands inside it unfixed, one level per pass).
                    foreach (var operand in new[] { b.Left, b.Right })
                    {
                        if (operand is BinaryExpr { Op: BinaryOp.Concat })
                            Collect(operand, source, flags, edits);
                        else
                            AddWrap(operand, source, "ToText", edits);
                    }
                    return;
                }
                if (b.Op is BinaryOp.Equal or BinaryOp.NotEqual && flaggedEquality.TryGetValue(b.Span, out var types))
                {
                    // Quickbase compares text with another type as text: wrap the non-text side.
                    if (types.Left == "Text" && TextCoercible.Contains(types.Right)) { AddWrap(b.Right, source, "ToText", edits); return; }
                    if (types.Right == "Text" && TextCoercible.Contains(types.Left)) { AddWrap(b.Left, source, "ToText", edits); return; }
                    // Text against a user: PowerBase does not compare users, so the user is compared by the email the text is expected to hold.
                    if (types.Left == "Text" && types.Right == "User") { AddWrap(b.Right, source, "UserToEmail", edits); return; }
                    if (types.Left == "User" && types.Right == "Text") { AddWrap(b.Left, source, "UserToEmail", edits); return; }
                    // A checkbox against a number: Quickbase counts true as 1 and false as 0.
                    if (types.Left == "Number" && types.Right == "Bool") { AddWrap(b.Right, source, "ToNumber", edits); return; }
                    if (types.Left == "Bool" && types.Right == "Number") { AddWrap(b.Left, source, "ToNumber", edits); return; }
                }
                if (b.Op is BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual && flags.Ordering.TryGetValue(b.Span, out var ordering))
                {
                    // Text against a number: Quickbase reads the text as a number.
                    if (ordering.Left == "Text" && ordering.Right == "Number") { AddWrap(b.Left, source, "ToNumber", edits); return; }
                    if (ordering.Right == "Text" && ordering.Left == "Number") { AddWrap(b.Right, source, "ToNumber", edits); return; }
                    if (ordering.Left == "Number" && ordering.Right == "Bool") { AddWrap(b.Right, source, "ToNumber", edits); return; }
                    if (ordering.Left == "Bool" && ordering.Right == "Number") { AddWrap(b.Left, source, "ToNumber", edits); return; }
                }
                if (b.Op is BinaryOp.And or BinaryOp.Or && flags.Logical.TryGetValue(b.Span, out var logical))
                {
                    // A number in and / or: Quickbase reads it as true when it is not zero.
                    if (logical.Left == "Number") AddWrap(b.Left, source, "ToBool", edits);
                    if (logical.Right == "Number") AddWrap(b.Right, source, "ToBool", edits);
                    if (logical.Left == "Number" || logical.Right == "Number") return;
                }
                Collect(b.Left, source, flags, edits);
                Collect(b.Right, source, flags, edits);
                return;

            case LetExpr l:
                // A formula that starts with "var ... ;" is a LetExpr: everything below it needs the same fixes.
                foreach (var declaration in l.Declarations)
                {
                    if (flags.Variables.TryGetValue(declaration.Value.Span, out var conversion))
                        AddWrap(declaration.Value, source, conversion, edits);
                    else
                        Collect(declaration.Value, source, flags, edits);
                }
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
                if (string.Equals(f.Name, "Split", StringComparison.OrdinalIgnoreCase) && f.Args.Count == 1 && flags.Splits.Contains(f.Span))
                {
                    // Quickbase's one-argument Split(List(" ; ", …)) splits on the delimiter the list was built with.
                    var delimiter = f.Args[0] is FunctionCallExpr { Args.Count: > 0 } list
                        && string.Equals(list.Name, "List", StringComparison.OrdinalIgnoreCase) && list.Args[0] is LiteralExpr { Type: FormulaType.Text }
                        ? source[list.Args[0].Span.Start..list.Args[0].Span.End]
                        : "\";\"";
                    edits.Add((f.Span, $"Split({source[f.Args[0].Span.Start..f.Args[0].Span.End]}, {delimiter})"));
                    return;
                }
                var wrappedBranch = -1;
                if (string.Equals(f.Name, "If", StringComparison.OrdinalIgnoreCase) && f.Args.Count == 3 && flags.IfBranches.TryGetValue(f.Span, out var branches))
                {
                    // A text branch next to a user branch becomes a user; any other mix is compared as text.
                    string? function = null;
                    if (branches.Then == "Text" && branches.Else == "User") { wrappedBranch = 1; function = "ToUser"; }
                    else if (branches.Then == "User" && branches.Else == "Text") { wrappedBranch = 2; function = "ToUser"; }
                    else if (branches.Else == "Text" && TextCoercible.Contains(branches.Then)) { wrappedBranch = 1; function = "ToText"; }
                    else if (branches.Then == "Text" && TextCoercible.Contains(branches.Else)) { wrappedBranch = 2; function = "ToText"; }
                    if (function is not null) AddWrap(f.Args[wrappedBranch], source, function, edits);
                }
                for (var i = 0; i < f.Args.Count; i++)
                {
                    if (i == wrappedBranch) continue;
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
