using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

/// <summary>
/// Covers the platform functions in <c>SystemFunctions</c>: AppID/URLRoot (existing), and the
/// Dbid(name) + Rurl() additions that let a URL-formula field build a link to another table's
/// Add form and return to the page that triggered evaluation.
/// </summary>
public class SystemFunctionsTests
{
    private static readonly FormulaEngine Engine = new();

    private static FormulaValue Eval(string expr, EvaluationOptions opt, IRecordContext? ctx = null)
    {
        var compiled = Engine.Compile(expr, EmptyFieldSchema.Instance);
        compiled.HasErrors.Should().BeFalse(because: FormulaEval.Diag(compiled));
        return Engine.Evaluate(compiled, ctx ?? EmptyContext.Instance, opt);
    }

    [Fact]
    public void Dbid_with_no_args_returns_the_current_table_id()
    {
        var opt = new EvaluationOptions { TableId = "current-guid" };
        Eval("Dbid()", opt).AsText().Should().Be("current-guid");
    }

    [Fact]
    public void Dbid_with_a_name_resolves_via_the_record_context()
    {
        var ctx = new NamedTableContext(new() { ["File Logs"] = "child-guid" });
        Eval("Dbid(\"File Logs\")", EvaluationOptions.Default, ctx).AsText().Should().Be("child-guid");
    }

    [Fact]
    public void Dbid_with_an_unknown_name_returns_empty_text()
    {
        var ctx = new NamedTableContext(new());
        Eval("Dbid(\"No Such Table\")", EvaluationOptions.Default, ctx).AsText().Should().Be(string.Empty);
    }

    [Fact]
    public void Dbid_with_a_name_and_no_context_override_returns_empty_text()
    {
        // The default IRecordContext.ResolveTableId implementation (EmptyContext doesn't override it) returns "".
        Eval("Dbid(\"Anything\")", EvaluationOptions.Default).AsText().Should().Be(string.Empty);
    }

    [Fact]
    public void Rurl_returns_the_configured_return_url()
    {
        var opt = new EvaluationOptions { ReturnUrl = "https://app.example.com/app/1/tables/2/records/3" };
        Eval("Rurl()", opt).AsText().Should().Be("https://app.example.com/app/1/tables/2/records/3");
    }

    [Fact]
    public void Rurl_defaults_to_empty_text()
    {
        Eval("Rurl()", EvaluationOptions.Default).AsText().Should().Be(string.Empty);
    }

    private sealed class NamedTableContext : IRecordContext
    {
        private readonly Dictionary<string, string> _byName;
        public NamedTableContext(Dictionary<string, string> byName) => _byName = byName;
        public object? GetValue(long fid) => null;
        public string ResolveTableId(string tableName) => _byName.GetValueOrDefault(tableName, string.Empty);
    }
}
