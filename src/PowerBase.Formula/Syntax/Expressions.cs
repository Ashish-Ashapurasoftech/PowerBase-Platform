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

    /// <summary>Set by the binder when the field is an Address: its value is the whole address as text, or the part named by
    /// <see cref="AddressPart"/> (<c>[Address : Street 1]</c>).</summary>
    public bool IsAddress { get; internal set; }

    /// <summary>The stored key of the address part this reference reads (street1, street2, city, state, zip, country), or null for the whole address.</summary>
    public string? AddressPart { get; internal set; }

    /// <summary>Set by the binder when this bracket token resolved as a table alias
    /// (<c>[_DBID_FILE_TYPES]</c>) rather than a field — the target table's id, to be evaluated as
    /// a Text literal. Null for an ordinary field reference. See <see cref="Binding.ITableAliasSchema"/>.</summary>
    public string? TableAliasId { get; internal set; }

    /// <summary>Set by the binder when this bracket token resolved as an app variable rather than a field —
    /// the variable's id, and its value (always text), evaluated as a Text constant. Null for an ordinary field.</summary>
    public long? VariableId { get; internal set; }

    public string? VariableValue { get; internal set; }
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

    /// <summary>Set by the binder for <c>GetFieldValue(&lt;field id&gt;)</c> once the ID resolves to a field,
    /// so the call types and evaluates as that field. Null for every other function.</summary>
    public Binding.FieldRef? ResolvedField { get; internal set; }

    /// <summary>Set by the binder for <c>GetFieldValue(&lt;expr&gt;)</c> whose ID is only known when the
    /// formula runs (it comes from a field or a computed value). Such a call is resolved per record.</summary>
    public bool IsDynamicFieldValue { get; internal set; }
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

/// <summary>One <c>var &lt;type&gt; &lt;name&gt; = &lt;value&gt;;</c>. The declared type is enforced by the
/// type checker: the initialiser must produce that type ("Expecting number but found text").</summary>
public sealed class VariableDeclaration
{
    public VariableDeclaration(string name, string declaredType, Expr value, TextSpan span, TextSpan declaredTypeSpan = default)
    {
        Name = name;
        DeclaredType = declaredType;
        Value = value;
        Span = span;
        DeclaredTypeSpan = declaredTypeSpan;
    }

    /// <summary>Where the type keyword sits, for "unknown type" diagnostics.</summary>
    public TextSpan DeclaredTypeSpan { get; }

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
