using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

internal static class TextFunctions
{
    public static void Register(FunctionRegistry r)
    {
        r.Add(Fn.Exact("Length", FormulaType.Number, new[] { P.Text }, (a, _) => FormulaValue.Number(a[0].AsText().Length)));
        r.Add(Fn.Exact("Trim", FormulaType.Text, new[] { P.Text }, (a, _) => FormulaValue.Text(a[0].AsText().Trim())));
        //r.Add(Fn.Exact("Trim", FormulaType.Text, new[] { P.Text }, (a, _) => FormulaValue.Text(a[0].AsText().())));
        r.Add(Fn.Exact("Lower", FormulaType.Text, new[] { P.Text }, (a, _) => FormulaValue.Text(a[0].AsText().ToLowerInvariant())));
        r.Add(Fn.Exact("Upper", FormulaType.Text, new[] { P.Text }, (a, _) => FormulaValue.Text(a[0].AsText().ToUpperInvariant())));
        r.Add(Fn.Exact("Left", FormulaType.Text, new[] { P.Text, P.Number }, (a, _) => FormulaValue.Text(Left(a[0].AsText(), ToInt(a[1])))));
        r.Add(Fn.Exact("Right", FormulaType.Text, new[] { P.Text, P.Number }, (a, _) => FormulaValue.Text(Right(a[0].AsText(), ToInt(a[1])))));
        r.Add(Fn.Exact("NotLeft", FormulaType.Text, new[] { P.Text, P.Number }, (a, _) => FormulaValue.Text(NotLeft(a[0].AsText(), ToInt(a[1])))));
        r.Add(Fn.Exact("NotRight", FormulaType.Text, new[] { P.Text, P.Number }, (a, _) => FormulaValue.Text(NotRight(a[0].AsText(), ToInt(a[1])))));
        r.Add(Fn.Exact("Mid", FormulaType.Text, new[] { P.Text, P.Number, P.Number }, (a, _) => FormulaValue.Text(Mid(a[0].AsText(), ToInt(a[1]), ToInt(a[2])))));
        r.Add(Fn.Exact("Contains", FormulaType.Bool, new[] { P.Text, P.Text }, (a, _) => FormulaValue.Bool(a[0].AsText().Contains(a[1].AsText(), StringComparison.Ordinal))));
        r.Add(Fn.Exact("Begins", FormulaType.Bool, new[] { P.Text, P.Text }, (a, _) => FormulaValue.Bool(a[0].AsText().StartsWith(a[1].AsText(), StringComparison.Ordinal))));
        r.Add(Fn.Exact("Ends", FormulaType.Bool, new[] { P.Text, P.Text }, (a, _) => FormulaValue.Bool(a[0].AsText().EndsWith(a[1].AsText(), StringComparison.Ordinal))));
        r.Add(Fn.Exact("Find", FormulaType.Number, new[] { P.Text, P.Text }, (a, _) => FormulaValue.Number(Find(a[0].AsText(), a[1].AsText()))));
        r.Add(Fn.Exact("Replace", FormulaType.Text, new[] { P.Text, P.Text, P.Text }, (a, _) => FormulaValue.Text(Replace(a[0].AsText(), a[1].AsText(), a[2].AsText()))));
        r.Add(Fn.Exact("SearchAndReplace", FormulaType.Text, new[] { P.Text, P.Text, P.Text }, (a, _) => FormulaValue.Text(Replace(a[0].AsText(), a[1].AsText(), a[2].AsText()))));
        r.Add(Fn.Exact("Part", FormulaType.Text, new[] { P.Text, P.Number, P.Text }, (a, _) => FormulaValue.Text(Part(a[0].AsText(), ToInt(a[1]), a[2].AsText()))));
        r.Add(Fn.Variadic("Concat", FormulaType.Text, P.Text, 1, (a, _) => FormulaValue.Text(string.Concat(a.Select(v => v.AsText())))));
        r.Add(Fn.Variadic("List", FormulaType.Text, P.Text, 1, (a, _) => FormulaValue.Text(ListJoin(a))));
        r.Add(Fn.Exact("ToText", FormulaType.Text, new[] { P.Any }, (a, _) => FormulaValue.Text(ValueConvert.ToTextString(a[0]))));

        // Regex (BCL System.Text.RegularExpressions; bad patterns / timeouts degrade to a safe default).
        r.Add(Fn.Exact("RegexMatch", FormulaType.Bool, new[] { P.Text, P.Text }, (a, _) => FormulaValue.Bool(RegexMatch(a[0].AsText(), a[1].AsText()))));
        r.Add(Fn.Exact("RegexExtract", FormulaType.Text, new[] { P.Text, P.Text }, (a, _) => FormulaValue.Text(RegexExtract(a[0].AsText(), a[1].AsText()))));
        r.Add(Fn.Exact("RegexReplace", FormulaType.Text, new[] { P.Text, P.Text, P.Text }, (a, _) => FormulaValue.Text(RegexReplace(a[0].AsText(), a[1].AsText(), a[2].AsText()))));

        // Encoding / markup (BCL).
        r.Add(Fn.Exact("HTMLToText", FormulaType.Text, new[] { P.Text }, (a, _) => FormulaValue.Text(HtmlToText(a[0].AsText()))));
        r.Add(Fn.Exact("URLEncode", FormulaType.Text, new[] { P.Text }, (a, _) => FormulaValue.Text(Uri.EscapeDataString(a[0].AsText()))));
        r.Add(Fn.Exact("URLDecode", FormulaType.Text, new[] { P.Text }, (a, _) => FormulaValue.Text(UrlDecode(a[0].AsText()))));
        r.Add(Fn.Exact("Base64Encode", FormulaType.Text, new[] { P.Text }, (a, _) => FormulaValue.Text(Convert.ToBase64String(Encoding.UTF8.GetBytes(a[0].AsText())))));
        r.Add(Fn.Exact("Base64Decode", FormulaType.Text, new[] { P.Text }, (a, _) => FormulaValue.Text(Base64Decode(a[0].AsText()))));
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static int ToInt(FormulaValue v) => v.IsNull ? 0 : (int)Math.Truncate(v.AsNumber());

    private static string Left(string s, int n) { n = Math.Clamp(n, 0, s.Length); return s.Substring(0, n); }

    private static string Right(string s, int n) { n = Math.Clamp(n, 0, s.Length); return s.Substring(s.Length - n); }

    private static string NotLeft(string s, int n) { n = Math.Clamp(n, 0, s.Length); return s.Substring(n); }

    private static string NotRight(string s, int n) { n = Math.Clamp(n, 0, s.Length); return s.Substring(0, s.Length - n); }

    // 1-based position of the first occurrence; 0 when absent (Quickbase semantics).
    private static int Find(string s, string sub)
    {
        if (sub.Length == 0) return 0;
        int i = s.IndexOf(sub, StringComparison.Ordinal);
        return i < 0 ? 0 : i + 1;
    }

    private static bool RegexMatch(string s, string pattern)
    {
        try { return Regex.IsMatch(s, pattern, RegexOptions.None, RegexTimeout); }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    // First match; if the pattern has a capture group, the first group, else the whole match.
    private static string RegexExtract(string s, string pattern)
    {
        try
        {
            var m = Regex.Match(s, pattern, RegexOptions.None, RegexTimeout);
            if (!m.Success) return string.Empty;
            return m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
        }
        catch (ArgumentException) { return string.Empty; }
        catch (RegexMatchTimeoutException) { return string.Empty; }
    }

    private static string RegexReplace(string s, string pattern, string repl)
    {
        try { return Regex.Replace(s, pattern, repl, RegexOptions.None, RegexTimeout); }
        catch (ArgumentException) { return s; }
        catch (RegexMatchTimeoutException) { return s; }
    }

    private static string HtmlToText(string html)
    {
        if (html.Length == 0) return string.Empty;
        var stripped = Regex.Replace(html, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(stripped);
    }

    private static string UrlDecode(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    private static string Base64Decode(string s)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch (FormatException) { return string.Empty; }
    }

    private static string Mid(string s, int start1, int len)
    {
        int i0 = Math.Max(0, start1 - 1);
        if (i0 >= s.Length || len <= 0) return string.Empty;
        return s.Substring(i0, Math.Min(len, s.Length - i0));
    }

    private static string Replace(string s, string find, string repl) => find.Length == 0 ? s : s.Replace(find, repl);

    private static string Part(string s, int n, string delim)
    {
        if (n < 1) return string.Empty;
        var parts = delim.Length == 0 ? new[] { s } : s.Split(delim);
        return n <= parts.Length ? parts[n - 1] : string.Empty;
    }

    private static string ListJoin(IReadOnlyList<FormulaValue> a)
    {
        var delim = a[0].AsText();
        var items = a.Skip(1).Select(v => v.AsText()).Where(x => x.Length > 0);
        return string.Join(delim, items);
    }
}
