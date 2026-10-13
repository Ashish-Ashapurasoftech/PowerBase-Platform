using PowerBase.Formula.Binding;
using PowerBase.Formula.Checking;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Syntax;
using PowerBase.Formula.Types;

namespace PowerBase.Formula;

/// <summary>
/// Public entry point to the formula engine. Stateless and thread-safe; share a
/// single instance. Pipeline: Lex → Parse → Bind → TypeCheck (→
/// <see cref="CompiledFormula"/>), then <see cref="Evaluate"/> per record.
/// </summary>
public sealed class FormulaEngine
{
    private readonly IFunctionRegistry _functions;

    public FormulaEngine(IFunctionRegistry? functions = null) => _functions = functions ?? FunctionRegistry.Builtin;

    /// <summary>
    /// Compile an expression against a field schema. When <paramref name="expectedType"/>
    /// is supplied (e.g. a formula field's declared result type), a mismatch is
    /// reported as a diagnostic. <paramref name="aliasSchema"/> lets <c>[_DBID_*]</c> bracket
    /// tokens resolve as table-alias references (see <see cref="ITableAliasSchema"/>) instead of
    /// unknown fields — omit it for callers with no table-alias concept to offer. Always returns
    /// a <see cref="CompiledFormula"/>; inspect <see cref="CompiledFormula.HasErrors"/> before
    /// evaluating.
    /// </summary>
    public CompiledFormula Compile(string expression, IFieldSchema schema, FormulaType? expectedType = null, ITableAliasSchema? aliasSchema = null, IVariableSchema? variableSchema = null)
    {
        var parse = Parser.Parse(expression);
        var diagnostics = new List<FormulaDiagnostic>(parse.Diagnostics);

        var referencedFids = Binder.Bind(parse.Root, schema, diagnostics, aliasSchema, variableSchema);
        var resultType = new TypeChecker(_functions, diagnostics).Check(parse.Root);

        if (expectedType is { } expected && resultType != FormulaType.Null && resultType != expected)
        {
            diagnostics.Add(new FormulaDiagnostic(
                FormulaErrorCode.ResultTypeMismatch,
                $"Formula returns {resultType} but {expected} is required.",
                parse.Root.Span));
        }

        return new CompiledFormula(expression, resultType, parse.Root, diagnostics, referencedFids,
            expectedType, Binding.DynamicReferenceScanner.Uses(parse.Root));
    }

    /// <summary>Compile and return only the diagnostics — drives the authoring UI's inline errors.</summary>
    public IReadOnlyList<FormulaDiagnostic> Validate(string expression, IFieldSchema schema, FormulaType? expectedType = null, ITableAliasSchema? aliasSchema = null, IVariableSchema? variableSchema = null)
        => Compile(expression, schema, expectedType, aliasSchema, variableSchema).Diagnostics;

    /// <summary>Evaluate a previously compiled formula against one record's values.</summary>
    /// <exception cref="Evaluation.FormulaEvaluationException">If the formula has compile errors.</exception>
    public FormulaValue Evaluate(CompiledFormula compiled, IRecordContext context, EvaluationOptions options)
    {
        if (compiled.Root is null || compiled.HasErrors)
            throw new Evaluation.FormulaEvaluationException("Formula has errors and cannot be evaluated.");

        // A formula that reads a field chosen per record couldn't have its types fully checked when it was
        // compiled, so they are verified as it runs. Every other formula takes the unchanged path.
        if (!compiled.UsesDynamicFieldReference)
            return new Evaluator(_functions).Eval(compiled.Root, context, options);

        try
        {
            var value = new Evaluator(_functions, verifyTypes: true).Eval(compiled.Root, context, options);
            if (compiled.ExpectedType is { } expected && value.Type != FormulaType.Null && value.Type != expected)
                throw new Evaluation.FormulaEvaluationException($"Formula returns {value.Type} but {expected} is required.");
            return value;
        }
        catch (InvalidCastException)
        {
            // A value of an unexpected type reached something that assumes another; never let that escape as a crash.
            throw new Evaluation.FormulaEvaluationException("A value has a different type than this formula expects.");
        }
    }
}
