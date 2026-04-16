using System.Collections.Generic;
using System.Text;

namespace CInterpreterWpf
{
    public class Lexer
    {
        private readonly string _source;
        private int _position;
        private int _line = 1;
        private int _column = 1;

        public Lexer(string source)
        {
            _source = source;
        }

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();
            while (true)
            {
                var t = GetNextToken();
                tokens.Add(t);
                if (t.Type == TokenType.EOF) break;
            }
            return tokens;
        }

        private Token GetNextToken()
        {
            SkipWhitespaceAndComments();
            if (_position >= _source.Length) return new Token(TokenType.EOF, "", _line, _column);

            char c = CurrentChar();

            if (c == '+' && Peek() == '+') return AdvanceTwiceAndCreateToken(TokenType.Increment, "++");
            if (c == '-' && Peek() == '-') return AdvanceTwiceAndCreateToken(TokenType.Decrement, "--");
            if (c == '-' && Peek() == '>') return AdvanceTwiceAndCreateToken(TokenType.Arrow, "->");
            if (c == '+' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.PlusAssign, "+=");
            if (c == '-' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.MinusAssign, "-=");
            if (c == '*' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.AsteriskAssign, "*=");
            if (c == '/' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.SlashAssign, "/=");
            if (c == '=' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.Equal, "==");
            if (c == '!' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.NotEqual, "!=");
            if (c == '<' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.LessEqual, "<=");
            if (c == '>' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.GreaterEqual, ">=");
            if (c == '&' && Peek() == '&') return AdvanceTwiceAndCreateToken(TokenType.LogicalAnd, "&&");
            if (c == '|' && Peek() == '|') return AdvanceTwiceAndCreateToken(TokenType.LogicalOr, "||");

            switch (c)
            {
                case '=': return AdvanceAndCreateToken(TokenType.Assign, "=");
                case '+': return AdvanceAndCreateToken(TokenType.Plus, "+");
                case '-': return AdvanceAndCreateToken(TokenType.Minus, "-");
                case '*': return AdvanceAndCreateToken(TokenType.Asterisk, "*");
                case '/': return AdvanceAndCreateToken(TokenType.Slash, "/");
                case '%': return AdvanceAndCreateToken(TokenType.Percent, "%");
                case '<': return AdvanceAndCreateToken(TokenType.Less, "<");
                case '>': return AdvanceAndCreateToken(TokenType.Greater, ">");
                case '!': return AdvanceAndCreateToken(TokenType.LogicalNot, "!");
                case '(': return AdvanceAndCreateToken(TokenType.LParen, "(");
                case ')': return AdvanceAndCreateToken(TokenType.RParen, ")");
                case '{': return AdvanceAndCreateToken(TokenType.LBrace, "{");
                case '}': return AdvanceAndCreateToken(TokenType.RBrace, "}");
                case '[': return AdvanceAndCreateToken(TokenType.LBracket, "[");
                case ']': return AdvanceAndCreateToken(TokenType.RBracket, "]");
                case ';': return AdvanceAndCreateToken(TokenType.Semicolon, ";");
                case ',': return AdvanceAndCreateToken(TokenType.Comma, ",");
                case '.': return AdvanceAndCreateToken(TokenType.Dot, ".");
                case '&': return AdvanceAndCreateToken(TokenType.Ampersand, "&");
                case '\'': return ReadCharLiteral();
            }

            if (c == '"') return ReadStringLiteral();
            if (char.IsDigit(c)) return ReadNumber();
            if (char.IsLetter(c) || c == '_') return ReadIdentifierOrKeyword();

            return AdvanceAndCreateToken(TokenType.Unknown, c.ToString());
        }

        private char CurrentChar() => _position < _source.Length ? _source[_position] : '\0';
        private char Peek() => _position + 1 < _source.Length ? _source[_position + 1] : '\0';

        private void Advance()
        {
            if (CurrentChar() == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
            _position++;
        }

        private Token AdvanceAndCreateToken(TokenType type, string value)
        {
            var t = new Token(type, value, _line, _column);
            Advance();
            return t;
        }

        private Token AdvanceTwiceAndCreateToken(TokenType type, string value)
        {
            var t = new Token(type, value, _line, _column);
            Advance();
            Advance();
            return t;
        }

        private void SkipWhitespaceAndComments()
        {
            while (_position < _source.Length)
            {
                if (char.IsWhiteSpace(CurrentChar()))
                {
                    Advance();
                }
                else if (CurrentChar() == '/' && Peek() == '/')
                {
                    while (_position < _source.Length && CurrentChar() != '\n') Advance();
                }
                else
                {
                    break;
                }
            }
        }

        private Token ReadCharLiteral()
        {
            int sc = _column;
            Advance();

            char c = CurrentChar();
            Advance();

            if (CurrentChar() == '\'') Advance();

            return new Token(TokenType.CharLiteral, c.ToString(), _line, sc);
        }

        private Token ReadStringLiteral()
        {
            int sl = _line, sc = _column;
            Advance();

            var sb = new StringBuilder();
            while (CurrentChar() != '"' && _position < _source.Length)
            {
                if (CurrentChar() == '\\')
                {
                    Advance();
                    if (CurrentChar() == 'n') sb.Append('\n');
                    else sb.Append(CurrentChar());
                }
                else
                {
                    sb.Append(CurrentChar());
                }
                Advance();
            }

            if (CurrentChar() == '"') Advance();
            return new Token(TokenType.StringLiteral, sb.ToString(), sl, sc);
        }

        private Token ReadNumber()
        {
            int sc = _column;
            var sb = new StringBuilder();
            while (char.IsDigit(CurrentChar()))
            {
                sb.Append(CurrentChar());
                Advance();
            }
            return new Token(TokenType.Number, sb.ToString(), _line, sc);
        }

        private Token ReadIdentifierOrKeyword()
        {
            int sc = _column;
            var sb = new StringBuilder();
            while (char.IsLetterOrDigit(CurrentChar()) || CurrentChar() == '_')
            {
                sb.Append(CurrentChar());
                Advance();
            }

            string v = sb.ToString();
            TokenType t = v switch
            {
                "int" => TokenType.Int,
                "char" => TokenType.Char,
                "void" => TokenType.Void,
                "struct" => TokenType.Struct,
                "typedef" => TokenType.Typedef,
                "return" => TokenType.Return,
                "if" => TokenType.If,
                "else" => TokenType.Else,
                "while" => TokenType.While,
                "for" => TokenType.For,
                "do" => TokenType.Do,
                "break" => TokenType.Break,
                "continue" => TokenType.Continue,
                _ => TokenType.Identifier
            };

            return new Token(t, v, _line, sc);
        }
    }
}