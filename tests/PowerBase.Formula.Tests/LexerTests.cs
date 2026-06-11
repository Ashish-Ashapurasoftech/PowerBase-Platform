using FluentAssertions;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Lexing;

namespace PowerBase.Formula.Tests;

public class LexerTests
{
    private static IReadOnlyList<Token> Lex(string src) => new Lexer(src).Tokenize().Tokens;

    private static IReadOnlyList<FormulaDiagnostic> Diags(string src) => new Lexer(src).Tokenize().Diagnostics;

    [Fact]
    public void Tokenizes_arithmetic()
    {
        var kinds = Lex("1 + 2").Select(t => t.Kind);
        kinds.Should().Equal(TokenKind.Number, TokenKind.Plus, TokenKind.Number, TokenKind.EndOfFile);
    }

    [Fact]
    public void Number_decodes_decimal_value()
    {
        var tok = Lex("12.5").First();
        tok.Kind.Should().Be(TokenKind.Number);
        tok.Value.Should().Be(12.5m);
    }

    [Fact]
    public void String_decodes_escaped_quote()
    {
        // source: "a\"b"  → value: a"b
        var tok = Lex("\"a\\\"b\"").First();
        tok.Kind.Should().Be(TokenKind.String);
        tok.Value.Should().Be("a\"b");
    }

    [Fact]
    public void FieldRef_decodes_name_with_spaces()
    {
        var tok = Lex("[First Name]").First();
        tok.Kind.Should().Be(TokenKind.FieldRef);
        tok.Value.Should().Be("First Name");
    }

    [Fact]
    public void FieldRef_decodes_escaped_bracket()
    {
        // source: [a\]b] → name: a]b
        var tok = Lex("[a\\]b]").First();
        tok.Kind.Should().Be(TokenKind.FieldRef);
        tok.Value.Should().Be("a]b");
    }

    [Fact]
    public void Keywords_are_recognized_case_insensitively()
    {
        Lex("AND or Not TRUE false").Select(t => t.Kind).Should().Equal(
            TokenKind.And, TokenKind.Or, TokenKind.Not, TokenKind.True, TokenKind.False, TokenKind.EndOfFile);
    }

    [Fact]
    public void Line_comment_is_skipped()
    {
        Lex("1 // a comment\n+ 2").Select(t => t.Kind).Should().Equal(
            TokenKind.Number, TokenKind.Plus, TokenKind.Number, TokenKind.EndOfFile);
    }

    [Fact]
    public void Multi_char_operators_are_recognized()
    {
        Lex("<= >= <> != = < >").Select(t => t.Kind).Should().Equal(
            TokenKind.LessEqual, TokenKind.GreaterEqual, TokenKind.NotEqual, TokenKind.NotEqual,
            TokenKind.Equal, TokenKind.Less, TokenKind.Greater, TokenKind.EndOfFile);
    }

    [Fact]
    public void Unterminated_string_reports_diagnostic()
    {
        Diags("\"abc").Should().ContainSingle(d => d.Code == FormulaErrorCode.UnterminatedString);
    }

    [Fact]
    public void Unexpected_character_reports_diagnostic()
    {
        Diags("1 @ 2").Should().ContainSingle(d => d.Code == FormulaErrorCode.UnexpectedCharacter);
    }
}
