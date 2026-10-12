using PowerBase.Formula.Syntax;

namespace PowerBase.Formula.Binding;

/// <summary>Finds whether a bound formula reads a field chosen per record (<c>GetFieldValue</c> with an
/// ID that is only known when the formula runs).</summary>
internal static class DynamicReferenceScanner
{
    public static bool Uses(Expr? expr) => expr switch
    {
        FunctionCallExpr c => c.IsDynamicFieldValue || c.Args.Any(Uses),
        UnaryExpr u => Uses(u.Operand),
        BinaryExpr b => Uses(b.Left) || Uses(b.Right),
        LetExpr l => l.Declarations.Any(d => Uses(d.Value)) || Uses(l.Body),
        _ => false,
    };
}
