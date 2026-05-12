using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CInterpreterWpf
{
    public partial class Evaluator
    {

        private void ExecuteStatement(IASTNode stmt)
        {
            if (_hasReturn || stmt == null) return;
            if (_breakRequested || _continueRequested) return;

            int previousSourceLine = _currentSourceLine;
            if (stmt is ISourceLineNode sourceLineNode && sourceLineNode.Line > 0)
                _currentSourceLine = sourceLineNode.Line;

            try
            {
            if (stmt is BlockNode block)
            {
                EnterScope();
                try
                {
                    foreach (var s in block.Statements)
                    {
                        ExecuteStatement(s);
                        if (_hasReturn || _breakRequested || _continueRequested) break;
                    }
                }
                finally
                {
                    ExitScope();
                }

                CaptureSnapshot("Block exited");
                return;
            }

            if (stmt is VarDeclNode v)
            {
                int resolvedArrayLength = v.IsArray
                    ? (v.IsArrayLengthInferred ? GetInitializerArrayLength(v) : v.ArrayLength)
                    : v.ArrayLength;

                var info = CreateVarInfoFromDeclaration(
                    v,
                    _stackPtr,
                    resolvedArrayLength,
                    v.IsStruct ? GetStructSize(v.StructName) : 0);

                if (info.IsArray && info.ArrayLength <= 0)
                    throw new Exception($"Execution Error: invalid array length for '{v.VarName}'");

                int addr = AllocateStackRegion(info.Size, v.VarName);
                info.Address = addr;
                BindVariable(v.VarName, info);
                ZeroMemory(info.Address, info.Size);

                if (v.IsStruct && !v.IsPointer)
                {
                    if (v.Initializer != null)
                        InitializeStruct(info, v.Initializer);
                }
                else if (v.IsArray)
                {
                    InitializeArray(info, v.Initializer);
                }
                else if (v.Initializer != null)
                {
                    int value = Convert.ToInt32(EvaluateExpression(v.Initializer));
                    WriteScalarAtAddress(info.Type, info.IsPointer, info.Address, value);
                }

                CaptureSnapshot($"VarDecl: {v.VarName}");
                return;
            }

            if (stmt is VarDeclListNode varDeclList)
            {
                foreach (var declaration in varDeclList.Declarations)
                {
                    ExecuteStatement(declaration);
                    if (_hasReturn || _breakRequested || _continueRequested)
                        break;
                }

                return;
            }

            if (stmt is AssignmentNode assign)
            {
                int rightValue = Convert.ToInt32(EvaluateExpression(assign.Right));
                int currentValue = assign.Operator == "=" ? 0 : ReadTarget(assign.Left);
                int newValue = ApplyAssignmentOperator(assign.Operator, assign.Left, currentValue, rightValue);
                WriteTarget(assign.Left, newValue);

                CaptureSnapshot($"Assign: {assign.Operator}");
                return;
            }

            if (stmt is PostfixOpNode postfixStmt)
            {
                EvaluateExpression(postfixStmt);
                CaptureSnapshot($"Postfix: {postfixStmt.Operator}");
                return;
            }

            if (stmt is UnaryOpNode unaryStmt &&
                (unaryStmt.Operator == "++" || unaryStmt.Operator == "--"))
            {
                EvaluateExpression(unaryStmt);
                CaptureSnapshot($"Unary: {unaryStmt.Operator}");
                return;
            }

            if (stmt is FunctionCallNode callStmt)
            {
                EvaluateFunctionCall(callStmt);
                CaptureSnapshot($"FunctionCall: {callStmt.FunctionName}");
                return;
            }

            if (stmt is IfNode ifNode)
            {
                int cond = Convert.ToInt32(EvaluateExpression(ifNode.Condition));
                if (cond != 0)
                    ExecuteScopedStatement(ifNode.ThenBranch);
                else if (ifNode.ElseBranch != null)
                    ExecuteScopedStatement(ifNode.ElseBranch);

                CaptureSnapshot("If completed");
                return;
            }

            if (stmt is WhileNode whileNode)
            {
                while (!_hasReturn && Convert.ToInt32(EvaluateExpression(whileNode.Condition)) != 0)
                {
                    _continueRequested = false;
                    ExecuteScopedStatement(whileNode.Body);

                    if (_hasReturn) break;

                    if (_breakRequested)
                    {
                        _breakRequested = false;
                        break;
                    }

                    if (_continueRequested)
                    {
                        _continueRequested = false;
                        continue;
                    }
                }

                CaptureSnapshot("While completed");
                return;
            }

            if (stmt is DoWhileNode doWhileNode)
            {
                do
                {
                    _continueRequested = false;
                    ExecuteScopedStatement(doWhileNode.Body);

                    if (_hasReturn) break;

                    if (_breakRequested)
                    {
                        _breakRequested = false;
                        break;
                    }
                }
                while (!_hasReturn && Convert.ToInt32(EvaluateExpression(doWhileNode.Condition)) != 0);

                _continueRequested = false;
                CaptureSnapshot("DoWhile completed");
                return;
            }

            if (stmt is ForNode forNode)
            {
                EnterScope();
                try
                {
                    if (forNode.Initializer != null)
                    {
                        if (forNode.Initializer is AssignmentNode or VarDeclNode or VarDeclListNode or PostfixOpNode or UnaryOpNode or FunctionCallNode)
                            ExecuteStatement(forNode.Initializer);
                        else
                            EvaluateExpression(forNode.Initializer);
                    }

                    while (!_hasReturn)
                    {
                        if (forNode.Condition != null)
                        {
                            int cond = Convert.ToInt32(EvaluateExpression(forNode.Condition));
                            if (cond == 0) break;
                        }

                        _continueRequested = false;
                        ExecuteScopedStatement(forNode.Body);

                        if (_hasReturn) break;

                        if (_breakRequested)
                        {
                            _breakRequested = false;
                            break;
                        }

                        if (_continueRequested)
                            _continueRequested = false;

                        CaptureSnapshot("For iteration");

                        if (forNode.Increment != null)
                        {
                            if (forNode.Increment is AssignmentNode or PostfixOpNode or UnaryOpNode or FunctionCallNode)
                                ExecuteStatement(forNode.Increment);
                            else
                                EvaluateExpression(forNode.Increment);
                        }
                    }
                }
                finally
                {
                    ExitScope();
                }

                CaptureSnapshot("For completed");
                return;
            }

            if (stmt is SwitchNode switchNode)
            {
                int switchValue = Convert.ToInt32(EvaluateExpression(switchNode.Expression));
                int startIndex = -1;
                int defaultIndex = -1;

                for (int i = 0; i < switchNode.Cases.Count; i++)
                {
                    var section = switchNode.Cases[i];
                    if (section.IsDefault)
                    {
                        defaultIndex = i;
                        continue;
                    }

                    int caseValue = Convert.ToInt32(EvaluateExpression(section.Value));
                    if (caseValue == switchValue)
                    {
                        startIndex = i;
                        break;
                    }
                }

                if (startIndex < 0)
                    startIndex = defaultIndex;

                if (startIndex >= 0)
                {
                    EnterScope();
                    try
                    {
                        for (int i = startIndex; i < switchNode.Cases.Count; i++)
                        {
                            foreach (var sectionStatement in switchNode.Cases[i].Statements)
                            {
                                ExecuteStatement(sectionStatement);

                                if (_hasReturn || _breakRequested || _continueRequested)
                                    break;
                            }

                            if (_hasReturn || _breakRequested || _continueRequested)
                                break;
                        }
                    }
                    finally
                    {
                        ExitScope();
                    }

                    if (_breakRequested)
                        _breakRequested = false;
                }

                CaptureSnapshot("Switch completed");
                return;
            }

            if (stmt is BreakNode)
            {
                _breakRequested = true;
                CaptureSnapshot("Break");
                return;
            }

            if (stmt is ContinueNode)
            {
                _continueRequested = true;
                CaptureSnapshot("Continue");
                return;
            }

            if (stmt is ReturnNode ret)
            {
                _returnValue = ret.Value == null ? 0 : EvaluateExpression(ret.Value);
                _hasReturn = true;
                CaptureSnapshot("Return");
                return;
            }
            }
            finally
            {
                _currentSourceLine = previousSourceLine;
            }
        }

        private void ExecuteScopedStatement(IASTNode stmt)
        {
            EnterScope();
            try
            {
                ExecuteStatement(stmt);
            }
            finally
            {
                ExitScope();
            }
        }
    }
}
