using System.Globalization;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Syntax;

/// <summary>
/// Renders an AST as a fully-parenthesized infix string. Used by tests to assert
/// parse structure (precedence/associativity) without reaching into node fields.
/// </summary>
public static class AstPrinter
{
    public static string Print(Expr expr) => expr switch
    {
        LiteralExpr l => PrintLiteral(l.Value),
        FieldRefExpr f => $"[{f.Name}]",
        UnaryExpr u => $"({UnarySym(u.Op)} {Print(u.Operand)})",
        BinaryExpr b => $"({Print(b.Left)} {BinarySym(b.Op)} {Print(b.Right)})",
        FunctionCallExpr c => $"{c.Name}({string.Join(", ", c.Args.Select(Print))})",
        ErrorExpr => "<error>",
        _ => "<?>",
    };

    private static string PrintLiteral(FormulaValue v) => v.Type switch
    {
        FormulaType.Number => v.AsNumber().ToString(CultureInfo.InvariantCulture),
        FormulaType.Text => "\"" + v.AsText() + "\"",
        FormulaType.Bool => v.AsBool() ? "true" : "false",
        _ => v.ToString(),
    };

    private static string UnarySym(UnaryOp op) => op switch
    {
        UnaryOp.Negate => "-",
        UnaryOp.Plus => "+",
        UnaryOp.Not => "not",
        _ => "?",
    };

    private static string BinarySym(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Power => "^",
        BinaryOp.Concat => "&",
        BinaryOp.Equal => "=",
        BinaryOp.NotEqual => "<>",
        BinaryOp.Less => "<",
        BinaryOp.Greater => ">",
        BinaryOp.LessEqual => "<=",
        BinaryOp.GreaterEqual => ">=",
        BinaryOp.And => "and",
        BinaryOp.Or => "or",
        _ => "?",
    };
}
