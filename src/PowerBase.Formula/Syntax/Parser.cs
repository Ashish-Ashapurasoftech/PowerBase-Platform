using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Lexing;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Syntax;

public sealed record ParseResult(Expr Root, IReadOnlyList<FormulaDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == FormulaSeverity.Error);
}

/// <summary>
/// Recursive-descent parser. Precedence, tightest-binding first:
/// unary (+ - not) &gt; ^ (right-assoc) &gt; * / &gt; + - &amp; &gt; comparison (&lt; &gt; &lt;= &gt;=)
/// &gt; equality (= &lt;&gt; !=) &gt; and &gt; or. Matches Quickbase, including the quirks
/// that <c>&amp;</c> sits at additive level, <c>^</c> is right-associative, and
/// <c>or</c> is the loosest operator. Recovers from errors by emitting a diagnostic
/// and continuing, so multiple problems surface in one pass.
/// </summary>
public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private readonly List<FormulaDiagnostic> _diags;
    private int _pos;

    private Parser(IReadOnlyList<Token> tokens, List<FormulaDiagnostic> diags)
    {
        _tokens = tokens;
        _diags = diags;
    }

    public static ParseResult Parse(string? source)
    {
        var (tokens, lexDiags) = new Lexer(source).Tokenize();
        var diags = new List<FormulaDiagnostic>(lexDiags);
        var root = new Parser(tokens, diags).ParseRoot();
        return new ParseResult(root, diags);
    }

    private Token Current => _tokens[_pos];

    private Token Advance() => _tokens[_pos++];

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private bool Match(TokenKind kind)
    {
        if (!Check(kind)) return false;
        _pos++;
        return true;
    }

    private void Expect(TokenKind kind, string what)
    {
        if (Check(kind)) { _pos++; return; }
        _diags.Add(new FormulaDiagnostic(FormulaErrorCode.ExpectedToken, $"Expected '{what}'.", Current.Span));
    }

    private static TextSpan Span(Expr left, Expr right) => TextSpan.FromBounds(left.Span.Start, right.Span.End);

    private Expr ParseRoot()
    {
        if (Check(TokenKind.EndOfFile))
        {
            _diags.Add(new FormulaDiagnostic(FormulaErrorCode.EmptyExpression, "Formula is empty.", Current.Span));
            return new ErrorExpr(Current.Span);
        }

        var expr = ParseOr();
        if (!Check(TokenKind.EndOfFile))
        {
            _diags.Add(new FormulaDiagnostic(FormulaErrorCode.UnexpectedToken, $"Unexpected '{Current.Text}'.", Current.Span));
        }

        return expr;
    }

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (Check(TokenKind.Or))
        {
            Advance();
            var right = ParseAnd();
            left = new BinaryExpr(BinaryOp.Or, left, right, Span(left, right));
        }
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseEquality();
        while (Check(TokenKind.And))
        {
            Advance();
            var right = ParseEquality();
            left = new BinaryExpr(BinaryOp.And, left, right, Span(left, right));
        }
        return left;
    }

    private Expr ParseEquality()
    {
        var left = ParseComparison();
        while (Check(TokenKind.Equal) || Check(TokenKind.NotEqual))
        {
            var op = Advance().Kind == TokenKind.Equal ? BinaryOp.Equal : BinaryOp.NotEqual;
            var right = ParseComparison();
            left = new BinaryExpr(op, left, right, Span(left, right));
        }
        return left;
    }

    private Expr ParseComparison()
    {
        var left = ParseAdditive();
        while (Check(TokenKind.Less) || Check(TokenKind.Greater) || Check(TokenKind.LessEqual) || Check(TokenKind.GreaterEqual))
        {
            var op = Advance().Kind switch
            {
                TokenKind.Less => BinaryOp.Less,
                TokenKind.Greater => BinaryOp.Greater,
                TokenKind.LessEqual => BinaryOp.LessEqual,
                _ => BinaryOp.GreaterEqual,
            };
            var right = ParseAdditive();
            left = new BinaryExpr(op, left, right, Span(left, right));
        }
        return left;
    }

    private Expr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Check(TokenKind.Plus) || Check(TokenKind.Minus) || Check(TokenKind.Amp))
        {
            var op = Advance().Kind switch
            {
                TokenKind.Plus => BinaryOp.Add,
                TokenKind.Minus => BinaryOp.Subtract,
                _ => BinaryOp.Concat,
            };
            var right = ParseMultiplicative();
            left = new BinaryExpr(op, left, right, Span(left, right));
        }
        return left;
    }

    private Expr ParseMultiplicative()
    {
        var left = ParsePower();
        while (Check(TokenKind.Star) || Check(TokenKind.Slash))
        {
            var op = Advance().Kind == TokenKind.Star ? BinaryOp.Multiply : BinaryOp.Divide;
            var right = ParsePower();
            left = new BinaryExpr(op, left, right, Span(left, right));
        }
        return left;
    }

    // Right-associative; binds looser than unary so -2^2 parses as (-2)^2.
    private Expr ParsePower()
    {
        var left = ParseUnary();
        if (Check(TokenKind.Caret))
        {
            Advance();
            var right = ParsePower();
            return new BinaryExpr(BinaryOp.Power, left, right, Span(left, right));
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (Check(TokenKind.Minus) || Check(TokenKind.Plus) || Check(TokenKind.Not))
        {
            var opTok = Advance();
            var op = opTok.Kind switch
            {
                TokenKind.Minus => UnaryOp.Negate,
                TokenKind.Plus => UnaryOp.Plus,
                _ => UnaryOp.Not,
            };
            var operand = ParseUnary();
            return new UnaryExpr(op, operand, TextSpan.FromBounds(opTok.Span.Start, operand.Span.End));
        }
        return ParsePrimary();
    }

    private Expr ParsePrimary()
    {
        var tok = Current;
        switch (tok.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new LiteralExpr(FormulaValue.Number((decimal)tok.Value!), tok.Span);
            case TokenKind.String:
                Advance();
                return new LiteralExpr(FormulaValue.Text((string)tok.Value!), tok.Span);
            case TokenKind.True:
                Advance();
                return new LiteralExpr(FormulaValue.Bool(true), tok.Span);
            case TokenKind.False:
                Advance();
                return new LiteralExpr(FormulaValue.Bool(false), tok.Span);
            case TokenKind.FieldRef:
                Advance();
                return new FieldRefExpr((string)tok.Value!, tok.Span);
            case TokenKind.LParen:
                Advance();
                var inner = ParseOr();
                Expect(TokenKind.RParen, ")");
                return inner;
            case TokenKind.Identifier:
                return ParseFunctionCall();
            default:
                _diags.Add(new FormulaDiagnostic(
                    FormulaErrorCode.UnexpectedToken,
                    tok.Kind == TokenKind.EndOfFile ? "Unexpected end of formula." : $"Unexpected '{tok.Text}'.",
                    tok.Span));
                if (tok.Kind != TokenKind.EndOfFile) Advance(); // guarantee progress
                return new ErrorExpr(tok.Span);
        }
    }

    private Expr ParseFunctionCall()
    {
        var name = Advance(); // identifier
        if (!Match(TokenKind.LParen))
        {
            _diags.Add(new FormulaDiagnostic(FormulaErrorCode.ExpectedToken, $"Expected '(' after '{name.Text}'.", Current.Span));
            return new ErrorExpr(name.Span);
        }

        var args = new List<Expr>();
        if (!Check(TokenKind.RParen))
        {
            do
            {
                args.Add(ParseOr());
            }
            while (Match(TokenKind.Comma));
        }

        Expect(TokenKind.RParen, ")");
        var endPos = _tokens[_pos - 1].Span.End;
        return new FunctionCallExpr(name.Text, args, TextSpan.FromBounds(name.Span.Start, endPos));
    }
}
