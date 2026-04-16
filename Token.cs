namespace CInterpreterWpf
{
    public enum TokenType
    {
        Int, Char, Void, Typedef,Struct, Return,
        Short, Long, Float, Double,
        If, Else, While, For, Do, Break, Continue,
        Identifier, Number, StringLiteral, FloatLiteral,CharLiteral,

        Assign, Plus, Minus, Asterisk, Slash, Percent,
        PlusAssign, MinusAssign, AsteriskAssign, SlashAssign,
        Increment, Decrement,
        Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
        LogicalAnd, LogicalOr, LogicalNot,

        LParen, RParen, LBrace, RBrace, LBracket, RBracket,
        Semicolon, Comma, Dot, Arrow,
        Ampersand, EOF, Unknown
    }

    public class Token
    {
        public TokenType Type { get; }
        public string Value { get; }
        public int Line { get; }
        public int Column { get; }

        public Token(TokenType type, string value, int line, int column)
        {
            Type = type;
            Value = value;
            Line = line;
            Column = column;
        }

        public override string ToString() => $"L{Line:D2}:C{Column:D2} | {Type,-15} | '{Value}'";
    }
}