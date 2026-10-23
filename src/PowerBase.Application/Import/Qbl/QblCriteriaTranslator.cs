using System.Text;
using PowerBase.Application.Import.Pbl;

namespace PowerBase.Application.Import.Qbl;

public sealed class QblCriteriaResult
{
    public PblFilterGroup? Filter { get; init; }

    /// <summary>Why the criterion could not be translated (unknown field, unsupported operator, malformed). Null on success.</summary>
    public string? Problem { get; init; }
    public bool Succeeded => Problem is null && Filter is not null;
}

/// <summary>
/// Translates a Quickbase query — <c>({'8'.CT.'Test'})</c>, <c>{'200'.EX.'1'} AND ({'5'.GT.'3'} OR {'6'.XEX.'a'})</c> —
/// into a <see cref="PblFilterGroup"/>. Field ids are the source field <c>Id</c>s, resolved to PBL field names through
/// <c>resolveField</c>. Anything it cannot represent faithfully (unknown field, an operator with no PowerBase
/// equivalent such as <c>IR</c>, a malformed expression) fails the whole query: callers must treat that as
/// "cannot filter" and stay at the safer side (a role filter that cannot be translated must never become "no filter").
/// </summary>
public static class QblCriteriaTranslator
{
    private const string CURRENT_USER = "_curuser_";

    // Quickbase operator → PowerBase filter operator.
    private static readonly Dictionary<string, string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EX"] = "eq", ["XEX"] = "ne",
        ["CT"] = "contains", ["XCT"] = "notContains",
        ["SW"] = "startsWith", ["XSW"] = "notStartsWith",
        ["GT"] = "gt", ["GTE"] = "gte", ["LT"] = "lt", ["LTE"] = "lte",
        ["BF"] = "lt", ["AF"] = "gt", ["OBF"] = "lte", ["OAF"] = "gte",
        ["HAS"] = "includes", ["XHAS"] = "notIncludes",
        // True-value tests: handled specially (value forced).
        ["TV"] = "eq", ["XTV"] = "eq",
    };

    /// <param name="allowPrompts">Report filters may ask the viewer for a value (<c>_ask1_</c>); role record filters may not.</param>
    /// <param name="allowRelativeDates">Report filters resolve "today"/"yesterday"/"tomorrow" when the report runs; summaries and role filters do not.</param>
    public static QblCriteriaResult Translate(string? criteria, Func<int, string?> resolveField, bool allowPrompts = false, bool allowRelativeDates = false)
    {
        if (string.IsNullOrWhiteSpace(criteria))
            return new QblCriteriaResult { Problem = "empty criteria" };

        try
        {
            var parser = new Parser(criteria, resolveField, allowPrompts, allowRelativeDates);
            var node = parser.ParseExpression();
            parser.ExpectEnd();
            return new QblCriteriaResult { Filter = ToGroup(node) };
        }
        catch (CriteriaException ex)
        {
            return new QblCriteriaResult { Problem = ex.Message };
        }
    }

    // ---- tree ----

    private abstract record Node;
    private sealed record Leaf(PblFilterCondition Condition) : Node;
    private sealed record Branch(string Logic, List<Node> Children) : Node;

    private static PblFilterGroup ToGroup(Node node)
    {
        if (node is Leaf leaf)
            return new PblFilterGroup { Logic = "and", Conditions = [leaf.Condition] };

        var branch = (Branch)node;
        var group = new PblFilterGroup { Logic = branch.Logic };
        foreach (var child in branch.Children)
        {
            if (child is Leaf l) group.Conditions.Add(l.Condition);
            else group.Groups.Add(ToGroup(child));
        }
        return group;
    }

    private sealed class CriteriaException(string message) : Exception(message);

    // ---- parser: expr := term ((AND|OR) term)* with AND binding tighter than OR ----

    private sealed class Parser
    {
        private readonly string _s;
        private readonly Func<int, string?> _resolve;
        private readonly bool _allowPrompts;
        private readonly bool _allowRelativeDates;
        private int _i;

        public Parser(string s, Func<int, string?> resolve, bool allowPrompts, bool allowRelativeDates)
        {
            _s = s;
            _resolve = resolve;
            _allowPrompts = allowPrompts;
            _allowRelativeDates = allowRelativeDates;
        }

        public void ExpectEnd()
        {
            SkipWs();
            if (_i < _s.Length)
                throw new CriteriaException($"unexpected text at position {_i}");
        }

        public Node ParseExpression()
        {
            var orParts = new List<Node> { ParseAnd() };
            while (TryKeyword("OR"))
                orParts.Add(ParseAnd());
            return orParts.Count == 1 ? orParts[0] : new Branch("or", orParts);
        }

        private Node ParseAnd()
        {
            var parts = new List<Node> { ParseTerm() };
            while (TryKeyword("AND"))
                parts.Add(ParseTerm());
            return parts.Count == 1 ? parts[0] : new Branch("and", parts);
        }

        private Node ParseTerm()
        {
            SkipWs();
            if (_i >= _s.Length)
                throw new CriteriaException("unexpected end of criteria");

            if (_s[_i] == '(')
            {
                _i++;
                var inner = ParseExpression();
                SkipWs();
                if (_i >= _s.Length || _s[_i] != ')')
                    throw new CriteriaException("missing closing parenthesis");
                _i++;
                return inner;
            }

            if (_s[_i] == '{')
                return ParseCondition();

            throw new CriteriaException($"unexpected character '{_s[_i]}'");
        }

        // {'FID'.OP.'value'}  |  {FID.OP.value}
        private Node ParseCondition()
        {
            _i++; // {
            var fidText = ReadUntilDot().Trim().Trim('\'');
            if (!int.TryParse(fidText, out var fid))
                throw new CriteriaException($"field id '{fidText}' is not numeric");

            var op = ReadUntilDot().Trim();
            var isRange = string.Equals(op, "IR", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "XIR", StringComparison.OrdinalIgnoreCase);
            string pbOperator = "";
            if (!isRange && !Operators.TryGetValue(op, out pbOperator!))
                throw new CriteriaException($"operator '{op}' has no PowerBase equivalent");

            var value = ReadValue();
            if (_i >= _s.Length || _s[_i] != '}')
                throw new CriteriaException("condition is not closed with '}'");
            _i++;

            var fieldName = _resolve(fid) ?? throw new CriteriaException($"field id {fid} does not resolve to a field");

            // "In range" (IR) / "not in range" (XIR) with a relative period: PowerBase's during / notDuring operators.
            if (isRange)
            {
                if (!_allowRelativeDates)
                    throw new CriteriaException($"the relative period '{value}' has no PowerBase equivalent here");
                var during = ParseRelativePeriod(value) ?? throw new CriteriaException($"the period '{value}' (operator {op.ToUpperInvariant()}) has no PowerBase equivalent");
                return new Leaf(new PblFilterCondition
                {
                    FieldName = fieldName,
                    Operator = string.Equals(op, "IR", StringComparison.OrdinalIgnoreCase) ? "during" : "notDuring",
                    ValueMode = during.Mode,
                    Value = during.Value,
                });
            }

            var condition = new PblFilterCondition { FieldName = fieldName, Operator = pbOperator, Value = value };
            // An empty comparison value means "is empty" / "is not empty".
            if (string.IsNullOrEmpty(value) && string.Equals(op, "EX", StringComparison.OrdinalIgnoreCase)) { condition.Operator = "isEmpty"; condition.Value = null; }
            else if (string.IsNullOrEmpty(value) && string.Equals(op, "XEX", StringComparison.OrdinalIgnoreCase)) { condition.Operator = "isNotEmpty"; condition.Value = null; }

            if (System.Text.RegularExpressions.Regex.IsMatch(value ?? "", @"^_ask\d*_$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                if (!_allowPrompts)
                    throw new CriteriaException("a prompt for a value (_ask_) cannot be part of a stored record filter");
                condition.ValueMode = "ask";
                condition.Value = null;
            }
            else if (System.Text.RegularExpressions.Regex.Match(value ?? "", @"^_fid_(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } fidMatch)
            {
                condition.ValueFieldName = _resolve(int.Parse(fidMatch.Groups[1].Value)) ?? throw new CriteriaException($"compared field id {fidMatch.Groups[1].Value} does not resolve to a field");
                condition.ValueMode = "field";
                condition.Value = null;
            }
            else if (string.Equals(value, "today", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yesterday", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "tomorrow", StringComparison.OrdinalIgnoreCase))
            {
                if (!_allowRelativeDates)
                    throw new CriteriaException($"the relative date '{value}' has no PowerBase equivalent here");
                condition.ValueMode = value!.ToLowerInvariant();
                condition.Value = null;
            }

            if (!string.Equals(value, CURRENT_USER, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(op, "TV", StringComparison.OrdinalIgnoreCase)) condition.Value = "true";
                else if (string.Equals(op, "XTV", StringComparison.OrdinalIgnoreCase)) condition.Value = "false";
            }

            if (string.Equals(value, CURRENT_USER, StringComparison.OrdinalIgnoreCase))
            {
                // PowerBase has "is / is not the current user" operators; no other comparison against the user can be expressed.
                condition.Operator = op.ToUpperInvariant() switch
                {
                    // "TV" (true value) against the current user: the user field holds / contains the current user.
                    "EX" or "TV" => "isCurrentUser",
                    "XEX" or "XTV" => "isNotCurrentUser",
                    _ => throw new CriteriaException($"operator '{op}' against the current user has no PowerBase equivalent"),
                };
                condition.UseCurrentUser = true;
                condition.Value = null;
            }

            return new Leaf(condition);
        }

        /// <summary>"this wk", "last 5 wk", "last 60 d", "next 2 mon" → PowerBase's during modes (duringCurrent / duringPrevious / duringNext)
        /// with a "count:unit" value. "this" is one period; the previous/next N periods exclude the period in progress, as in Quickbase.</summary>
        private static (string Mode, string Value)? ParseRelativePeriod(string? text)
        {
            var m = System.Text.RegularExpressions.Regex.Match((text ?? "").Trim(),
                @"^(?<which>this|last|next)(?:\s+(?<n>\d+))?\s+(?<unit>d|day|days|wk|week|weeks|mon|month|months|qtr|quarter|quarters|yr|year|years)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            var unit = m.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "d" or "day" or "days" => "day",
                "wk" or "week" or "weeks" => "week",
                "mon" or "month" or "months" => "month",
                "qtr" or "quarter" or "quarters" => "quarter",
                _ => "year",
            };
            var count = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : 1;
            var mode = m.Groups["which"].Value.ToLowerInvariant() switch { "this" => "duringCurrent", "last" => "duringPrevious", _ => "duringNext" };
            return (mode, $"{(mode == "duringCurrent" ? 1 : Math.Max(1, count))}:{unit}");
        }

        private string ReadUntilDot()
        {
            var sb = new StringBuilder();
            var quoted = false;
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (c == '\'') quoted = !quoted;
                if (c == '.' && !quoted) { _i++; return sb.ToString(); }
                sb.Append(c);
                _i++;
            }
            throw new CriteriaException("condition is incomplete");
        }

        private string ReadValue()
        {
            var sb = new StringBuilder();
            if (_i < _s.Length && _s[_i] == '\'')
            {
                _i++;
                while (_i < _s.Length)
                {
                    if (_s[_i] == '\'')
                    {
                        if (_i + 1 < _s.Length && _s[_i + 1] == '\'') { sb.Append('\''); _i += 2; continue; }
                        _i++;
                        return sb.ToString();
                    }
                    sb.Append(_s[_i++]);
                }
                throw new CriteriaException("unterminated quoted value");
            }

            while (_i < _s.Length && _s[_i] != '}')
                sb.Append(_s[_i++]);
            return sb.ToString().Trim();
        }

        private bool TryKeyword(string keyword)
        {
            SkipWs();
            if (_i + keyword.Length <= _s.Length
                && string.Compare(_s, _i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (_i + keyword.Length == _s.Length || !char.IsLetterOrDigit(_s[_i + keyword.Length])))
            {
                _i += keyword.Length;
                return true;
            }
            return false;
        }

        private void SkipWs()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }
    }
}
