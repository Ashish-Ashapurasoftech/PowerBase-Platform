using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

/// <summary>A record context with no values (every field reads as null).</summary>
internal sealed class EmptyContext : IRecordContext
{
    public static readonly EmptyContext Instance = new();
    public object? GetValue(long fid) => null;
}

/// <summary>Compiles and evaluates field-free constant expressions (golden corpus).</summary>
internal static class FormulaEval
{
    private static readonly FormulaEngine Engine = new();
    private static readonly TestSchema EmptySchema = new();

    public static FormulaValue Const(string expr, EvaluationOptions? opt = null)
    {
        var compiled = Engine.Compile(expr, EmptySchema);
        compiled.HasErrors.Should().BeFalse(because: Diag(compiled));
        return Engine.Evaluate(compiled, EmptyContext.Instance, opt ?? EvaluationOptions.Default);
    }

    public static string Diag(CompiledFormula c) => string.Join("; ", c.Diagnostics.Select(d => $"{d.Code}:{d.Message}"));
}

/// <summary>Fluent harness for formulas that reference fields, with per-field stored values.</summary>
internal sealed class Bed
{
    private static readonly FormulaEngine Engine = new();
    private readonly TestSchema _schema = new();
    private readonly Dictionary<long, object?> _values = new();

    public Bed Field(string name, FormulaType type, object? value = null)
    {
        _schema.Add(name, type);
        _schema.TryResolve(name, out var f);
        _values[f.Fid] = value;
        return this;
    }

    public FormulaValue Eval(string expr, EvaluationOptions? opt = null)
    {
        var compiled = Engine.Compile(expr, _schema);
        compiled.HasErrors.Should().BeFalse(because: FormulaEval.Diag(compiled));
        return Engine.Evaluate(compiled, new DictContext(_values), opt ?? EvaluationOptions.Default);
    }

    private sealed class DictContext : IRecordContext
    {
        private readonly IReadOnlyDictionary<long, object?> _v;
        public DictContext(IReadOnlyDictionary<long, object?> v) => _v = v;
        public object? GetValue(long fid) => _v.TryGetValue(fid, out var x) ? x : null;
    }
}
