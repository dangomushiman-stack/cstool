using System;
using System.Collections.Generic;

namespace CInterpreterWpf
{
    public class Parser
    {
        private readonly List<Token> _tokens;
        private int _position;

        // --- typedef用の型記憶辞書を追加 ---
        private readonly Dictionary<string, TypeInfo> _typedefs = new Dictionary<string, TypeInfo>();

        private class TypeInfo
        {
            public string Type { get; set; }
            public bool IsStruct { get; set; }
            public string StructName { get; set; }
            public bool IsPointer { get; set; }
        }

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

        private bool IsAssignmentOperator(TokenType t)
        {
            return t == TokenType.Assign ||
                   t == TokenType.PlusAssign ||
                   t == TokenType.MinusAssign ||
                   t == TokenType.AsteriskAssign ||
                   t == TokenType.SlashAssign;
        }

        public ProgramNode Parse()
        {
            var p = new ProgramNode();

            while (CurrentToken.Type != TokenType.EOF)
            {
                if (CurrentToken.Type == TokenType.Typedef)
                {
                    // typedef は構造体定義を含む可能性があるため、プログラムノードを渡す
                    p.Declarations.Add(ParseTypedef(p));
                }
                else if (CurrentToken.Type == TokenType.Struct &&
                        PeekToken().Type == TokenType.Identifier &&
                        PeekToken(2).Type == TokenType.LBrace)
                {
                    p.Declarations.Add(ParseStructDeclaration());
                }
                else if (IsTopLevelVariableDeclaration())
                {
                    p.Declarations.Add(ParseVariableDeclaration(true));
                }
                else
                {
                    p.Declarations.Add(ParseFunctionDeclaration());
                }
            }

            return p;
        }

        private IASTNode ParseTypedef(ProgramNode program)
        {
            Expect(TokenType.Typedef);
            var info = new TypeInfo();
            StructDeclNode structDef = null;

            if (CurrentToken.Type == TokenType.Struct)
            {
                Consume();
                info.IsStruct = true;
                info.Type = "struct";

                // 1. 構造体名がある場合 (typedef struct Point ...)
                if (CurrentToken.Type == TokenType.Identifier)
                {
                    info.StructName = Consume().Value;
                }

                // 2. 構造体の定義本体がある場合 (typedef struct Point { int x; int y; } ...)
                if (CurrentToken.Type == TokenType.LBrace)
                {
                    structDef = new StructDeclNode { Name = info.StructName };
                    Consume(); // {
                    while (CurrentToken.Type != TokenType.RBrace)
                    {
                        structDef.Fields.Add(ParseStructField());
                    }
                    Expect(TokenType.RBrace);

                    // 匿名構造体 (typedef struct { ... } Alias;) の場合
                    // 内部管理用にエイリアス名を構造体名として後でセットする
                }
            }
            else if (CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char || CurrentToken.Type == TokenType.Void)
            {
                info.Type = Consume().Value;
            }
            else if (CurrentToken.Type == TokenType.Identifier && _typedefs.TryGetValue(CurrentToken.Value, out var existing))
            {
                Consume();
                info.Type = existing.Type;
                info.IsStruct = existing.IsStruct;
                info.StructName = existing.StructName;
                info.IsPointer = existing.IsPointer;
            }

            // ポインタ指定の処理 (typedef struct Point { ... } *PointerPtr;)
            if (CurrentToken.Type == TokenType.Asterisk)
            {
                Consume();
                info.IsPointer = true;
            }

            // エイリアス名 (Pointer)
            string alias = Expect(TokenType.Identifier).Value;
            Expect(TokenType.Semicolon);

            // 匿名構造体だった場合、エイリアス名を構造体名として定義する
            if (structDef != null)
            {
                if (string.IsNullOrEmpty(structDef.Name))
                {
                    structDef.Name = alias;
                    info.StructName = alias;
                }
                // Evaluatorが構造体のサイズやフィールドを計算できるようにDeclarationsに追加
                program.Declarations.Add(structDef);
            }

            // 型辞書に登録
            _typedefs[alias] = info;

            return new TypedefNode { AliasName = alias };
        }

        private bool IsTopLevelVariableDeclaration()
        {
            int saved = _position;
            try
            {
                if (CurrentToken.Type == TokenType.Struct)
                {
                    Consume();
                    Expect(TokenType.Identifier);
                    if (CurrentToken.Type == TokenType.Asterisk) Consume();
                    Expect(TokenType.Identifier);
                    if (CurrentToken.Type == TokenType.LParen) return false;
                    return true;
                }

                if (CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char)
                {
                    Consume();
                    if (CurrentToken.Type == TokenType.Asterisk) Consume();
                    Expect(TokenType.Identifier);
                    if (CurrentToken.Type == TokenType.LParen) return false;
                    return true;
                }

                // typedefで定義された型名かどうか
                if (CurrentToken.Type == TokenType.Identifier && _typedefs.ContainsKey(CurrentToken.Value))
                {
                    Consume();
                    if (CurrentToken.Type == TokenType.Asterisk) Consume();
                    Expect(TokenType.Identifier);
                    if (CurrentToken.Type == TokenType.LParen) return false;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                _position = saved;
            }
        }

        private StructDeclNode ParseStructDeclaration()
        {
            Expect(TokenType.Struct);
            var node = new StructDeclNode
            {
                Name = Expect(TokenType.Identifier).Value
            };

            Expect(TokenType.LBrace);
            while (CurrentToken.Type != TokenType.RBrace)
                node.Fields.Add(ParseStructField());

            Expect(TokenType.RBrace);
            Expect(TokenType.Semicolon);
            return node;
        }

        private StructFieldDecl ParseStructField()
        {
            var field = new StructFieldDecl();

            if (CurrentToken.Type == TokenType.Struct)
            {
                Consume();
                field.IsStruct = true;
                field.StructName = Expect(TokenType.Identifier).Value;
                field.Type = "struct";
            }
            else if (CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char)
            {
                field.Type = Consume().Value;
            }
            else if (CurrentToken.Type == TokenType.Identifier && _typedefs.TryGetValue(CurrentToken.Value, out var td))
            {
                Consume();
                field.Type = td.Type;
                field.IsStruct = td.IsStruct;
                field.StructName = td.StructName;
                field.IsPointer = td.IsPointer;
            }
            else
            {
                throw new Exception($"Struct field type error at line {CurrentToken.Line}");
            }

            if (CurrentToken.Type == TokenType.Asterisk)
            {
                Consume();
                field.IsPointer = true;
            }

            field.Name = Expect(TokenType.Identifier).Value;
            Expect(TokenType.Semicolon);
            return field;
        }

        private IASTNode ParseFunctionDeclaration()
        {
            var fn = new FunctionDeclNode();

            if (CurrentToken.Type == TokenType.Struct)
            {
                Consume();
                fn.ReturnType = "struct";
                fn.ReturnIsStruct = true;
                fn.ReturnStructName = Expect(TokenType.Identifier).Value;
            }
            else if (CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char || CurrentToken.Type == TokenType.Void)
            {
                fn.ReturnType = Consume().Value;
            }
            else if (CurrentToken.Type == TokenType.Identifier && _typedefs.TryGetValue(CurrentToken.Value, out var td))
            {
                Consume();
                fn.ReturnType = td.Type;
                fn.ReturnIsStruct = td.IsStruct;
                fn.ReturnStructName = td.StructName;
                fn.ReturnIsPointer = td.IsPointer; // typedefからの引き継ぎ
            }
            else
            {
                throw new Exception($"Invalid function return type at line {CurrentToken.Line}, column {CurrentToken.Column}");
            }

            if (CurrentToken.Type == TokenType.Asterisk)
            {
                Consume();
                fn.ReturnIsPointer = true;
            }

            if (fn.ReturnIsStruct && !fn.ReturnIsPointer)
                throw new Exception("Struct return type is not supported yet");

            fn.Name = Expect(TokenType.Identifier).Value;

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
            var param = new FunctionParameter();

            if (CurrentToken.Type == TokenType.Struct)
            {
                Consume();
                param.IsStruct = true;
                param.StructName = Expect(TokenType.Identifier).Value;
                param.Type = "struct";
            }
            else if (CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char)
            {
                param.Type = Consume().Value;
            }
            else if (CurrentToken.Type == TokenType.Identifier && _typedefs.TryGetValue(CurrentToken.Value, out var td))
            {
                Consume();
                param.Type = td.Type;
                param.IsStruct = td.IsStruct;
                param.StructName = td.StructName;
                param.IsPointer = td.IsPointer;
            }
            else
            {
                throw new Exception($"Parameter type error at line {CurrentToken.Line}");
            }

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

            // 型名による変数宣言の判定 (struct, int, char または typedef済の識別子)
            if (CurrentToken.Type == TokenType.Struct || CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char || 
               (CurrentToken.Type == TokenType.Identifier && _typedefs.ContainsKey(CurrentToken.Value)))
            {
                return ParseVariableDeclaration(true);
            }

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
            if (CurrentToken.Type == TokenType.Typedef)
            {
                // ローカルなtypedefにも対応（ただし、今回の簡易Evaluatorではグローバル扱いに近い挙動になります）
                // 本来は現在のスコープに閉じるべきですが、簡易化のためParserレベルで解決します。
                // ここでは便宜上、ダミーのProgramNodeを渡すか、共通の辞書を更新します。
                // 今回はトップレベルでの使用を想定し、既存のParse()の流れを優先してください。
                return ParseTypedef(null); 
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

        // --- 統合・強化された変数宣言パース ---
        private VarDeclNode ParseVariableDeclaration(bool expectSemicolon)
        {
            var v = new VarDeclNode();

            if (CurrentToken.Type == TokenType.Struct)
            {
                Consume();
                v.Type = "struct";
                v.IsStruct = true;
                v.StructName = Expect(TokenType.Identifier).Value;
            }
            else if (CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char)
            {
                v.Type = Consume().Value;
            }
            else if (CurrentToken.Type == TokenType.Identifier && _typedefs.TryGetValue(CurrentToken.Value, out var td))
            {
                Consume();
                v.Type = td.Type;
                v.IsStruct = td.IsStruct;
                v.StructName = td.StructName;
                v.IsPointer = td.IsPointer;
            }
            else
            {
                throw new Exception($"Expected type at line {CurrentToken.Line}");
            }

            if (CurrentToken.Type == TokenType.Asterisk)
            {
                Consume();
                v.IsPointer = true;
            }

            v.VarName = Expect(TokenType.Identifier).Value;

            if (CurrentToken.Type == TokenType.LBracket)
            {
                if (v.IsPointer) throw new Exception("Pointer arrays are not supported yet");

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
                    if (v.IsStruct) {
                        throw new Exception("Struct array initializer is not supported yet");
                    } else if (CurrentToken.Type == TokenType.LBrace) {
                        v.Initializer = ParseArrayInitializer();
                    } else if (CurrentToken.Type == TokenType.StringLiteral && v.Type == "char") {
                        v.Initializer = ParseExpression();
                    } else {
                        throw new Exception("Array initializer must be {...} or string literal for char array");
                    }
                }
                else if (v.IsArrayLengthInferred)
                {
                    throw new Exception("Array length may be omitted only when initializer is provided");
                }
            }
            else if (CurrentToken.Type == TokenType.Assign)
            {
                Consume();
                if (v.IsStruct && !v.IsPointer)
                {
                    if (CurrentToken.Type == TokenType.LBrace)
                        v.Initializer = ParseStructInitializer();
                    else
                        throw new Exception("Struct initializer must be {...}");
                }
                else
                {
                    v.Initializer = ParseExpression();
                }
            }

            if (expectSemicolon)
                Expect(TokenType.Semicolon);

            return v;
        }

        private StructInitializerNode ParseStructInitializer()
        {
            var init = new StructInitializerNode();
            Expect(TokenType.LBrace);

            if (CurrentToken.Type != TokenType.RBrace)
            {
                init.Elements.Add(ParseStructInitializerElement());
                while (CurrentToken.Type == TokenType.Comma)
                {
                    Consume();
                    if (CurrentToken.Type == TokenType.RBrace) break;
                    init.Elements.Add(ParseStructInitializerElement());
                }
            }

            Expect(TokenType.RBrace);
            return init;
        }

        private IASTNode ParseStructInitializerElement()
        {
            if (CurrentToken.Type == TokenType.LBrace)
                return ParseStructInitializer();

            return ParseExpression();
        }

        private bool IsStartOfAssignment()
        {
            int saved = _position;
            try
            {
                if (CurrentToken.Type == TokenType.Asterisk)
                {
                    Consume();
                    ParseAssignableTarget();
                }
                else
                {
                    ParseAssignableTarget();
                }

                bool result = IsAssignmentOperator(CurrentToken.Type);
                _position = saved;
                return result;
            }
            catch
            {
                _position = saved;
                return false;
            }
        }

        private bool IsStartOfIncDecStatement()
        {
            if (CurrentToken.Type == TokenType.Increment || CurrentToken.Type == TokenType.Decrement)
                return true;

            int saved = _position;
            try
            {
                ParseAssignableTarget();
                bool result = CurrentToken.Type == TokenType.Increment || CurrentToken.Type == TokenType.Decrement;
                _position = saved;
                return result;
            }
            catch
            {
                _position = saved;
                return false;
            }
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

                if (CurrentToken.Type == TokenType.Dot)
                {
                    Consume();
                    string member = Expect(TokenType.Identifier).Value;
                    node = new StructMemberAccessNode
                    {
                        Target = node,
                        MemberName = member
                    };
                    continue;
                }

                if (CurrentToken.Type == TokenType.Arrow)
                {
                    Consume();
                    string member = Expect(TokenType.Identifier).Value;
                    node = new StructPointerMemberAccessNode
                    {
                        Target = node,
                        MemberName = member
                    };
                    continue;
                }

                break;
            }

            if (node is VariableNode || node is ArrayAccessNode || node is StructMemberAccessNode || node is StructPointerMemberAccessNode)
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
                if (CurrentToken.Type == TokenType.Struct || CurrentToken.Type == TokenType.Int || CurrentToken.Type == TokenType.Char ||
                   (CurrentToken.Type == TokenType.Identifier && _typedefs.ContainsKey(CurrentToken.Value)))
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

                if (CurrentToken.Type == TokenType.Dot)
                {
                    Consume();
                    string member = Expect(TokenType.Identifier).Value;
                    node = new StructMemberAccessNode
                    {
                        Target = node,
                        MemberName = member
                    };
                    continue;
                }

                if (CurrentToken.Type == TokenType.Arrow)
                {
                    Consume();
                    string member = Expect(TokenType.Identifier).Value;
                    node = new StructPointerMemberAccessNode
                    {
                        Target = node,
                        MemberName = member
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