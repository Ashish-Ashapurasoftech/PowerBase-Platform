using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Pipelines;
using PowerBase.Formula;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// powerflowextraRd: turns a Quickbase Pipelines value that uses Jinja expressions ({{time.now|date_mdy}}, {{a.name|upper}},
/// {{a.amount * 2}}, {{CLEAR}} …) into a PowerFlow formula value — the same formula language, and the same
/// <see cref="FormulaEngine"/>, as a table's formula fields, as the app import does for Quickbase formula fields
/// (<see cref="FormulaTranslation.FormulaTranslator"/>). Only what our formula language has an equivalent for is translated; the
/// result is compiled with the formula engine before it is used, and anything else is reported, so the value is cleared and the
/// step left as a draft exactly as before.
///
/// Translated (Jinja → formula):
/// <list type="bullet">
/// <item>references: <c>a.field</c>, <c>a.$prev.field</c>, <c>metadata.a.…</c> → the step reference <c>[{{steps.ref_a.fid_N}}]</c> (read as text)</item>
/// <item>literals: <c>'text'</c>, numbers, <c>true</c>/<c>false</c>; <c>CLEAR</c> → <c>""</c> (no value, which empties the field)</item>
/// <item><c>time.now</c> → <c>Now()</c> (UTC); <c>time.today</c> → <c>ToDate(Now())</c> (today in UTC, as Quickbase)</item>
/// <item><c>time.delta(days|hours|minutes|seconds|weeks=n)</c> → <c>Days(n)</c> …; <c>months|years=n</c> added to a date → <c>AdjustMonth/AdjustYear</c></item>
/// <item><c>time.parse(x)</c>, <c>x.date()</c> → <c>ToDate(x)</c>; <c>(d1 - d2).days|seconds</c> → <c>ToDays/ToSeconds</c></item>
/// <item><c>x.strftime('%Y-%m-%d …')</c> → built from Year/Month/Day/Hour/Minute/Second/NameOfMonth/NameOfDay
/// (%Y %m %d %H %I %M %S %p %B %b %A %a %%)</item>
/// <item>operators <c>+ - * / // % **</c> and <c>~</c> (text join, <c>&amp;</c>); <c>== != &lt; &gt; &lt;= &gt;=</c>; <c>and or not</c>;
/// <c>a if c else b</c> → <c>If</c>; <c>in</c> / <c>not in</c> → <c>Contains</c>; tests <c>is [not] none|defined|odd|even|divisibleby|eq|ne|lt|le|gt|ge|in|true|false</c></item>
/// <item>text: <c>x[i]</c>, <c>x[a:b]</c> → Mid/Left/Right/NotLeft/NotRight; <c>.upper() .lower() .strip() .capitalize() .startswith() .endswith()
/// .replace() .find() .count() .split()</c> (with <c>[i]</c> → Part, <c>|join</c> → Join, <c>|length</c> → Count, <c>in</c>)</item>
/// <item>dates: <c>.year .month .day .hour .minute .second</c>, <c>.weekday() .isoweekday()</c>, strftime <c>%y %j %w</c> too</item>
/// <item>more filters: <c>capitalize</c>, <c>base64_encode|base64_decode</c>, <c>text</c>, <c>join</c>, <c>round(n, 'floor'|'ceil')</c>, <c>format(…)</c></item>
/// <item>filters: <c>date_mdy|date_ymd|date_dmy(sep)</c>, <c>upper</c>, <c>lower</c>, <c>trim</c>, <c>replace(a, b)</c>,
/// <c>length|count</c>, <c>int</c>, <c>float</c>, <c>round(n)</c>, <c>abs</c>, <c>default(v)</c>, <c>string</c>,
/// <c>urlencode</c>, <c>striptags|html2text</c>, <c>first</c>, <c>last</c></item>
/// </list>
/// Not translated (no equivalent, so reported): Jinja statements (<c>{% … %}</c>), other filters (<c>timezone</c>, <c>title</c>,
/// <c>truncate</c>, <c>wordcount</c>, hashes, list filters …), type tests, <c>runtime.*</c>, indexes that are not whole-number literals.
/// </summary>
internal static class QblJinjaFormula
{
    /// <summary>Resolves a step reference (<c>a.name</c>) to the parts of its PowerFlows token, or returns why it cannot.</summary>
    /// <para><paramref name="kind"/>: "bool" for a checkbox, "date" / "datetime" for a date or date/time field, null otherwise.</para>
    internal delegate string? ResolvePath(string path, out List<object>? parts, out string? kind);

    internal sealed record Result(PblPipelineTextValue? Value, string? Problem);

    private static readonly FormulaEngine Engine = new();
    // powerflowextraRd: {{- and -}} are Jinja's whitespace control (drop the spaces before / after), not a minus sign.
    private static readonly Regex Token = new(@"\{\{(-?)(.*?)(-?)\}\}|\{%.*?%\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex PlainPath = new(@"^\s*[A-Za-z][A-Za-z0-9_]*(?:\.\$?[A-Za-z0-9_]+)+\s*$", RegexOptions.Compiled);

    /// <summary>Null when the value holds only plain references (or Jinja statements): those are handled as before.</summary>
    public static Result? Translate(string text, ResolvePath resolve)
    {
        var tokens = Token.Matches(text);
        if (tokens.Count == 0 || tokens.Any(t => !t.Value.StartsWith("{{", StringComparison.Ordinal))) return null;
        // time.now.year is a date part, not a step reference.
        // a.start.year is the field a.start, then its year, when a.start.year is no field of its own.
        if (tokens.All(t => PlainPath.IsMatch(t.Groups[2].Value) && t.Groups[1].Length == 0 && t.Groups[3].Length == 0
                && !t.Groups[2].Value.Trim().StartsWith("time.", StringComparison.Ordinal)
                && !IsDatePartOfAField(t.Groups[2].Value.Trim(), resolve)
                // powerflowextraRd: a checkbox or date field written inside a text prints as Quickbase prints it (True, 2025-01-05 00:00:00+00:00);
                // a value that is that field alone keeps its type for the field it is written to.
                && (tokens.Count == 1 && t.Length == text.Length || resolve(t.Groups[2].Value.Trim(), out _, out var kind) is not null || kind is null or "number"))) return null;

        var refs = new List<List<object>>();
        var pieces = new List<string>();
        var last = 0;
        var trimNext = false;
        try
        {
            foreach (Match token in tokens)
            {
                var literal = text[last..token.Index];
                if (trimNext) literal = literal.TrimStart();
                if (token.Groups[1].Length > 0) literal = literal.TrimEnd();
                if (literal.Length > 0) pieces.Add(Str(literal));
                last = token.Index + token.Length;
                trimNext = token.Groups[3].Length > 0;
                var node = new Parser(token.Groups[2].Value, resolve, refs).ParseAll();
                // A value that is one whole token keeps its type (a boolean or date for such a field), but a Python float prints with a point.
                var whole = tokens.Count == 1 && token.Index == 0 && token.Length == text.Length;
                pieces.Add(whole && !(node.Type == T.Number && node.Float) && node.Py is null ? node.Code : AsText(node).Code);
            }
            var rest = text[last..];
            if (trimNext) rest = rest.TrimStart();
            if (rest.Length > 0) pieces.Add(Str(rest));
            if (pieces.Count == 0) pieces.Add("\"\"");
        }
        catch (JinjaNotSupported ex)
        {
            return new Result(null, ex.Message);
        }

        var expression = string.Join(" & ", pieces);

        // Checked as every formula is: with the formula engine, each reference a text field.
        var check = Regex.Replace(expression, @"\u0001(\d+)\u0002", m => $"[{{{{f{m.Groups[1].Value}}}}}]");
        var definition = new PipelineFormulaValue.Definition(check,
            refs.Select((_, i) => new PipelineFormulaValue.Field($"{{{{f{i}}}}}", null)).ToList());
        var (compiled, _, diagnostics) = PipelineFormulaValue.Compile(definition, Engine);
        if (compiled is null)
            return new Result(null, $"the formula it would become could not be checked ({string.Join("; ", diagnostics.Select(d => d.Message))})");

        var parts = new List<object> { PipelineFormulaValue.Prefix + "{\"expression\":\"" };
        var at = 0;
        foreach (Match marker in Regex.Matches(expression, @"\u0001(\d+)\u0002"))
        {
            parts.Add(Json(expression[at..marker.Index]) + "[");
            parts.AddRange(refs[int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture)]);
            parts.Add("]");
            at = marker.Index + marker.Length;
        }
        parts.Add(Json(expression[at..]) + "\",\"fields\":[");
        for (var i = 0; i < refs.Count; i++)
        {
            parts.Add((i > 0 ? "," : "") + "{\"token\":\"");
            parts.AddRange(refs[i]);
            parts.Add("\",\"type\":null}");
        }
        parts.Add("]}");
        return new Result(new PblPipelineTextValue(parts), null);
    }

    private static bool IsDatePartOfAField(string path, ResolvePath resolve)
    {
        var dot = path.LastIndexOf('.');
        return dot > 0 && path.Count(c => c == '.') >= 2
            && Parser.IsDatePart(path[(dot + 1)..])
            && resolve(path, out _, out _) is not null
            && resolve(path[..dot], out _, out _) is null;
    }

    private static string Json(string s) => JsonEncodedText.Encode(s).ToString();

    /// <summary>A formula text literal (the lexer reads a backslash as "the next character as is").</summary>
    private static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private sealed class JinjaNotSupported(string message) : Exception(message);

    private enum T { Any, Text, Number, Bool, Date, DateTime, Duration, Months, Years, List }

    /// <summary>A translated piece. A list made by <c>.split(sep)</c> keeps its text and separator so <c>[i]</c> can read one part.</summary>
    /// <para><see cref="Float"/>: a number Python holds as a float (a literal with a point, <c>|float</c>, <c>|round</c>, <c>/</c>, or
    /// arithmetic with one of those), which Jinja prints with a point: <c>10 / 5</c> → <c>2.0</c>, <c>2.5|round</c> → <c>3.0</c>.</para>
    /// <para><see cref="Py"/>: how Python prints a date value Quickbase made — "aware" (time.now, time.today: a UTC datetime,
    /// <c>2026-11-13 05:47:54+00:00</c>), "naive" (time.parse: <c>2025-03-01 00:00:00</c>) or "date" (.date(): <c>2025-03-01</c>).</para>
    /// <para><see cref="FloatWhen"/>: a condition that is true at run time when a number field's value is a float in Quickbase (12.5, not 10),
    /// so arithmetic with it prints with a point as Python does (12.5 * 2 → 25.0).</para>
    private sealed record Node(string Code, T Type, string? SplitText = null, string? SplitSep = null, bool Float = false, string? Py = null, string? FloatWhen = null);

    private static bool DateLike(Node n) => n.Type is T.Date or T.DateTime;

    private static Node Num(string code, params Node[] from) => new(code, T.Number, Float: from.Any(n => n.Float),
        FloatWhen: from.Where(n => n.FloatWhen is not null).Select(n => n.FloatWhen!).ToList() is { Count: > 0 } w ? "(" + string.Join(" or ", w) + ")" : null);

    private static Node AsText(Node n) => n.Type switch
    {
        T.Text => n,
        // powerflowextraRd: Python prints a whole float with ".0".
        T.Number when n.Float => new Node($"If(Int({n.Code}) = {n.Code}, ToText({n.Code}) & \".0\", ToText({n.Code}))", T.Text),
        T.Number when n.FloatWhen is not null => new Node($"If({n.FloatWhen} and Int({n.Code}) = {n.Code}, ToText({n.Code}) & \".0\", ToText({n.Code}))", T.Text),
        // Jinja prints a boolean as True / False.
        T.Bool => new Node($"If({n.Code}, \"True\", \"False\")", T.Text),
        // powerflowextraRd: a date Quickbase made prints as Python prints it (checked in a real Quickbase pipeline; the fraction of a second is left out).
        T.Date or T.DateTime when n.Py is not null => new Node(PythonDateText(n), T.Text),
        // A Quickbase date or date/time field: nothing when empty, else a UTC datetime (seconds; the fraction is left out).
        T.Any when n.Py == "field" => new Node(
            $"If(Length(ToText({n.Code})) = 0, \"\", {PythonDateText(new Node($"ToTimestamp({n.Code})", T.DateTime, Py: "aware"))})", T.Text),
        T.Duration or T.Months or T.Years => throw new JinjaNotSupported("a time span cannot be written as text"),
        T.List => throw new JinjaNotSupported("a list cannot be written as text without |join"),
        _ => new Node($"ToText({n.Code})", T.Text),
    };

    private static string PythonDateText(Node n)
    {
        var d = n.Code;
        var date = $"ToText(Year({d})) & \"-\" & {Pad($"Month({d})")} & \"-\" & {Pad($"Day({d})")}";
        if (n.Py == "date") return $"({date})";
        var t = n.Type == T.DateTime ? d : $"ToTimestamp({d})";
        var time = $"{Pad($"Hour({t})")} & \":\" & {Pad($"Minute({t})")} & \":\" & {Pad($"Second({t})")}";
        return $"({date} & \" \" & {time}{(n.Py == "aware" ? " & \"+00:00\"" : "")})";
    }

    /// <summary>Jinja truthiness: a boolean as is, a number when not 0, a text (every reference) when not empty.</summary>
    private static Node AsBool(Node n) => n.Type switch
    {
        T.Bool => n,
        T.Number => new Node($"({n.Code} <> 0)", T.Bool),
        T.Text or T.Any => new Node($"(Length(ToText({n.Code})) > 0)", T.Bool),
        T.List => new Node($"(Count({n.Code}) > 0)", T.Bool),
        _ => new Node($"IsNotNull({n.Code})", T.Bool),
    };

    private static Node AsNumber(Node n) => n.Type switch
    {
        T.Number => n,
        T.Any or T.Text => new Node($"ToNumber({n.Code})", T.Number, FloatWhen: n.FloatWhen),
        _ => throw new JinjaNotSupported("a value that is not a number is used as one"),
    };

    private static Node AsDate(Node n) => DateLike(n) ? n
        : n.Type is T.Any or T.Text ? new Node($"ToDate({n.Code})", T.Date)
        : throw new JinjaNotSupported("a value that is not a date is used as one");

    /// <summary>AdjustMonth / AdjustYear take a date: a date and time is cut to its date, as Quickbase's month/year delta keeps the day.</summary>
    private static string DateOnly(Node n) => AsDate(n) is { Type: T.DateTime } dt ? $"ToDate({dt.Code})" : AsDate(n).Code;

    private static string Pad(string code) => $"Right(\"0\" & ToText({code}), 2)";

    private enum K { Num, Str, Name, Op, End }

    private sealed record Tok(K Kind, string Text);

    private sealed class Parser
    {
        private readonly List<Tok> _toks = [];
        private readonly ResolvePath _resolve;
        private readonly List<List<object>> _refs;
        private readonly string _source;
        private int _pos;

        public Parser(string source, ResolvePath resolve, List<List<object>> refs)
        {
            _source = source.Trim();
            _resolve = resolve;
            _refs = refs;
            Lex(_source);
        }

        private void Lex(string s)
        {
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (char.IsDigit(c))
                {
                    var j = i;
                    while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.' && j + 1 < s.Length && char.IsDigit(s[j + 1]))) j++;
                    _toks.Add(new(K.Num, s[i..j])); i = j; continue;
                }
                if (c is '\'' or '"')
                {
                    var sb = new StringBuilder();
                    var j = i + 1;
                    while (j < s.Length && s[j] != c)
                    {
                        if (s[j] == '\\' && j + 1 < s.Length) j++;
                        sb.Append(s[j]); j++;
                    }
                    if (j >= s.Length) throw new JinjaNotSupported($"'{_source}' has an unterminated text");
                    _toks.Add(new(K.Str, sb.ToString())); i = j + 1; continue;
                }
                if (char.IsLetter(c) || c is '_' or '$')
                {
                    var j = i + 1;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    _toks.Add(new(K.Name, s[i..j])); i = j; continue;
                }
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "//" or "**" or "==" or "!=" or "<=" or ">=") { _toks.Add(new(K.Op, two)); i += 2; continue; }
                _toks.Add(new(K.Op, c.ToString())); i++;
            }
            _toks.Add(new(K.End, ""));
        }

        private Tok Peek => _toks[_pos];
        private Tok Next() => _toks[_pos++];
        private bool IsOp(string op) => Peek.Kind == K.Op && Peek.Text == op;
        private void Expect(string op)
        {
            if (!IsOp(op)) throw new JinjaNotSupported($"'{_source}' is not an expression PowerFlows can write as a formula");
            _pos++;
        }

        public Node ParseAll()
        {
            var node = ParseExpr();
            if (Peek.Kind != K.End)
                throw new JinjaNotSupported($"'{{{{{_source}}}}}' uses '{Peek.Text}', which PowerFlows formulas have no equivalent for");
            return node;
        }

        private bool IsName(string name) => Peek.Kind == K.Name && Peek.Text == name;
        private bool IsNameAt(int offset, string name) => _toks[_pos + offset].Kind == K.Name && _toks[_pos + offset].Text == name;

        /// <summary><c>a if cond else b</c> → <c>If(cond, a, b)</c> (no <c>else</c>: an empty text, as Jinja prints).</summary>
        private Node ParseExpr()
        {
            var value = ParseOr();
            if (!IsName("if")) return value;
            _pos++;
            var cond = AsBool(ParseOr());
            Node other = new("\"\"", T.Text);
            if (IsName("else")) { _pos++; other = ParseExpr(); }
            if (value.Type == other.Type && value.Type is not T.Any)
                return new Node($"If({cond.Code}, {value.Code}, {other.Code})", value.Type);
            return new Node($"If({cond.Code}, {AsText(value).Code}, {AsText(other).Code})", T.Text);
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (IsName("or")) { _pos++; left = new Node($"({AsBool(left).Code} or {AsBool(ParseAnd()).Code})", T.Bool); }
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (IsName("and")) { _pos++; left = new Node($"({AsBool(left).Code} and {AsBool(ParseNot()).Code})", T.Bool); }
            return left;
        }

        private Node ParseNot()
        {
            if (IsName("not")) { _pos++; return new Node($"Not({AsBool(ParseNot()).Code})", T.Bool); }
            return ParseCompare();
        }

        private static readonly Dictionary<string, string> CompareOps = new()
        {
            ["=="] = "=", ["!="] = "<>", ["<"] = "<", [">"] = ">", ["<="] = "<=", [">="] = ">=",
        };

        private Node ParseCompare()
        {
            var left = ParseConcat();
            while (true)
            {
                if (Peek.Kind == K.Op && CompareOps.TryGetValue(Peek.Text, out var op))
                {
                    _pos++;
                    left = Compare(left, op, ParseConcat());
                    continue;
                }
                if (IsName("in") || IsName("not") && IsNameAt(1, "in"))
                {
                    var negate = IsName("not");
                    _pos += negate ? 2 : 1;
                    var inside = In(left, ParseConcat());
                    left = negate ? new Node($"Not({inside.Code})", T.Bool) : inside;
                    continue;
                }
                if (IsName("is"))
                {
                    _pos++;
                    var negate = IsName("not");
                    if (negate) _pos++;
                    var test = Test(left);
                    left = negate ? new Node($"Not({test.Code})", T.Bool) : test;
                    continue;
                }
                return left;
            }
        }

        /// <summary>Compared as numbers when a side is a number, as dates when a side is a date, else as texts (as a reference is text).</summary>
        private static Node Compare(Node l, string op, Node r)
        {
            // powerflowextraRd: an empty Quickbase field is None in Jinja (checked in a real Quickbase pipeline): it equals none
            // or another empty field, and nothing else — not even '' (a.email == '' is False there).
            if (op is "=" or "<>" && (l.Type == T.Any || r.Type == T.Any || IsNone(l) || IsNone(r)))
            {
                string Empty(Node n) => IsNone(n) ? "true" : n.Type == T.Any ? $"(Length(ToText({n.Code})) = 0)" : "false";
                var (le, re) = (Empty(l), Empty(r));
                var bothValues = IsNone(l) || IsNone(r) ? "false" : CompareValues(l, "=", r).Code;
                var equal = $"If({le} or {re}, ({le} and {re}), {bothValues})";
                return new Node(op == "=" ? $"({equal})" : $"Not({equal})", T.Bool);
            }
            return CompareValues(l, op, r);
        }

        private static bool IsNone(Node n) => n.Py == "none";

        private static Node CompareValues(Node l, string op, Node r)
        {
            if (l.Type == T.Number || r.Type == T.Number) return new Node($"({AsNumber(l).Code} {op} {AsNumber(r).Code})", T.Bool);
            if (DateLike(l) || DateLike(r)) return new Node($"({AsDate(l).Code} {op} {AsDate(r).Code})", T.Bool);
            if (l.Type == T.Bool && r.Type == T.Bool) return new Node($"({l.Code} {op} {r.Code})", T.Bool);
            if (l.Type == T.Bool || r.Type == T.Bool) return new Node($"({AsText(l).Code} {op} {AsText(r).Code})", T.Bool);
            return new Node($"({AsText(l).Code} {op} {AsText(r).Code})", T.Bool);
        }

        /// <summary><c>x in text</c> → <c>Contains(text, x)</c>; <c>x in a.split(',')</c> → one of the parts equals x.</summary>
        private static Node In(Node needle, Node haystack)
        {
            if (haystack.Type == T.List)
            {
                if (haystack.SplitText is null) throw new JinjaNotSupported("'in' on this list has no PowerFlows formula equivalent");
                var sep = haystack.SplitSep!;
                return new Node($"Contains({sep} & {haystack.SplitText} & {sep}, {sep} & {AsText(needle).Code} & {sep})", T.Bool);
            }
            return new Node($"Contains({AsText(haystack).Code}, {AsText(needle).Code})", T.Bool);
        }

        /// <summary>Jinja tests after <c>is</c>.</summary>
        private Node Test(Node x)
        {
            var name = Next();
            if (name.Kind != K.Name) throw new JinjaNotSupported($"'{_source}' has a test PowerFlows cannot read");
            Node Arg()
            {
                if (IsOp("(")) { var a = Arguments(); if (a.Positional.Count == 1 && a.Named.Count == 0) return a.Positional[0]; }
                else if (Peek.Kind is K.Num or K.Str or K.Name) return ParseConcat();
                throw new JinjaNotSupported($"the '{name.Text}' test needs one value");
            }
            return name.Text switch
            {
                // A checkbox is never None in Quickbase (an empty one is False).
                "none" => new Node(x.Type is T.Text or T.Any ? $"(Length(ToText({x.Code})) = 0)" : x.Type == T.Bool ? "false" : $"IsNull({x.Code})", T.Bool),
                // A step's field always exists, so it is defined even when empty.
                "defined" => new Node("true", T.Bool),
                "undefined" => new Node("false", T.Bool),
                "odd" => new Node($"(Mod({AsNumber(x).Code}, 2) = 1)", T.Bool),
                "even" => new Node($"(Mod({AsNumber(x).Code}, 2) = 0)", T.Bool),
                "divisibleby" => new Node($"(Mod({AsNumber(x).Code}, {AsNumber(Arg()).Code}) = 0)", T.Bool),
                "eq" or "equalto" or "sameas" => Compare(x, "=", Arg()),
                "ne" => Compare(x, "<>", Arg()),
                "lt" or "lessthan" => Compare(x, "<", Arg()),
                "le" => Compare(x, "<=", Arg()),
                "gt" or "greaterthan" => Compare(x, ">", Arg()),
                "ge" => Compare(x, ">=", Arg()),
                "in" => In(x, Arg()),
                // "is true" / "is false" ask for the boolean itself: a text such as a field is neither.
                "true" => new Node(x.Type == T.Bool ? $"({x.Code} = true)" : "false", T.Bool),
                "false" => new Node(x.Type == T.Bool ? $"({x.Code} = false)" : "false", T.Bool),
                _ => throw new JinjaNotSupported($"the '{name.Text}' test has no PowerFlows formula equivalent"),
            };
        }

        private Node ParseConcat()
        {
            var left = ParseAdd();
            while (IsOp("~"))
            {
                _pos++;
                var right = ParseAdd();
                left = new Node($"{AsText(left).Code} & {AsText(right).Code}", T.Text);
            }
            return left;
        }

        private Node ParseAdd()
        {
            var left = ParseMul();
            while (IsOp("+") || IsOp("-"))
            {
                var op = Next().Text;
                left = Arithmetic(left, op, ParseMul());
            }
            return left;
        }

        private Node ParseMul()
        {
            var left = ParsePow();
            while (IsOp("*") || IsOp("/") || IsOp("//") || IsOp("%"))
            {
                var op = Next().Text;
                var right = ParsePow();
                var (ln, rn) = (AsNumber(left), AsNumber(right));
                var (l, r) = (ln.Code, rn.Code);
                left = op switch
                {
                    // Python // rounds down (-7 // 2 is -4); / is always a float.
                    "//" => Num($"Floor({l} / {r})", ln, rn),
                    "%" => Num($"Mod({l}, {r})", ln, rn),
                    "/" => new Node($"({l} / {r})", T.Number, Float: true),
                    _ => Num($"({l} {op} {r})", ln, rn),
                };
            }
            return left;
        }

        /// <summary>a ** b → a ^ b. As Jinja's parser has it, ** binds looser than a leading minus (-2 ** 2 is 4) and goes left to right.</summary>
        private Node ParsePow()
        {
            var left = ParseUnary(withFilter: true);
            while (IsOp("**"))
            {
                _pos++;
                var (b, e) = (AsNumber(left), AsNumber(ParseUnary(withFilter: true)));
                left = Num($"({b.Code} ^ {e.Code})", b, e);
            }
            return left;
        }

        /// <summary>As Jinja's parse_unary: a leading minus applies to the value, and the filters after it to the negated value
        /// (-5|abs is 5).</summary>
        private Node ParseUnary(bool withFilter)
        {
            Node node;
            if (IsOp("-")) { _pos++; var v = AsNumber(ParseUnary(withFilter: false)); node = Num($"(-{v.Code})", v); }
            else if (IsOp("+")) { _pos++; node = AsNumber(ParseUnary(withFilter: false)); }
            else node = ParsePrimary();
            return ParsePostfix(node, withFilter);
        }

        private static Node Arithmetic(Node l, string op, Node r)
        {
            if (op == "+")
            {
                if (r.Type is T.Months or T.Years && l.Type is not (T.Months or T.Years))
                    return new Node($"{(r.Type == T.Months ? "AdjustMonth" : "AdjustYear")}({DateOnly(l)}, {r.Code})", T.Date, Py: l.Py);
                if (l.Type is T.Months or T.Years) return Arithmetic(r, op, l);
                if (r.Type == T.Duration && l.Type != T.Duration) return new Node($"({AsDate(l).Code} + {r.Code})", AsDate(l).Type, Py: l.Py);
                if (l.Type == T.Duration && r.Type != T.Duration) return Arithmetic(r, op, l);
                if (l.Type == T.Duration) return new Node($"({l.Code} + {r.Code})", T.Duration);
                if (l.Type == T.Number || r.Type == T.Number) return Num($"({AsNumber(l).Code} + {AsNumber(r).Code})", l, r);
                // Jinja's + joins two texts (a field is text).
                return new Node($"{AsText(l).Code} & {AsText(r).Code}", T.Text);
            }
            if (r.Type is T.Months or T.Years)
                return new Node($"{(r.Type == T.Months ? "AdjustMonth" : "AdjustYear")}({DateOnly(l)}, -({r.Code}))", T.Date, Py: l.Py);
            if (r.Type == T.Duration) return new Node($"({AsDate(l).Code} - {r.Code})", AsDate(l).Type, Py: l.Py);
            if (DateLike(l) || DateLike(r)) return new Node($"({AsDate(l).Code} - {AsDate(r).Code})", T.Duration);
            return Num($"({AsNumber(l).Code} - {AsNumber(r).Code})", l, r);
        }

        private Node ParsePrimary()
        {
            var tok = Next();
            switch (tok.Kind)
            {
                case K.Num: return new Node(tok.Text, T.Number, Float: tok.Text.Contains('.'));
                case K.Str: return new Node(Str(tok.Text), T.Text);
                case K.Op when tok.Text == "(":
                    var inner = ParseExpr();
                    Expect(")");
                    return inner;
                case K.Name: break;
                default: throw new JinjaNotSupported($"'{{{{{_source}}}}}' is not an expression PowerFlows can write as a formula");
            }

            var path = new List<string> { tok.Text };
            // A dotted path stops before a method call (".date(", ".strftime("), except time.delta( / time.parse(.
            while (IsOp(".") && _toks[_pos + 1].Kind == K.Name)
            {
                var call = _toks[_pos + 2].Kind == K.Op && _toks[_pos + 2].Text == "(";
                if (call && !(path.Count == 1 && path[0] == "time")) break;
                _pos++;
                path.Add(Next().Text);
            }
            var name = string.Join('.', path);

            if (IsOp("("))
            {
                var args = Arguments();
                return name switch
                {
                    "time.delta" => Delta(args),
                    "time.parse" when args.Positional.Count == 1 => new Node($"ToDate({AsText(args.Positional[0]).Code})", T.Date, Py: "naive"),
                    _ => throw new JinjaNotSupported($"'{name}(…)' has no PowerFlows formula equivalent"),
                };
            }
            switch (name)
            {
                case "CLEAR": return new Node("\"\"", T.Text);
                case "true" or "True": return new Node("true", T.Bool);
                case "false" or "False": return new Node("false", T.Bool);
                case "time.now": return new Node("Now()", T.DateTime, Py: "aware");
                case "time.today": return new Node("ToDate(Now())", T.Date, Py: "aware");
                // none is no value: compared as an empty text, as a field without a value reads.
                case "none" or "None": return new Node("\"\"", T.Text, Py: "none");
                case "time.now.year" or "time.now.month" or "time.now.day" or "time.now.hour" or "time.now.minute" or "time.now.second":
                    return DatePart(new Node("Now()", T.DateTime), path[^1]);
                case "time.today.year" or "time.today.month" or "time.today.day":
                    return DatePart(new Node("ToDate(Now())", T.Date), path[^1]);
            }
            if (path.Count < 2 || path[0] is "time" or "runtime" or "loop")
                throw new JinjaNotSupported($"'{name}' has no PowerFlows formula equivalent");

            var problem = _resolve(name, out var parts, out var kind);
            if (problem is not null)
            {
                // a.created.year: the field a.created, then its year.
                if (path.Count > 2 && DateParts.Contains(path[^1]) && _resolve(string.Join('.', path.Take(path.Count - 1)), out var fieldParts, out _) is null)
                {
                    _refs.Add(fieldParts!);
                    return DatePart(new Node($"ToTimestamp(\u0001{_refs.Count - 1}\u0002)", T.DateTime), path[^1]);
                }
                throw new JinjaNotSupported(problem);
            }
            _refs.Add(parts!);
            var reference = $"\u0001{_refs.Count - 1}\u0002";
            return kind switch
            {
                // A checkbox is a boolean in Quickbase's Jinja, False when empty (checked in a real Quickbase pipeline).
                "bool" => new Node($"If(IsNull(ToBool({reference})), false, ToBool({reference}))", T.Bool),
                // A date or date/time field is a UTC datetime (2025-01-05 00:00:00+00:00), None when empty. Only its first 19
                // characters (yyyy-MM-ddTHH:mm:ss) are read: a value written as UTC with a "Z" would otherwise be read in local time.
                "date" or "datetime" => new Node($"ToTimestamp(Left(ToText({reference}), 19))", T.Any, Py: "field"),
                // A number field is a float in Quickbase when it has a fraction (12.5), an int when whole (10).
                "number" => new Node(reference, T.Any, FloatWhen: $"(Int(ToNumber({reference})) <> ToNumber({reference}))"),
                _ => new Node(reference, T.Any),
            };
        }

        private static readonly HashSet<string> DateParts = ["year", "month", "day", "hour", "minute", "second"];

        internal static bool IsDatePart(string name) => DateParts.Contains(name);

        private static Node DatePart(Node d, string part) => part switch
        {
            "year" => new Node($"Year({AsDate(d).Code})", T.Number),
            "month" => new Node($"Month({AsDate(d).Code})", T.Number),
            "day" => new Node($"Day({AsDate(d).Code})", T.Number),
            "hour" => new Node($"Hour({AsTimestamp(d).Code})", T.Number),
            "minute" => new Node($"Minute({AsTimestamp(d).Code})", T.Number),
            "second" => new Node($"Second({AsTimestamp(d).Code})", T.Number),
            _ => throw new JinjaNotSupported($"'.{part}' has no PowerFlows formula equivalent"),
        };

        private static Node AsTimestamp(Node d) => d.Type == T.DateTime ? d : new Node($"ToTimestamp({AsDate(d).Code})", T.DateTime);

        private Node ParsePostfix(Node node, bool withFilter = true)
        {
            while (true)
            {
                if (withFilter && IsOp("|"))
                {
                    _pos++;
                    var filter = Next();
                    if (filter.Kind != K.Name) throw new JinjaNotSupported($"'{_source}' has a filter PowerFlows cannot read");
                    var args = IsOp("(") ? Arguments() : new Args([], []);
                    node = Filter(node, filter.Text, args);
                    continue;
                }
                if (IsOp(".") && _toks[_pos + 1].Kind == K.Name)
                {
                    _pos++;
                    var member = Next().Text;
                    if (IsOp("("))
                    {
                        var args = Arguments();
                        node = Method(node, member, args);
                        continue;
                    }
                    node = (member, node.Type) switch
                    {
                        ("days", T.Duration) => new Node($"ToDays({node.Code})", T.Number),
                        ("seconds", T.Duration) => new Node($"ToSeconds({node.Code})", T.Number),
                        (_, T.Date or T.DateTime) when DateParts.Contains(member) => DatePart(node, member),
                        _ => throw new JinjaNotSupported($"'.{member}' has no PowerFlows formula equivalent"),
                    };
                    continue;
                }
                if (IsOp("["))
                {
                    node = Subscript(node);
                    continue;
                }
                return node;
            }
        }

        private static Node Stamp(Node node) => node.Type == T.Any ? new Node($"ToTimestamp({node.Code})", T.DateTime) : node;

        /// <summary>Python methods Jinja allows on a text or a date.</summary>
        private static Node Method(Node node, string member, Args args)
        {
            var p = args.Positional;
            if (args.Named.Count > 0) throw new JinjaNotSupported($"'.{member}(…)' with named arguments has no PowerFlows formula equivalent");
            return (member, p.Count) switch
            {
                ("date", 0) => new Node($"ToDate({AsDate(node).Code})", T.Date, Py: node.Py is null ? null : "date"),
                ("strftime", 1) when p[0] is { Type: T.Text } format && format.Code.StartsWith('"') =>
                    Strftime(AsDate(Stamp(node)), Unquote(format.Code)),
                // Python: Monday = 0 … Sunday = 6; ISO: Monday = 1 … Sunday = 7. WeekDay is Sunday = 1 … Saturday = 7.
                ("weekday", 0) => new Node($"Mod(WeekDay({AsDate(Stamp(node)).Code}) + 5, 7)", T.Number),
                ("isoweekday", 0) => new Node($"(Mod(WeekDay({AsDate(Stamp(node)).Code}) + 5, 7) + 1)", T.Number),
                ("upper", 0) => new Node($"Upper({AsText(node).Code})", T.Text),
                ("lower", 0) => new Node($"Lower({AsText(node).Code})", T.Text),
                ("strip", 0) => new Node($"Trim({AsText(node).Code})", T.Text),
                ("capitalize", 0) => Capitalize(node),
                ("startswith", 1) => new Node($"Begins({AsText(node).Code}, {AsText(p[0]).Code})", T.Bool),
                ("endswith", 1) => new Node($"Ends({AsText(node).Code}, {AsText(p[0]).Code})", T.Bool),
                ("replace", 2) => new Node($"SearchAndReplace({AsText(node).Code}, {AsText(p[0]).Code}, {AsText(p[1]).Code})", T.Text),
                // Python find is 0-based and -1 when missing; Find is 1-based and 0 when missing.
                ("find", 1) => new Node($"(Find({AsText(node).Code}, {AsText(p[0]).Code}) - 1)", T.Number),
                ("count", 1) => new Node($"((Length({AsText(node).Code}) - Length(SearchAndReplace({AsText(node).Code}, {AsText(p[0]).Code}, \"\"))) / Length({AsText(p[0]).Code}))", T.Number),
                ("split", 1) => new Node($"Split({AsText(node).Code}, {AsText(p[0]).Code})", T.List, AsText(node).Code, AsText(p[0]).Code),
                _ => throw new JinjaNotSupported($"'.{member}(…)' has no PowerFlows formula equivalent"),
            };
        }

        private static Node Capitalize(Node node)
        {
            var t = AsText(node).Code;
            return new Node($"(Upper(Left({t}, 1)) & Lower(NotLeft({t}, 1)))", T.Text);
        }

        /// <summary><c>x[i]</c>, <c>x[a:b]</c> on a text, with whole-number literals as Python slices them; <c>a.split(',')[i]</c> → Part.</summary>
        private Node Subscript(Node node)
        {
            Expect("[");
            int? Index()
            {
                if (IsOp(":") || IsOp("]")) return null;
                var minus = IsOp("-");
                if (minus) _pos++;
                if (Peek.Kind != K.Num || Peek.Text.Contains('.')) throw new JinjaNotSupported($"'{_source}' indexes with a value PowerFlows formulas cannot slice by");
                var n = int.Parse(Next().Text, CultureInfo.InvariantCulture);
                return minus ? -n : n;
            }
            var start = Index();
            if (IsOp("]"))
            {
                _pos++;
                if (start is not { } i) throw new JinjaNotSupported($"'{_source}' has an empty index");
                if (node.Type == T.List)
                {
                    if (node.SplitText is null || i < 0) throw new JinjaNotSupported("this list index has no PowerFlows formula equivalent");
                    return new Node($"Part({node.SplitText}, {i + 1}, {node.SplitSep})", T.Text);
                }
                var s = AsText(node).Code;
                return i >= 0 ? new Node($"Mid({s}, {i + 1}, 1)", T.Text) : new Node($"Left(Right({s}, {-i}), 1)", T.Text);
            }
            Expect(":");
            var end = Index();
            Expect("]");
            if (node.Type == T.List) throw new JinjaNotSupported("slicing a list has no PowerFlows formula equivalent");
            var x = AsText(node).Code;
            var code = (start, end) switch
            {
                (null, null) => x,
                (null, >= 0) => $"Left({x}, {end})",
                (null, < 0) => $"NotRight({x}, {-end})",
                (>= 0, null) => $"NotLeft({x}, {start})",
                (< 0, null) => $"Right({x}, {-start})",
                (>= 0, >= 0) => end > start ? $"Mid({x}, {start + 1}, {end - start})" : "\"\"",
                (>= 0, < 0) => $"NotRight(NotLeft({x}, {start}), {-end})",
                (< 0, < 0) => end > start ? $"NotRight(Right({x}, {-start}), {-end})" : "\"\"",
                _ => throw new JinjaNotSupported($"'{_source}' slices in a way PowerFlows formulas cannot"),
            };
            return new Node(code, T.Text);
        }

        private sealed record Args(List<Node> Positional, Dictionary<string, Node> Named);

        private Args Arguments()
        {
            Expect("(");
            var positional = new List<Node>();
            var named = new Dictionary<string, Node>(StringComparer.Ordinal);
            while (!IsOp(")"))
            {
                if (Peek.Kind == K.Name && _toks[_pos + 1].Kind == K.Op && _toks[_pos + 1].Text == "=")
                {
                    var key = Next().Text;
                    _pos++;
                    named[key] = ParseExpr();
                }
                else positional.Add(ParseExpr());
                if (IsOp(",")) _pos++;
                else if (!IsOp(")")) Expect(")");
            }
            _pos++;
            return new Args(positional, named);
        }

        private static Node Delta(Args args)
        {
            if (args.Positional.Count > 0 || args.Named.Count == 0)
                throw new JinjaNotSupported("time.delta(…) needs named spans such as days=1");
            if (args.Named.Count == 1 && args.Named.First().Key is "months" or "years")
                return new Node(AsNumber(args.Named.First().Value).Code, args.Named.First().Key == "months" ? T.Months : T.Years);
            var spans = args.Named.Select(kv => kv.Key switch
            {
                "days" => $"Days({AsNumber(kv.Value).Code})",
                "hours" => $"Hours({AsNumber(kv.Value).Code})",
                "minutes" => $"Minutes({AsNumber(kv.Value).Code})",
                "seconds" => $"Seconds({AsNumber(kv.Value).Code})",
                "weeks" => $"Weeks({AsNumber(kv.Value).Code})",
                _ => throw new JinjaNotSupported($"time.delta({kv.Key}=…) has no PowerFlows formula equivalent"),
            });
            return new Node("(" + string.Join(" + ", spans) + ")", T.Duration);
        }

        private static string Unquote(string literal) =>
            Regex.Replace(literal[1..^1], @"\\(.)", "$1");

        private static Node Filter(Node x, string name, Args args)
        {
            Node Only(Node result) => args.Positional.Count == 0 && args.Named.Count == 0 ? result
                : throw new JinjaNotSupported($"the '{name}' filter with arguments has no PowerFlows formula equivalent");
            switch (name)
            {
                case "date_mdy" or "date_ymd" or "date_dmy":
                {
                    var sep = args.Positional switch
                    {
                        [] => "-",
                        [{ Type: T.Text } s] when s.Code.StartsWith('"') => Unquote(s.Code),
                        _ => throw new JinjaNotSupported($"the '{name}' separator must be a text"),
                    };
                    var d = AsDate(x).Code;
                    string Y = $"ToText(Year({d}))", M = Pad($"Month({d})"), D = Pad($"Day({d})"), S = $" & {Str(sep)} & ";
                    return new Node("(" + (name switch { "date_ymd" => Y + S + M + S + D, "date_dmy" => D + S + M + S + Y, _ => M + S + D + S + Y }) + ")", T.Text);
                }
                case "upper": return Only(new Node($"Upper({AsText(x).Code})", T.Text));
                case "lower": return Only(new Node($"Lower({AsText(x).Code})", T.Text));
                case "trim": return Only(new Node($"Trim({AsText(x).Code})", T.Text));
                case "string": return Only(AsText(x));
                case "urlencode": return Only(new Node($"URLEncode({AsText(x).Code})", T.Text));
                // powerflowextraRd: html2text is not here: Quickbase turns HTML into Markdown with it (<b>x</b> → **x**), which no formula does.
                case "striptags": return Only(new Node($"HTMLToText({AsText(x).Code})", T.Text));
                case "length" or "count" when x.Type == T.List: return Only(new Node($"Count({x.Code})", T.Number));
                case "length" or "count": return Only(new Node($"Length({AsText(x).Code})", T.Number));
                case "first" when x.Type == T.List && x.SplitText is not null: return Only(new Node($"Part({x.SplitText}, 1, {x.SplitSep})", T.Text));
                case "first": return Only(new Node($"Left({AsText(x).Code}, 1)", T.Text));
                case "last": return Only(new Node($"Right({AsText(x).Code}, 1)", T.Text));
                case "join" when x.Type == T.List && args.Named.Count == 0 && args.Positional.Count <= 1:
                    return new Node($"Join({x.Code}, {(args.Positional.Count == 1 ? AsText(args.Positional[0]).Code : "\"\"")})", T.Text);
                case "capitalize": return Only(Capitalize(x));
                case "base64_encode" or "b64encode": return Only(new Node($"Base64Encode({AsText(x).Code})", T.Text));
                case "base64_decode" or "b64decode": return Only(new Node($"Base64Decode({AsText(x).Code})", T.Text));
                case "text": return Only(new Node($"HTMLToText({AsText(x).Code})", T.Text));
                case "int": return Only(new Node($"Int({AsNumber(x).Code})", T.Number));
                case "float": return Only(AsNumber(x) with { Float = true });
                case "abs": return Only(Num($"Abs({AsNumber(x).Code})", x));
                case "round" when args.Positional.Count <= 2 && args.Named.Keys.All(k => k is "precision" or "method"):
                {
                    var precision = args.Named.TryGetValue("precision", out var pn) ? pn : args.Positional.ElementAtOrDefault(0);
                    var method = args.Named.TryGetValue("method", out var mn) ? mn : args.Positional.ElementAtOrDefault(1);
                    var value = AsNumber(x).Code;
                    var digits = precision is null ? "0" : AsNumber(precision).Code;
                    // Jinja's round always gives a float. 'common' is Python's round: a half goes to the even neighbour
                    // (2.5 → 2.0, 3.5 → 4.0), so the scaled value is halved, rounded and doubled when it is exactly half-way.
                    var scaled = $"({value} * (10 ^ {digits}))";
                    var code = method?.Code switch
                    {
                        null or "\"common\"" =>
                            $"(If(Abs({scaled} - Int({scaled})) = 0.5, 2 * Round({scaled} / 2, 0), Round({scaled}, 0)) / (10 ^ {digits}))",
                        "\"floor\"" => $"RoundDown({value}, {digits})",
                        "\"ceil\"" => $"RoundUp({value}, {digits})",
                        _ => throw new JinjaNotSupported("round(…) with that method has no PowerFlows formula equivalent"),
                    };
                    return new Node(code, T.Number, Float: true);
                }
                case "format" when args.Named.Count == 0 && x.Type == T.Text && x.Code.StartsWith('"'):
                {
                    // "Hello, %s"|format(a.name): each %s / %d is the next value.
                    var pieces = Regex.Split(Unquote(x.Code), "(%[sd%])");
                    var next = 0;
                    var codes = new List<string>();
                    foreach (var piece in pieces)
                    {
                        if (piece is "%s" or "%d")
                        {
                            if (next >= args.Positional.Count) throw new JinjaNotSupported("format(…) has fewer values than placeholders");
                            var v = args.Positional[next++];
                            codes.Add(piece == "%d" ? $"ToText(Int({AsNumber(v).Code}))" : AsText(v).Code);
                        }
                        else if (piece == "%%") codes.Add(Str("%"));
                        else if (piece.Length > 0) codes.Add(Str(piece));
                    }
                    if (next != args.Positional.Count || codes.Count == 0)
                        throw new JinjaNotSupported("format(…) has no PowerFlows formula equivalent for these values");
                    return new Node("(" + string.Join(" & ", codes) + ")", T.Text);
                }
                case "replace" when args.Named.Count == 0 && args.Positional.Count == 2:
                    return new Node($"SearchAndReplace({AsText(x).Code}, {AsText(args.Positional[0]).Code}, {AsText(args.Positional[1]).Code})", T.Text);
                case "default" or "d" when args.Positional.Count is 1 or 2 && args.Named.Keys.All(k => k is "default_value" or "boolean"):
                {
                    // powerflowextraRd: checked in a real Quickbase pipeline — an empty field is None, which is defined, so
                    // default(v) leaves it as it is (prints nothing); only default(v, true) replaces an empty (falsy) value.
                    var boolean = args.Positional.ElementAtOrDefault(1) ?? (args.Named.TryGetValue("boolean", out var b) ? b : null);
                    if (boolean is null || boolean.Code == "false") return x;
                    if (boolean.Code != "true") throw new JinjaNotSupported("default(…) with a computed second value has no PowerFlows formula equivalent");
                    var fallback = args.Positional[0];
                    var empty = AsBool(x).Code;
                    return x.Type == fallback.Type && x.Type is not T.Any
                        ? new Node($"If({empty}, {x.Code}, {fallback.Code})", x.Type, Float: x.Float || fallback.Float)
                        : new Node($"If({empty}, {AsText(x).Code}, {AsText(fallback).Code})", T.Text);
                }
                default:
                    throw new JinjaNotSupported($"the '{name}' filter has no PowerFlows formula equivalent");
            }
        }

        private static Node Strftime(Node d, string format)
        {
            var parts = new List<string>();
            var literal = new StringBuilder();
            void Flush() { if (literal.Length > 0) { parts.Add(Str(literal.ToString())); literal.Clear(); } }
            var hour12 = $"If(Mod(Hour({AsTimestamp(d).Code}), 12) = 0, 12, Mod(Hour({AsTimestamp(d).Code}), 12))";
            for (var i = 0; i < format.Length; i++)
            {
                if (format[i] != '%' || i + 1 >= format.Length) { literal.Append(format[i]); continue; }
                var code = format[++i] switch
                {
                    'Y' => $"ToText(Year({d.Code}))",
                    'y' => Pad($"Mod(Year({d.Code}), 100)"),
                    'j' => $"Right(\"00\" & ToText(DayOfYear({d.Code})), 3)",
                    'w' => $"ToText(WeekDay({d.Code}) - 1)",
                    'm' => Pad($"Month({d.Code})"),
                    'd' => Pad($"Day({d.Code})"),
                    'H' => Pad($"Hour({d.Code})"),
                    'I' => Pad(hour12),
                    'M' => Pad($"Minute({d.Code})"),
                    'S' => Pad($"Second({d.Code})"),
                    'p' => $"If(Hour({d.Code}) < 12, \"AM\", \"PM\")",
                    'B' => $"NameOfMonth({d.Code})",
                    'b' => $"Left(NameOfMonth({d.Code}), 3)",
                    'A' => $"NameOfDay({d.Code})",
                    'a' => $"Left(NameOfDay({d.Code}), 3)",
                    '%' => null,
                    var other => throw new JinjaNotSupported($"strftime '%{other}' has no PowerFlows formula equivalent"),
                };
                if (code is null) { literal.Append('%'); continue; }
                Flush();
                parts.Add(code);
            }
            Flush();
            return new Node(parts.Count == 0 ? "\"\"" : "(" + string.Join(" & ", parts) + ")", T.Text);
        }
    }
}
