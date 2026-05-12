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
            _source = Preprocessor.Process(source);
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

            if (c == '<' && Peek() == '<' && Peek(2) == '=') return AdvanceManyAndCreateToken(TokenType.ShiftLeftAssign, "<<=", 3);
            if (c == '>' && Peek() == '>' && Peek(2) == '=') return AdvanceManyAndCreateToken(TokenType.ShiftRightAssign, ">>=", 3);
            if (c == '+' && Peek() == '+') return AdvanceTwiceAndCreateToken(TokenType.Increment, "++");
            if (c == '-' && Peek() == '-') return AdvanceTwiceAndCreateToken(TokenType.Decrement, "--");
            if (c == '-' && Peek() == '>') return AdvanceTwiceAndCreateToken(TokenType.Arrow, "->");
            if (c == '+' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.PlusAssign, "+=");
            if (c == '-' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.MinusAssign, "-=");
            if (c == '*' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.AsteriskAssign, "*=");
            if (c == '/' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.SlashAssign, "/=");
            if (c == '%' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.PercentAssign, "%=");
            if (c == '&' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.AmpersandAssign, "&=");
            if (c == '|' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.BitwiseOrAssign, "|=");
            if (c == '^' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.BitwiseXorAssign, "^=");
            if (c == '=' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.Equal, "==");
            if (c == '!' && Peek() == '=') return AdvanceTwiceAndCreateToken(TokenType.NotEqual, "!=");
            if (c == '<' && Peek() == '<') return AdvanceTwiceAndCreateToken(TokenType.ShiftLeft, "<<");
            if (c == '>' && Peek() == '>') return AdvanceTwiceAndCreateToken(TokenType.ShiftRight, ">>");
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
                case '|': return AdvanceAndCreateToken(TokenType.BitwiseOr, "|");
                case '^': return AdvanceAndCreateToken(TokenType.BitwiseXor, "^");
                case '~': return AdvanceAndCreateToken(TokenType.BitwiseNot, "~");
                case '(': return AdvanceAndCreateToken(TokenType.LParen, "(");
                case ')': return AdvanceAndCreateToken(TokenType.RParen, ")");
                case '{': return AdvanceAndCreateToken(TokenType.LBrace, "{");
                case '}': return AdvanceAndCreateToken(TokenType.RBrace, "}");
                case '[': return AdvanceAndCreateToken(TokenType.LBracket, "[");
                case ']': return AdvanceAndCreateToken(TokenType.RBracket, "]");
                case ';': return AdvanceAndCreateToken(TokenType.Semicolon, ";");
                case ',': return AdvanceAndCreateToken(TokenType.Comma, ",");
                case '.': return AdvanceAndCreateToken(TokenType.Dot, ".");
                case '?': return AdvanceAndCreateToken(TokenType.Question, "?");
                case ':': return AdvanceAndCreateToken(TokenType.Colon, ":");
                case '&': return AdvanceAndCreateToken(TokenType.Ampersand, "&");
                case '\'': return ReadCharLiteral();
            }

            if (c == '"') return ReadStringLiteral();
            if (char.IsDigit(c)) return ReadNumber();
            if (char.IsLetter(c) || c == '_') return ReadIdentifierOrKeyword();

            return AdvanceAndCreateToken(TokenType.Unknown, c.ToString());
        }

        private char CurrentChar() => _position < _source.Length ? _source[_position] : '\0';
        private char Peek(int offset = 1) => _position + offset < _source.Length ? _source[_position + offset] : '\0';

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

        private Token AdvanceManyAndCreateToken(TokenType type, string value, int count)
        {
            var t = new Token(type, value, _line, _column);
            for (int i = 0; i < count; i++)
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
                else if (CurrentChar() == '/' && Peek() == '*')
                {
                    Advance();
                    Advance();

                    while (_position < _source.Length)
                    {
                        if (CurrentChar() == '*' && Peek() == '/')
                        {
                            Advance();
                            Advance();
                            break;
                        }

                        Advance();
                    }

                    if (_position >= _source.Length)
                        throw new Exception("Unterminated block comment");
                }
                else
                {
                    break;
                }
            }
        }

        private Token ReadCharLiteral()
        {
            int sl = _line, sc = _column;
            Advance();

            if (_position >= _source.Length || CurrentChar() == '\n')
                throw new Exception($"Unterminated char literal at line {sl}, column {sc}");

            char c = ReadEscapedOrCurrentChar();

            if (CurrentChar() != '\'')
                throw new Exception($"Unterminated char literal at line {sl}, column {sc}");

            Advance();

            return new Token(TokenType.CharLiteral, c.ToString(), sl, sc);
        }

        private Token ReadStringLiteral()
        {
            int sl = _line, sc = _column;
            Advance();

            var sb = new StringBuilder();
            while (CurrentChar() != '"' && _position < _source.Length)
            {
                if (CurrentChar() == '\n')
                    throw new Exception($"Unterminated string literal at line {sl}, column {sc}");

                sb.Append(ReadEscapedOrCurrentChar());
            }

            if (CurrentChar() != '"')
                throw new Exception($"Unterminated string literal at line {sl}, column {sc}");

            Advance();
            return new Token(TokenType.StringLiteral, sb.ToString(), sl, sc);
        }

        private char ReadEscapedOrCurrentChar()
        {
            if (CurrentChar() != '\\')
            {
                char c = CurrentChar();
                Advance();
                return c;
            }

            Advance();
            if (_position >= _source.Length)
                return '\\';

            char escaped = CurrentChar();
            Advance();

            return escaped switch
            {
                '0' => '\0',
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',
                '\\' => '\\',
                '\'' => '\'',
                '"' => '"',
                _ => escaped
            };
        }

        private Token ReadNumber()
        {
            int sc = _column;
            var sb = new StringBuilder();
            bool hasDot = false;

            if (CurrentChar() == '0' && (Peek() == 'x' || Peek() == 'X'))
            {
                sb.Append(CurrentChar());
                Advance();
                sb.Append(CurrentChar());
                Advance();

                while (_position < _source.Length && IsHexDigit(CurrentChar()))
                {
                    sb.Append(CurrentChar());
                    Advance();
                }

                if (sb.Length == 2)
                    return new Token(TokenType.Unknown, sb.ToString(), _line, sc);

                return new Token(TokenType.Number, sb.ToString(), _line, sc);
            }

            // 数字、またはまだ一度も出てきていない小数点を許容
            while (_position < _source.Length && (char.IsDigit(CurrentChar()) || (!hasDot && CurrentChar() == '.')))
            {
                if (CurrentChar() == '.') hasDot = true;
                sb.Append(CurrentChar());
                Advance();
            }

            return new Token(hasDot ? TokenType.FloatLiteral : TokenType.Number, sb.ToString(), _line, sc);
        }

        private static bool IsHexDigit(char c)
        {
            return char.IsDigit(c) ||
                   (c >= 'a' && c <= 'f') ||
                   (c >= 'A' && c <= 'F');
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
                "short" => TokenType.Short,   
                "long" => TokenType.Long,     
                "float" => TokenType.Float,   
                "double" => TokenType.Double,
                "void" => TokenType.Void,
                "struct" => TokenType.Struct,
                "enum" => TokenType.Enum,
                "typedef" => TokenType.Typedef,
                "return" => TokenType.Return,
                "sizeof" => TokenType.Sizeof,
                "if" => TokenType.If,
                "else" => TokenType.Else,
                "while" => TokenType.While,
                "for" => TokenType.For,
                "do" => TokenType.Do,
                "switch" => TokenType.Switch,
                "case" => TokenType.Case,
                "default" => TokenType.Default,
                "break" => TokenType.Break,
                "continue" => TokenType.Continue,
                _ => TokenType.Identifier
            };

            return new Token(t, v, _line, sc);
        }
    }
}
