using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Binding;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Querying;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

public class RecordFunctionTests
{
    private static readonly FormulaEngine Engine = new();

    private static FormulaValue Eval(string expr, IRecordContext ctx)
    {
        var compiled = Engine.Compile(expr, EmptyFieldSchema.Instance);
        compiled.HasErrors.Should().BeFalse(because: FormulaEval.Diag(compiled));
        return Engine.Evaluate(compiled, ctx, EvaluationOptions.Default);
    }

    // ── Query-string parser ──────────────────────────────────────────────────

    [Fact]
    public void Parses_single_quoted_clause()
    {
        RecordQueryParser.TryParse("{6.EX.'Open'}", out var q).Should().BeTrue();
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Should().Be(new RecordQueryClause(6, "EX", "Open"));
        q.Connectors.Should().BeEmpty();
    }

    [Fact]
    public void Parses_bare_numeric_value()
    {
        RecordQueryParser.TryParse("{7.GT.100}", out var q).Should().BeTrue();
        q.Clauses[0].Should().Be(new RecordQueryClause(7, "GT", "100"));
    }

    [Fact]
    public void Parses_two_clauses_with_and()
    {
        RecordQueryParser.TryParse("{6.EX.'Open'}AND{7.GT.100}", out var q).Should().BeTrue();
        q.Clauses.Should().HaveCount(2);
        q.Connectors.Should().Equal(QueryConnector.And);
    }

    [Fact]
    public void Parses_or_and_ignores_whitespace()
    {
        RecordQueryParser.TryParse(" { 6 . EX . 'A' }  OR  { 6 . EX . 'B' } ", out var q).Should().BeTrue();
        q.Clauses.Should().HaveCount(2);
        q.Connectors.Should().Equal(QueryConnector.Or);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{6.EX.'Open'")]    // unterminated brace
    [InlineData("{6.EX.'Open'}XX{7.EX.'a'}")] // bad connector
    [InlineData("{.EX.'a'}")]       // missing fid
    public void Rejects_malformed_queries(string text)
    {
        RecordQueryParser.TryParse(text, out _).Should().BeFalse();
    }

    // ── Cross-table functions over a fake context ────────────────────────────

    private static FakeCrossTable TwoTableModel() => new(new()
    {
        ["items"] = new()
        {
            new(1, new() { [6] = "Open",   [7] = 10m, [8] = "Alpha" }),
            new(2, new() { [6] = "Open",   [7] = 30m, [8] = "Beta" }),
            new(3, new() { [6] = "Closed", [7] = 5m,  [8] = "Gamma" }),
        },
    });

    [Fact]
    public void GetRecords_then_GetFieldValues_returns_matching_values()
    {
        var v = Eval("GetFieldValues(GetRecords(\"{6.EX.'Open'}\", \"items\"), 8)", TwoTableModel());
        v.Type.Should().Be(FormulaType.TextList);
        v.AsTextList().Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public void SumValues_sums_a_numeric_field_over_the_record_set()
    {
        Eval("SumValues(GetRecords(\"{6.EX.'Open'}\", \"items\"), 7)", TwoTableModel())
            .AsNumber().Should().Be(40m);
    }

    [Fact]
    public void Count_measures_a_record_list()
    {
        Eval("Count(GetRecords(\"{6.EX.'Open'}\", \"items\"))", TwoTableModel())
            .AsNumber().Should().Be(2);
    }

    [Fact]
    public void GetRecords_with_and_narrows_results()
    {
        Eval("SumValues(GetRecords(\"{6.EX.'Open'}AND{7.GT.20}\", \"items\"), 7)", TwoTableModel())
            .AsNumber().Should().Be(30m);
    }

    [Fact]
    public void GetRecordByUniqueField_finds_the_one_record()
    {
        Eval("GetFieldValues(GetRecordByUniqueField(\"items\", 8, \"Beta\"), 7)", TwoTableModel())
            .AsTextList().Should().Equal("30");
    }

    [Fact]
    public void GetRecord_yields_the_record_when_it_exists()
    {
        Eval("Count(GetRecord(\"items\", 2))", TwoTableModel()).AsNumber().Should().Be(1);
        Eval("Count(GetRecord(\"items\", 99))", TwoTableModel()).AsNumber().Should().Be(0);
    }

    [Fact]
    public void Record_functions_resolve_empty_without_cross_table_support()
    {
        // EmptyContext inherits the default no-op cross-table members.
        Eval("Count(GetRecords(\"{6.EX.'Open'}\", \"items\"))", EmptyContext.Instance)
            .AsNumber().Should().Be(0);
    }

    [Fact]
    public void Malformed_query_yields_empty_record_set()
    {
        Eval("Count(GetRecords(\"not a query\", \"items\"))", TwoTableModel())
            .AsNumber().Should().Be(0);
    }
}

/// <summary>An empty schema — no field references resolve (record functions use string/numeric args).</summary>
internal sealed class EmptyFieldSchema : IFieldSchema
{
    public static readonly EmptyFieldSchema Instance = new();
    public bool TryResolve(string fieldName, out FieldRef field) { field = null!; return false; }
}

/// <summary>In-memory multi-table record context for exercising the cross-table functions.</summary>
internal sealed class FakeCrossTable : IRecordContext
{
    public sealed record Rec(long Id, Dictionary<long, object?> Fields);

    private readonly Dictionary<string, List<Rec>> _tables;
    private readonly string _current;

    public FakeCrossTable(Dictionary<string, List<Rec>> tables, string current = "")
    {
        _tables = tables;
        _current = current;
    }

    public object? GetValue(long fid) => null;

    private List<Rec> Table(string tableId) =>
        _tables.TryGetValue(tableId.Length == 0 ? _current : tableId, out var t) ? t : new();

    public IReadOnlyList<long> QueryRecords(string tableId, RecordQuery query) =>
        Table(tableId).Where(r => Match(r, query)).Select(r => r.Id).ToList();

    public long? FindRecordByField(string tableId, long fid, string value) =>
        Table(tableId).FirstOrDefault(r => Str(r.Fields.GetValueOrDefault(fid)) == value)?.Id;

    public bool RecordExists(string tableId, long recordId) => Table(tableId).Any(r => r.Id == recordId);

    public IReadOnlyList<object?> GetFieldValues(string tableId, IReadOnlyList<long> recordIds, long fid)
    {
        var byId = Table(tableId).ToDictionary(r => r.Id);
        return recordIds.Select(id => byId.TryGetValue(id, out var r) ? r.Fields.GetValueOrDefault(fid) : null).ToList();
    }

    private static bool Match(Rec r, RecordQuery q)
    {
        if (q.Clauses.Count == 0) return false;
        bool acc = ClauseMatch(r, q.Clauses[0]);
        for (int i = 0; i < q.Connectors.Count; i++)
        {
            bool next = ClauseMatch(r, q.Clauses[i + 1]);
            acc = q.Connectors[i] == QueryConnector.And ? acc && next : acc || next;
        }
        return acc;
    }

    private static bool ClauseMatch(Rec r, RecordQueryClause c)
    {
        var raw = r.Fields.GetValueOrDefault(c.Fid);
        var s = Str(raw);
        return c.Op switch
        {
            "EX" => s == c.Value,
            "XEX" => s != c.Value,
            "CT" => s.Contains(c.Value),
            "SW" => s.StartsWith(c.Value),
            "GT" => Num(raw) > Dec(c.Value),
            "GTE" => Num(raw) >= Dec(c.Value),
            "LT" => Num(raw) < Dec(c.Value),
            "LTE" => Num(raw) <= Dec(c.Value),
            _ => false,
        };
    }

    private static string Str(object? v) => v?.ToString() ?? string.Empty;
    private static decimal Num(object? v) => v is decimal d ? d : decimal.TryParse(Str(v), out var p) ? p : 0m;
    private static decimal Dec(string v) => decimal.TryParse(v, out var p) ? p : 0m;
}
