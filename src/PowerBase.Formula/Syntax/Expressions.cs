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

/// <summary>A reference to a variable declared earlier in the same formula, written <c>$name</c>.</summary>
public sealed class VariableRefExpr : Expr
{
    public VariableRefExpr(string name, TextSpan span) : base(span) => Name = name;

    /// <summary>The name without the '$'.</summary>
    public string Name { get; }
}

/// <summary>
/// A formula written as a series of <c>var</c> declarations followed by the expression that
/// produces the result — Quickbase's shape for anything long enough to need naming its parts:
/// <code>var number tax = [Total] * 0.2; [Total] + $tax</code>
/// Each declaration is visible to the ones after it and to the final expression, and the whole
/// thing types as that final expression.
/// </summary>
public sealed class LetExpr : Expr
{
    public LetExpr(IReadOnlyList<VariableDeclaration> declarations, Expr body, TextSpan span) : base(span)
    {
        Declarations = declarations;
        Body = body;
    }

    public IReadOnlyList<VariableDeclaration> Declarations { get; }

    public Expr Body { get; }
}

/// <summary>One <c>var &lt;type&gt; &lt;name&gt; = &lt;value&gt;;</c>. The declared type is recorded for
/// diagnostics but the initialiser's own type is what the variable actually carries — Quickbase
/// is permissive here, and rejecting a mismatch would fail formulas it accepts.</summary>
public sealed class VariableDeclaration
{
    public VariableDeclaration(string name, string declaredType, Expr value, TextSpan span)
    {
        Name = name;
        DeclaredType = declaredType;
        Value = value;
        Span = span;
    }

    public string Name { get; }

    public string DeclaredType { get; }

    public Expr Value { get; }

    public TextSpan Span { get; }
}

/// <summary>A placeholder produced during error recovery; always types as Null.</summary>
public sealed class ErrorExpr : Expr
{
    public ErrorExpr(TextSpan span) : base(span) => Type = FormulaType.Null;
}
