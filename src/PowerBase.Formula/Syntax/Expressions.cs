using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Syntax;

public enum UnaryOp
{
    Negate,
    Plus,
    Not,
}

public enum BinaryOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Power,
    Concat,
    Equal,
    NotEqual,
    Less,
    Greater,
    LessEqual,
    GreaterEqual,
    And,
    Or,
}

/// <summary>A constant: number, text, or boolean literal. Its type is known at parse time.</summary>
public sealed class LiteralExpr : Expr
{
    public LiteralExpr(FormulaValue value, TextSpan span) : base(span)
    {
        Value = value;
        Type = value.Type;
    }

    public FormulaValue Value { get; }
}

/// <summary>A <c>[Field Name]</c> reference. The binder fills <see cref="Fid"/>/<see cref="Type"/>.</summary>
public sealed class FieldRefExpr : Expr
{
    public FieldRefExpr(string name, TextSpan span) : base(span) => Name = name;

    public string Name { get; }

    public long Fid { get; internal set; }

    public bool IsBound { get; internal set; }
}

public sealed class UnaryExpr : Expr
{
    public UnaryExpr(UnaryOp op, Expr operand, TextSpan span) : base(span)
    {
        Op = op;
        Operand = operand;
    }

    public UnaryOp Op { get; }

    public Expr Operand { get; }
}

public sealed class BinaryExpr : Expr
{
    public BinaryExpr(BinaryOp op, Expr left, Expr right, TextSpan span) : base(span)
    {
        Op = op;
        Left = left;
        Right = right;
    }

    public BinaryOp Op { get; }

    public Expr Left { get; }

    public Expr Right { get; }
}

public sealed class FunctionCallExpr : Expr
{
    public FunctionCallExpr(string name, IReadOnlyList<Expr> args, TextSpan span) : base(span)
    {
        Name = name;
        Args = args;
    }

    public string Name { get; }

    public IReadOnlyList<Expr> Args { get; }
}

/// <summary>A placeholder produced during error recovery; always types as Null.</summary>
public sealed class ErrorExpr : Expr
{
    public ErrorExpr(TextSpan span) : base(span) => Type = FormulaType.Null;
}
