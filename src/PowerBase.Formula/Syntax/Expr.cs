using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Syntax;

/// <summary>
/// Base of the formula AST. After binding/type-checking each node carries its
/// resolved <see cref="Type"/>; concrete node kinds are added in the parser step.
/// </summary>
public abstract class Expr
{
    protected Expr(TextSpan span) => Span = span;

    public TextSpan Span { get; }

    /// <summary>Resolved type, assigned by the type checker. <see cref="FormulaType.Null"/> until then.</summary>
    public FormulaType Type { get; internal set; } = FormulaType.Null;
}
