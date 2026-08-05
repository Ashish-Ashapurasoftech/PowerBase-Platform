using System.Globalization;
using System.Text;
using PowerBase.Formula.Diagnostics;

namespace PowerBase.Formula.Lexing;

/// <summary>
/// Hand-rolled lexer for the formula language. Skips whitespace and <c>//</c>
/// line comments, decodes literals/field references (resolving <c>\</c> escapes),
/// and reports lexical problems as diagnostics while still producing a token
/// stream so the parser can recover and surface multiple errors at once.
/// </summary>
public sealed class Lexer
{
    private readonly string _src;
    private readonly List<FormulaDiagnostic> _diags = new();
    private int _pos;

    public Lexer(string? source) => _src = source ?? string.Empty;

    public (IReadOnlyList<Token> Tokens, IReadOnlyList<FormulaDiagnostic> Diagnostics) Tokenize()
    {
        var tokens = new List<Token>();
        Token t;
        do
        {
            t = NextToken();
            tokens.Add(t);
        }
        while (t.Kind != TokenKind.EndOfFile);
        return (tokens, _diags);
    }

    private bool AtEnd => _pos >= _src.Length;
    private char Current => _pos < _src.Length ? _src[_pos] : '\0';
    private char Peek(int n = 1) => _pos + n < _src.Length ? _src[_pos + n] : '\0';

    private Token NextToken()
    {
        SkipTrivia();
        if (AtEnd) return new Token(TokenKind.EndOfFile, string.Empty, new TextSpan(_pos, 0));

        int start = _pos;
        char c = Current;

        if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek()))) return LexNumber(start);
        if (c == '"') return LexString(start);
        if (c == '[') return LexFieldRef(start);
        if (char.IsLetter(c) || c == '_') return LexIdentifier(start);
        if (c == '$') return LexVariableRef(start);
        return LexOperator(start);
    }

    private void SkipTrivia()
    {
        while (!AtEnd)
        {
            char c = Current;
            if (char.IsWhiteSpace(c)) { _pos++; continue; }
            if (c == '/' && Peek() == '/')
            {
                _pos += 2;
                while (!AtEnd && Current != '\n') _pos++;
                continue;
            }
            break;
        }
    }

    private Token LexNumber(int start)
    {
        while (!AtEnd && char.IsDigit(Current)) _pos++;
        if (Current == '.')
        {
            _pos++;
            while (!AtEnd && char.IsDigit(Current)) _pos++;
        }

        var text = _src.Substring(start, _pos - start);
        var span = TextSpan.FromBounds(start, _pos);
        var value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
        return new Token(TokenKind.Number, text, span, value);
    }

    private Token LexString(int start)
    {
        _pos++; // opening quote
        var sb = new StringBuilder();
        while (!AtEnd && Current != '"')
        {
            if (Current == '\\' && _pos + 1 < _src.Length)
            {
                _pos++;
                sb.Append(Current); // literal next char: handles \" \\ \[ \]
                _pos++;
            }
            else
            {
                sb.Append(Current);
                _pos++;
            }
        }

        if (AtEnd)
        {
            var sp = TextSpan.FromBounds(start, _pos);
            _diags.Add(new FormulaDiagnostic(FormulaErrorCode.UnterminatedString, "Unterminated text literal (missing closing \").", sp));
            return new Token(TokenKind.String, _src.Substring(start), sp, sb.ToString());
        }

        _pos++; // closing quote
        return new Token(TokenKind.String, _src.Substring(start, _pos - start), TextSpan.FromBounds(start, _pos), sb.ToString());
    }

    private Token LexFieldRef(int start)
    {
        _pos++; // opening [
        var sb = new StringBuilder();
        while (!AtEnd && Current != ']')
        {
            if (Current == '\\' && _pos + 1 < _src.Length)
            {
                _pos++;
                sb.Append(Current); // literal next char: handles \] \[ \\
                _pos++;
            }
            else
            {
                sb.Append(Current);
                _pos++;
            }
        }

        if (AtEnd)
        {
            var sp = TextSpan.FromBounds(start, _pos);
            _diags.Add(new FormulaDiagnostic(FormulaErrorCode.UnterminatedFieldReference, "Unterminated field reference (missing closing ]).", sp));
            return new Token(TokenKind.FieldRef, _src.Substring(start), sp, sb.ToString().Trim());
        }

        _pos++; // closing ]
        return new Token(TokenKind.FieldRef, _src.Substring(start, _pos - start), TextSpan.FromBounds(start, _pos), sb.ToString().Trim());
    }

    private Token LexIdentifier(int start)
    {
        while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_')) _pos++;
        var text = _src.Substring(start, _pos - start);
        var span = TextSpan.FromBounds(start, _pos);
        var kind = text.ToLowerInvariant() switch
        {
            "and" => TokenKind.And,
            "or" => TokenKind.Or,
            "not" => TokenKind.Not,
            "true" => TokenKind.True,
            "false" => TokenKind.False,
            "var" => TokenKind.Var,
            _ => TokenKind.Identifier,
        };
        return new Token(kind, text, span);
    }

    /// <summary>A '$' introduces a reference to a declared variable. The name is lexed without the
    /// sigil so it matches the identifier the declaration bound.</summary>
    private Token LexVariableRef(int start)
    {
        _pos++; // '$'
        var nameStart = _pos;
        while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_')) _pos++;

        var span = TextSpan.FromBounds(start, _pos);
        if (_pos == nameStart)
        {
            _diags.Add(new FormulaDiagnostic(FormulaErrorCode.UnexpectedCharacter, "Expected a variable name after '$'.", span));
            return new Token(TokenKind.VariableRef, string.Empty, span);
        }

        return new Token(TokenKind.VariableRef, _src[nameStart.._pos], span);
    }

    private Token LexOperator(int start)
    {
        char c = Current;
        switch (c)
        {
            case '+': return Single(TokenKind.Plus, start);
            case '-': return Single(TokenKind.Minus, start);
            case '*': return Single(TokenKind.Star, start);
            case '/': return Single(TokenKind.Slash, start);
            case '^': return Single(TokenKind.Caret, start);
            case '&': return Single(TokenKind.Amp, start);
            case '(': return Single(TokenKind.LParen, start);
            case ')': return Single(TokenKind.RParen, start);
            case ';': return Single(TokenKind.Semicolon, start);
            case ',': return Single(TokenKind.Comma, start);
            case '=': return Single(TokenKind.Equal, start);
            case '<':
                if (Peek() == '>') return Double(TokenKind.NotEqual, start);
                if (Peek() == '=') return Double(TokenKind.LessEqual, start);
                return Single(TokenKind.Less, start);
            case '>':
                if (Peek() == '=') return Double(TokenKind.GreaterEqual, start);
                return Single(TokenKind.Greater, start);
            case '!':
                if (Peek() == '=') return Double(TokenKind.NotEqual, start);
                break;
        }

        // Unknown character: record and skip so lexing can continue.
        _pos++;
        _diags.Add(new FormulaDiagnostic(FormulaErrorCode.UnexpectedCharacter, $"Unexpected character '{c}'.", TextSpan.FromBounds(start, _pos)));
        return NextToken();
    }

    private Token Single(TokenKind kind, int start)
    {
        _pos++;
        return new Token(kind, _src.Substring(start, 1), TextSpan.FromBounds(start, _pos));
    }

    private Token Double(TokenKind kind, int start)
    {
        _pos += 2;
        return new Token(kind, _src.Substring(start, 2), TextSpan.FromBounds(start, _pos));
    }
}
