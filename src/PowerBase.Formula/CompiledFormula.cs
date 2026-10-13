using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Syntax;
using PowerBase.Formula.Types;

namespace PowerBase.Formula;

/// <summary>
/// The result of compiling a formula: an immutable, cacheable artifact holding the
/// bound/typed AST, the declared result type, the set of fields it reads, and any
/// diagnostics. Compile once per (field, expression); evaluate many times.
/// </summary>
public sealed class CompiledFormula
{
    internal CompiledFormula(
        string expression,
        FormulaType resultType,
        Expr? root,
        IReadOnlyList<FormulaDiagnostic> diagnostics,
        IReadOnlyList<long> referencedFieldIds,
        FormulaType? expectedType = null,
        bool usesDynamicFieldReference = false)
    {
        Expression = expression;
        ResultType = resultType;
        Root = root;
        Diagnostics = diagnostics;
        ReferencedFieldIds = referencedFieldIds;
        ExpectedType = expectedType;
        UsesDynamicFieldReference = usesDynamicFieldReference;
    }

    /// <summary>The result type the formula was compiled to produce, if one was required.</summary>
    internal FormulaType? ExpectedType { get; }

    /// <summary>True when the formula reads a field chosen per record (<c>GetFieldValue</c> with an ID that
    /// is only known when it runs). Such a formula's real dependencies can't be listed in
    /// <see cref="ReferencedFieldIds"/>, and its types are verified as it runs.</summary>
    public bool UsesDynamicFieldReference { get; }

    public string Expression { get; }

    /// <summary>The declared result type of the whole formula.</summary>
    public FormulaType ResultType { get; }

    public IReadOnlyList<FormulaDiagnostic> Diagnostics { get; }

    /// <summary>Fids this formula reads — the dependency list a future recalc layer needs.</summary>
    public IReadOnlyList<long> ReferencedFieldIds { get; }

    public bool HasErrors => Diagnostics.Any(d => d.Severity == FormulaSeverity.Error);

    /// <summary>The bound AST root. Null only when compilation failed before producing a tree.</summary>
    internal Expr? Root { get; }
}
