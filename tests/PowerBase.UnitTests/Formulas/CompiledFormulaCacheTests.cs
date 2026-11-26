using System.Diagnostics;
using FluentAssertions;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Types;
using Xunit;
using Xunit.Abstractions;

namespace PowerBase.UnitTests.Formulas;

/// <summary>FRM-01: one compile serves every later read with the same inputs; any change the binder would see gives a new compile.</summary>
public class CompiledFormulaCacheTests(ITestOutputHelper output)
{
    private static readonly FormulaEngine Engine = new();

    private static List<AppField> Fields(string qtyLabel = "Qty") =>
    [
        new() { Id = 1, Fid = 6, Name = "Qty", Label = qtyLabel, TypeCode = "Number" },
        new() { Id = 2, Fid = 7, Name = "Price", TypeCode = "Currency" },
        new() { Id = 3, Fid = 8, Name = "Region", TypeCode = "Text" },
    ];

    private const string Expr = "If([Qty] > 5, [Qty] * [Price] * 0.9, [Qty] * [Price])";

    [Fact]
    public void Same_inputs_share_one_compile()
    {
        var a = CompiledFormulaCache.GetOrCompile(Engine, Expr, new AppFieldSchema(Fields()), FormulaType.Number);
        var b = CompiledFormulaCache.GetOrCompile(Engine, Expr, new AppFieldSchema(Fields()), FormulaType.Number);
        b.Should().BeSameAs(a);
        a.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void A_renamed_field_or_another_type_compiles_again()
    {
        var a = CompiledFormulaCache.GetOrCompile(Engine, "[Quantity] * 2", new AppFieldSchema(Fields("Quantity")), FormulaType.Number);
        var renamed = CompiledFormulaCache.GetOrCompile(Engine, "[Quantity] * 2", new AppFieldSchema(Fields("Units")), FormulaType.Number);
        a.HasErrors.Should().BeFalse();
        renamed.HasErrors.Should().BeTrue("[Quantity] no longer exists: a stale compile would have hidden that");

        var asText = CompiledFormulaCache.GetOrCompile(Engine, "[Region]", new AppFieldSchema(Fields()), FormulaType.Text);
        var asNumber = CompiledFormulaCache.GetOrCompile(Engine, "[Region]", new AppFieldSchema(Fields()), FormulaType.Number);
        asNumber.Should().NotBeSameAs(asText);
    }

    [Fact]
    public void A_changed_variable_value_compiles_again()
    {
        var v1 = new AppVariableSchema([new AppVariable { Id = 5, Name = "Tax", Value = "0.18" }]);
        var v2 = new AppVariableSchema([new AppVariable { Id = 5, Name = "Tax", Value = "0.12" }]);
        var a = CompiledFormulaCache.GetOrCompile(Engine, "[Tax]", new AppFieldSchema(Fields()), FormulaType.Text, v1);
        var b = CompiledFormulaCache.GetOrCompile(Engine, "[Tax]", new AppFieldSchema(Fields()), FormulaType.Text, v2);
        b.Should().NotBeSameAs(a);
    }

    [Fact]
    public void Measured_compile_versus_cache_hit_is_reported_not_asserted()
    {
        var schema = new AppFieldSchema(Fields());
        const int n = 2000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++) Engine.Compile(Expr, schema, FormulaType.Number);
        var compileMs = sw.Elapsed.TotalMilliseconds;
        CompiledFormulaCache.GetOrCompile(Engine, Expr, schema, FormulaType.Number);
        sw.Restart();
        for (var i = 0; i < n; i++) CompiledFormulaCache.GetOrCompile(Engine, Expr, new AppFieldSchema(Fields()), FormulaType.Number);
        var cachedMs = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"compile x{n}: {compileMs:F1} ms ({compileMs * 1000 / n:F1} µs each); cache hit incl. new schema x{n}: {cachedMs:F1} ms ({cachedMs * 1000 / n:F1} µs each)");
    }
}
