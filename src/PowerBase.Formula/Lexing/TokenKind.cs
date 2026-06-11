namespace PowerBase.Formula.Lexing;

public enum TokenKind
{
    Number,
    String,
    FieldRef,
    Identifier,

    // Operators / punctuation
    Plus,
    Minus,
    Star,
    Slash,
    Caret,
    Amp,
    Equal,
    NotEqual,
    Less,
    Greater,
    LessEqual,
    GreaterEqual,
    LParen,
    RParen,
    Comma,

    // Keywords
    And,
    Or,
    Not,
    True,
    False,

    EndOfFile,
}
