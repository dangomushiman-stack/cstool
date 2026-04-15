using System;
using System.Collections.Generic;

namespace CInterpreterWpf
{
    public class CInterpreter
    {
        private readonly Action<string> _stdout;

        public CInterpreter(Action<string> stdout)
        {
            _stdout = stdout;
        }

        public void Execute(string sourceCode)
        {
            try
            {
                // 1. Lexer (字句解析)
                var lexer = new Lexer(sourceCode);
                List<Token> tokens = lexer.Tokenize();

                // 2. Parser (構文解析)
                var parser = new Parser(tokens);
                ProgramNode ast = parser.Parse();

                // 3. Evaluator (評価・実行)
                var evaluator = new Evaluator(_stdout);
                
                _stdout("=== Program Output ===");
                
                // ここで実際にプログラムが動きます！
                evaluator.Evaluate(ast);
                
                _stdout("======================");
                _stdout("Execution finished successfully.");
            }
            catch (Exception ex)
            {
                _stdout(ex.Message);
            }
        }
    }
}