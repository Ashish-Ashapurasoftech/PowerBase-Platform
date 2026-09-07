using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Binding;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

/// <summary>
/// A table alias resolves inside brackets exactly like a field — <c>[_DBID_FILE_TYPES]</c> —
/// but binds to a Text constant carrying the target table's id, not a field read. See
/// PowerBase.Formula.Binding.Binder.Resolve.
/// </summary>
public class TableAliasBindingTests
{
    private static readonly FormulaEngine Engine = new();

    private static TestSchema Schema() => new TestSchema().Add("Status", FormulaType.Text);

    private sealed class TestAliasSchema : ITableAliasSchema
    {
        private readonly Dictionary<string, string> _byAlias = new(StringComparer.OrdinalIgnoreCase);
        public TestAliasSchema Add(string alias, string tableId) { _byAlias[alias] = tableId; return this; }
        public bool TryResolve(string alias, out string tableId) => _byAlias.TryGetValue(alias, out tableId!);
    }

    [Fact]
    public void Known_alias_resolves_as_text_constant()
    {
        var aliasSchema = new TestAliasSchema().Add("_DBID_FILE_TYPES", "table-guid-123");
        var compiled = Engine.Compile("[_DBID_FILE_TYPES]", Schema(), aliasSchema: aliasSchema);

        compiled.HasErrors.Should().BeFalse();
        compiled.ResultType.Should().Be(FormulaType.Text);

        var value = Engine.Evaluate(compiled, new EmptyRecordContext(), new PowerBase.Formula.Evaluation.EvaluationOptions());
        value.AsText().Should().Be("table-guid-123");
    }

    [Fact]
    public void Unresolved_alias_without_schema_reports_unknown_field()
    {
        Engine.Compile("[_DBID_FILE_TYPES]", Schema())
            .Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.UnknownField);
    }

    [Fact]
    public void Unresolved_alias_with_schema_reports_unknown_field()
    {
        var aliasSchema = new TestAliasSchema().Add("_DBID_OTHER", "table-guid-456");
        Engine.Compile("[_DBID_FILE_TYPES]", Schema(), aliasSchema: aliasSchema)
            .Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.UnknownField);
    }

    [Fact]
    public void Real_field_wins_over_an_alias_of_the_same_name()
    {
        // A field literally named "_DBID_FILE_TYPES" (unusual, but possible) always resolves as
        // the field — table aliases only apply when there's no field match.
        var schema = new TestSchema().Add("_DBID_FILE_TYPES", FormulaType.Number);
        var aliasSchema = new TestAliasSchema().Add("_DBID_FILE_TYPES", "table-guid-123");

        var compiled = Engine.Compile("[_DBID_FILE_TYPES]", schema, aliasSchema: aliasSchema);
        compiled.HasErrors.Should().BeFalse();
        compiled.ResultType.Should().Be(FormulaType.Number);
        compiled.ReferencedFieldIds.Should().HaveCount(1);
    }
}

/// <summary>A record context with no field values — enough to evaluate an expression that never
/// reads a field (a bare resolved table-alias constant).</summary>
public sealed class EmptyRecordContext : PowerBase.Formula.Evaluation.IRecordContext
{
    public object? GetValue(long fid) => null;
}
