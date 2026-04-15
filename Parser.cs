using System;
using System.Collections.Generic;

namespace CInterpreterWpf
{
    public class Parser
    {
        private readonly List<Token> _tokens;
        private int _position;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
            _position = 0;
        }

        private Token CurrentToken => _position < _tokens.Count ? _tokens[_position] : new Token(TokenType.EOF, "", 0, 0);
        private Token PeekToken(int offset = 1) => _position + offset < _tokens.Count ? _tokens[_position + offset] : new Token(TokenType.EOF, "", 0, 0);
        private Token Consume() => _tokens[_position++];

        private Token Expect(TokenType t)
        {
            if (CurrentToken.Type == t) return Consume();
            throw new Exception($"Expected {t}, but got {CurrentToken.Type} at line {CurrentToken.Line}, column {CurrentToken.Column}");
        }

        public ProgramNode Parse()
        {
            var p = new ProgramNode();
            while (CurrentToken.Type != TokenType.EOF)
                p.Declarations.Add(ParseFunctionDeclaration());
            return p;
        }

        private IASTNode ParseFunctionDeclaration()
        {
            var fn = new FunctionDeclNode
            {
                ReturnType = Consume().Value,
                Name = Expect(TokenType.Identifier).Value
            };

            Expect(TokenType.LParen);

            if (CurrentToken.Type != TokenType.RParen)
            {
                fn.Parameters.Add(ParseFunctionParameter());
                while (CurrentToken.Type == TokenType.Comma)
                {
                    Consume();
                    fn.Parameters.Add(ParseFunctionParameter());
                }
            }

            Expect(TokenType.RParen);
            Expect(TokenType.LBrace);

            while (CurrentToken.Type != TokenType.RBrace)
                fn.Body.Add(ParseStatement());

            Expect(TokenType.RBrace);
            return fn;
        }

        private FunctionParameter ParseFunctionParameter()
        {
            if (CurrentToken.Type != TokenType.Int && CurrentToken.Type != TokenType.Char)
                throw new Exception($"Parameter type must be int or char at line {CurrentToken.Line}, column {CurrentToken.Column}");

            var param = new FunctionParameter
            {
                Type = Consume().Value
            };

            if (CurrentToken.Type == TokenType.Asterisk)
            {
                Consume();
                param.IsPointer = true;
            }

            param.Name = Expect(TokenType.Identifier).Value;
            return param;
        }

        private IASTNode ParseStatement()
        {
            if (CurrentToken.Type == TokenType.LBrace)
                return ParseBlock();

            if (CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char)
                return ParseVariableDeclaration(true);

            if (CurrentToken.Type == TokenType.If)
                return ParseIfStatement();

            if (CurrentToken.Type == TokenType.While)
                return ParseWhileStatement();

            if (CurrentToken.Type == TokenType.Do)
                return ParseDoWhileStatement();

            if (CurrentToken.Type == TokenType.For)
                return ParseForStatement();

            if (CurrentToken.Type == TokenType.Break)
            {
                Consume();
                Expect(TokenType.Semicolon);
                return new BreakNode();
            }

            if (CurrentToken.Type == TokenType.Continue)
            {
                Consume();
                Expect(TokenType.Semicolon);
                return new ContinueNode();
            }

            if (CurrentToken.Type == TokenType.Return)
            {
                Consume();
                var r = new ReturnNode { Value = ParseExpression() };
                Expect(TokenType.Semicolon);
                return r;
            }

            if (IsStartOfAssignment())
                return ParseAssignmentStatement(true);

            if (CurrentToken.Type == TokenType.Identifier && PeekToken().Type == TokenType.LParen)
                return ParseFunctionCallStatement();

            if (IsStartOfIncDecStatement())
            {
                var expr = ParseExpression();
                Expect(TokenType.Semicolon);
                return expr;
            }

            throw new Exception($"Unknown statement at line {CurrentToken.Line}, column {CurrentToken.Column}");
        }

        private bool IsStartOfAssignment()
        {
            if (CurrentToken.Type == TokenType.Asterisk)
                return true;

            if (CurrentToken.Type != TokenType.Identifier)
                return false;

            if (PeekToken().Type == TokenType.Assign ||
                PeekToken().Type == TokenType.PlusAssign ||
                PeekToken().Type == TokenType.MinusAssign ||
                PeekToken().Type == TokenType.AsteriskAssign ||
                PeekToken().Type == TokenType.SlashAssign)
                return true;

            if (PeekToken().Type != TokenType.LBracket)
                return false;

            int pos = _position + 1;
            int depth = 0;
            while (pos < _tokens.Count)
            {
                var t = _tokens[pos];
                if (t.Type == TokenType.LBracket) depth++;
                else if (t.Type == TokenType.RBracket)
                {
                    depth--;
                    if (depth == 0)
                    {
                        pos++;
                        break;
                    }
                }
                pos++;
            }

            if (pos < _tokens.Count)
            {
                var after = _tokens[pos].Type;
                return after == TokenType.Assign ||
                       after == TokenType.PlusAssign ||
                       after == TokenType.MinusAssign ||
                       after == TokenType.AsteriskAssign ||
                       after == TokenType.SlashAssign;
            }

            return false;
        }

        private bool IsStartOfIncDecStatement()
        {
            if (CurrentToken.Type == TokenType.Increment || CurrentToken.Type == TokenType.Decrement)
                return true;

            if (CurrentToken.Type != TokenType.Identifier)
                return false;

            if (PeekToken().Type == TokenType.Increment || PeekToken().Type == TokenType.Decrement)
                return true;

            if (PeekToken().Type != TokenType.LBracket)
                return false;

            int pos = _position + 1;
            int depth = 0;
            while (pos < _tokens.Count)
            {
                var t = _tokens[pos];
                if (t.Type == TokenType.LBracket) depth++;
                else if (t.Type == TokenType.RBracket)
                {
                    depth--;
                    if (depth == 0)
                    {
                        pos++;
                        break;
                    }
                }
                pos++;
            }

            if (pos < _tokens.Count)
            {
                var after = _tokens[pos].Type;
                return after == TokenType.Increment || after == TokenType.Decrement;
            }

            return false;
        }

        private BlockNode ParseBlock()
        {
            var block = new BlockNode();
            Expect(TokenType.LBrace);

            while (CurrentToken.Type != TokenType.RBrace)
                block.Statements.Add(ParseStatement());

            Expect(TokenType.RBrace);
            return block;
        }

        private IASTNode ParseVariableDeclaration(bool expectSemicolon)
        {
            var v = new VarDeclNode { Type = Consume().Value };

            if (CurrentToken.Type == TokenType.Asterisk)
            {
                Consume();
                v.IsPointer = true;
            }

            v.VarName = Expect(TokenType.Identifier).Value;

            if (CurrentToken.Type == TokenType.LBracket)
            {
                if (v.IsPointer)
                    throw new Exception("Pointer arrays are not supported yet");

                Consume();

                if (CurrentToken.Type == TokenType.RBracket)
                {
                    v.IsArray = true;
                    v.IsArrayLengthInferred = true;
                    v.ArrayLength = 0;
                    Consume();
                }
                else
                {
                    var lengthNode = ParseExpression();
                    if (lengthNode is not NumberNode len || len.Value <= 0)
                        throw new Exception("Array length must be a positive integer literal");

                    v.IsArray = true;
                    v.ArrayLength = len.Value;
                    Expect(TokenType.RBracket);
                }

                if (CurrentToken.Type == TokenType.Assign)
                {
                    Consume();

                    if (CurrentToken.Type == TokenType.LBrace)
                    {
                        v.Initializer = ParseArrayInitializer();
                    }
                    else if (CurrentToken.Type == TokenType.StringLiteral && v.Type == "char")
                    {
                        v.Initializer = ParseExpression();
                    }
                    else
                    {
                        throw new Exception("Array initializer must be {...} or string literal for char array");
                    }
                }
                else if (v.IsArrayLengthInferred)
                {
                    throw new Exception("Array length may be omitted only when initializer is provided");
                }
            }
            else
            {
                Expect(TokenType.Assign);
                v.Initializer = ParseExpression();
            }

            if (expectSemicolon)
                Expect(TokenType.Semicolon);

            return v;
        }

        private ArrayInitializerNode ParseArrayInitializer()
        {
            var init = new ArrayInitializerNode();
            Expect(TokenType.LBrace);

            if (CurrentToken.Type != TokenType.RBrace)
            {
                init.Elements.Add(ParseExpression());
                while (CurrentToken.Type == TokenType.Comma)
                {
                    Consume();
                    if (CurrentToken.Type == TokenType.RBrace) break;
                    init.Elements.Add(ParseExpression());
                }
            }

            Expect(TokenType.RBrace);
            return init;
        }

        private IASTNode ParseAssignmentStatement(bool expectSemicolon)
        {
            IASTNode left;

            if (CurrentToken.Type == TokenType.Asterisk)
            {
                Consume();
                left = new UnaryOpNode
                {
                    Operator = "*",
                    Target = ParseAssignableTarget()
                };
            }
            else
            {
                left = ParseAssignableTarget();
            }

            string op = CurrentToken.Type switch
            {
                TokenType.Assign => Consume().Value,
                TokenType.PlusAssign => Consume().Value,
                TokenType.MinusAssign => Consume().Value,
                TokenType.AsteriskAssign => Consume().Value,
                TokenType.SlashAssign => Consume().Value,
                _ => throw new Exception($"Expected assignment operator at line {CurrentToken.Line}, column {CurrentToken.Column}")
            };

            var a = new AssignmentNode
            {
                Left = left,
                Operator = op,
                Right = ParseExpression()
            };

            if (expectSemicolon)
                Expect(TokenType.Semicolon);

            return a;
        }

        private IASTNode ParseAssignableTarget()
        {
            var node = ParsePrimary();

            while (CurrentToken.Type == TokenType.LBracket)
            {
                Consume();
                var index = ParseExpression();
                Expect(TokenType.RBracket);

                node = new ArrayAccessNode
                {
                    Target = node,
                    Index = index
                };
            }

            if (node is VariableNode || node is ArrayAccessNode)
                return node;

            throw new Exception($"Invalid assignment target at line {CurrentToken.Line}, column {CurrentToken.Column}");
        }

        private IASTNode ParseFunctionCallStatement()
        {
            var c = ParseFunctionCallExpression();
            Expect(TokenType.Semicolon);
            return c;
        }

        private FunctionCallNode ParseFunctionCallExpression()
        {
            var c = new FunctionCallNode { FunctionName = Expect(TokenType.Identifier).Value };
            Expect(TokenType.LParen);

            if (CurrentToken.Type != TokenType.RParen)
            {
                c.Arguments.Add(ParseExpression());
                while (CurrentToken.Type == TokenType.Comma)
                {
                    Consume();
                    c.Arguments.Add(ParseExpression());
                }
            }

            Expect(TokenType.RParen);
            return c;
        }

        private IASTNode ParseIfStatement()
        {
            Expect(TokenType.If);
            Expect(TokenType.LParen);
            var condition = ParseExpression();
            Expect(TokenType.RParen);

            var thenBranch = ParseStatement();
            IASTNode elseBranch = null;

            if (CurrentToken.Type == TokenType.Else)
            {
                Consume();
                elseBranch = ParseStatement();
            }

            return new IfNode
            {
                Condition = condition,
                ThenBranch = thenBranch,
                ElseBranch = elseBranch
            };
        }

        private IASTNode ParseWhileStatement()
        {
            Expect(TokenType.While);
            Expect(TokenType.LParen);
            var condition = ParseExpression();
            Expect(TokenType.RParen);

            return new WhileNode
            {
                Condition = condition,
                Body = ParseStatement()
            };
        }

        private IASTNode ParseDoWhileStatement()
        {
            Expect(TokenType.Do);
            var body = ParseStatement();
            Expect(TokenType.While);
            Expect(TokenType.LParen);
            var condition = ParseExpression();
            Expect(TokenType.RParen);
            Expect(TokenType.Semicolon);

            return new DoWhileNode
            {
                Body = body,
                Condition = condition
            };
        }

        private IASTNode ParseForStatement()
        {
            Expect(TokenType.For);
            Expect(TokenType.LParen);

            IASTNode initializer = null;
            if (CurrentToken.Type != TokenType.Semicolon)
            {
                if (CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char)
                    initializer = ParseVariableDeclaration(false);
                else if (IsStartOfAssignment())
                    initializer = ParseAssignmentStatement(false);
                else
                    initializer = ParseExpression();
            }
            Expect(TokenType.Semicolon);

            IASTNode condition = null;
            if (CurrentToken.Type != TokenType.Semicolon)
                condition = ParseExpression();
            Expect(TokenType.Semicolon);

            IASTNode increment = null;
            if (CurrentToken.Type != TokenType.RParen)
            {
                if (IsStartOfAssignment())
                    increment = ParseAssignmentStatement(false);
                else
                    increment = ParseExpression();
            }
            Expect(TokenType.RParen);

            return new ForNode
            {
                Initializer = initializer,
                Condition = condition,
                Increment = increment,
                Body = ParseStatement()
            };
        }

        private IASTNode ParseExpression()
        {
            return ParseLogicalOr();
        }

        private IASTNode ParseLogicalOr()
        {
            var node = ParseLogicalAnd();

            while (CurrentToken.Type == TokenType.LogicalOr)
            {
                node = new BinaryOpNode
                {
                    Left = node,
                    Operator = Consume().Value,
                    Right = ParseLogicalAnd()
                };
            }

            return node;
        }

        private IASTNode ParseLogicalAnd()
        {
            var node = ParseEquality();

            while (CurrentToken.Type == TokenType.LogicalAnd)
            {
                node = new BinaryOpNode
                {
                    Left = node,
                    Operator = Consume().Value,
                    Right = ParseEquality()
                };
            }

            return node;
        }

        private IASTNode ParseEquality()
        {
            var node = ParseComparison();

            while (CurrentToken.Type == TokenType.Equal || CurrentToken.Type == TokenType.NotEqual)
            {
                node = new BinaryOpNode
                {
                    Left = node,
                    Operator = Consume().Value,
                    Right = ParseComparison()
                };
            }

            return node;
        }

        private IASTNode ParseComparison()
        {
            var node = ParseAdditive();

            while (CurrentToken.Type == TokenType.Less ||
                   CurrentToken.Type == TokenType.LessEqual ||
                   CurrentToken.Type == TokenType.Greater ||
                   CurrentToken.Type == TokenType.GreaterEqual)
            {
                node = new BinaryOpNode
                {
                    Left = node,
                    Operator = Consume().Value,
                    Right = ParseAdditive()
                };
            }

            return node;
        }

        private IASTNode ParseAdditive()
        {
            var node = ParseTerm();

            while (CurrentToken.Type == TokenType.Plus || CurrentToken.Type == TokenType.Minus)
            {
                node = new BinaryOpNode
                {
                    Left = node,
                    Operator = Consume().Value,
                    Right = ParseTerm()
                };
            }

            return node;
        }

        private IASTNode ParseTerm()
        {
            var node = ParseUnary();

            while (CurrentToken.Type == TokenType.Asterisk ||
                   CurrentToken.Type == TokenType.Slash ||
                   CurrentToken.Type == TokenType.Percent)
            {
                node = new BinaryOpNode
                {
                    Left = node,
                    Operator = Consume().Value,
                    Right = ParseUnary()
                };
            }

            return node;
        }

        private IASTNode ParseUnary()
        {
            if (CurrentToken.Type == TokenType.Increment ||
                CurrentToken.Type == TokenType.Decrement ||
                CurrentToken.Type == TokenType.Ampersand ||
                CurrentToken.Type == TokenType.Asterisk ||
                CurrentToken.Type == TokenType.Minus ||
                CurrentToken.Type == TokenType.LogicalNot)
            {
                string op = Consume().Value;
                return new UnaryOpNode
                {
                    Operator = op,
                    Target = ParseUnary()
                };
            }

            return ParsePostfix();
        }

        private IASTNode ParsePostfix()
        {
            var node = ParsePrimary();

            while (true)
            {
                if (CurrentToken.Type == TokenType.LBracket)
                {
                    Consume();
                    var index = ParseExpression();
                    Expect(TokenType.RBracket);

                    node = new ArrayAccessNode
                    {
                        Target = node,
                        Index = index
                    };
                    continue;
                }

                if (CurrentToken.Type == TokenType.Increment || CurrentToken.Type == TokenType.Decrement)
                {
                    node = new PostfixOpNode
                    {
                        Target = node,
                        Operator = Consume().Value
                    };
                    continue;
                }

                break;
            }

            return node;
        }

        private IASTNode ParsePrimary()
        {
            if (CurrentToken.Type == TokenType.Number)
                return new NumberNode { Value = int.Parse(Consume().Value) };

            if (CurrentToken.Type == TokenType.StringLiteral)
                return new StringNode { Value = Consume().Value };

            if (CurrentToken.Type == TokenType.CharLiteral)
                return new CharLiteralNode { Value = Consume().Value[0] };

            if (CurrentToken.Type == TokenType.Identifier)
            {
                if (PeekToken().Type == TokenType.LParen)
                    return ParseFunctionCallExpression();

                return new VariableNode { Name = Consume().Value };
            }

            if (CurrentToken.Type == TokenType.LParen)
            {
                Consume();
                var n = ParseExpression();
                Expect(TokenType.RParen);
                return n;
            }

            throw new Exception($"Unexpected token in expression: {CurrentToken.Type} at line {CurrentToken.Line}, column {CurrentToken.Column}");
        }
    }
}