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
    Semicolon,

    /// <summary>A reference to a declared variable, e.g. <c>$total</c>. Text is the name without the '$'.</summary>
    VariableRef,

    // Keywords
    And,
    Or,
    Not,
    True,
    False,
    Var,

    EndOfFile,
}
