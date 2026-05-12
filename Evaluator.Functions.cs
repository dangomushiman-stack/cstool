using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CInterpreterWpf
{
    public partial class Evaluator
    {

        private int EvaluateFunctionCall(FunctionCallNode call)
        {
            if (call.FunctionName == "memset")
            {
                if (call.Arguments.Count != 3)
                    throw new Exception("Execution Error: memset expects 3 arguments");

                int destAddr = Convert.ToInt32(EvaluateExpression(call.Arguments[0]));
                int value = Convert.ToInt32(EvaluateExpression(call.Arguments[1])) & 0xFF;
                int count = Convert.ToInt32(EvaluateExpression(call.Arguments[2]));

                if (count < 0)
                    throw new Exception("Execution Error: memset count must be non-negative");

                EnsureMemoryRange(destAddr, count);
                for (int i = 0; i < count; i++)
                    WriteByte(destAddr + i, value);

                CaptureSnapshot($"Call: {call.FunctionName}");
                return destAddr;
            }

            if (call.FunctionName == "memcpy")
            {
                if (call.Arguments.Count != 3)
                    throw new Exception("Execution Error: memcpy expects 3 arguments");

                int destAddr = Convert.ToInt32(EvaluateExpression(call.Arguments[0]));
                int srcAddr = Convert.ToInt32(EvaluateExpression(call.Arguments[1]));
                int count = Convert.ToInt32(EvaluateExpression(call.Arguments[2]));

                if (count < 0)
                    throw new Exception("Execution Error: memcpy count must be non-negative");

                CopyBytes(srcAddr, destAddr, count);
                CaptureSnapshot($"Call: {call.FunctionName}");
                return destAddr;
            }

            if (call.FunctionName == "printf")
            {
                int fmtAddr = Convert.ToInt32(EvaluateExpression(call.Arguments[0]));
                string fmt = ReadCString(fmtAddr);
                var args = new List<object>();

                for (int i = 1; i < call.Arguments.Count; i++)
                    args.Add(EvaluateExpression(call.Arguments[i]));

                int idx = 0;
                string output = Regex.Replace(fmt, @"%[dcsxp]", m =>
                {
                    var v = args[idx++];
                    return m.Value switch
                    {
                        "%c" => ((char)Convert.ToInt32(v)).ToString(),
                        "%s" => ReadCString(Convert.ToInt32(v)),
                        "%x" => Convert.ToInt32(v).ToString("x"),
                        "%p" => $"0x{Convert.ToInt32(v):x8}",
                        _ => v.ToString()
                    };
                });

                _stdout(output.Replace("\n", Environment.NewLine));
                CaptureSnapshot($"Call: {call.FunctionName}");
                return 0;
            }

            int result = CallUserFunction(call);
            CaptureSnapshot($"Call: {call.FunctionName}");
            return result;
        }

        private int CallUserFunction(FunctionCallNode call)
        {
            if (!_functions.TryGetValue(call.FunctionName, out var fn))
                throw new Exception($"Execution Error: function '{call.FunctionName}' not found");

            if (fn.IsPrototype)
                throw new Exception($"Execution Error: function '{call.FunctionName}' has no definition");

            if (call.Arguments.Count != fn.Parameters.Count)
                throw new Exception($"Execution Error: function '{call.FunctionName}' expects {fn.Parameters.Count} arguments, but got {call.Arguments.Count}");

            var argValues = new List<int>();
            foreach (var arg in call.Arguments)
                argValues.Add(Convert.ToInt32(EvaluateExpression(arg)));

            bool savedHasReturn = _hasReturn;
            object savedReturnValue = _returnValue;
            bool savedBreak = _breakRequested;
            bool savedContinue = _continueRequested;

            _hasReturn = false;
            _returnValue = 0;
            _breakRequested = false;
            _continueRequested = false;

            EnterScope();
            CaptureSnapshot($"Enter function: {call.FunctionName}");

            try
            {
                for (int i = 0; i < fn.Parameters.Count; i++)
                {
                    var param = fn.Parameters[i];
                    var info = CreateVarInfoFromParameter(
                        param,
                        _stackPtr,
                        param.IsStruct && !param.IsPointer ? GetStructSize(param.StructName) : 0);

                    int addr = AllocateStackRegion(info.Size, param.Name);
                    info.Address = addr;
                    BindVariable(param.Name, info);

                    if (info.IsStruct && !info.IsPointer)
                        throw new Exception("Execution Error: passing struct by value is not supported yet");

                    WriteScalarAtAddress(info.Type, info.IsPointer, info.Address, argValues[i]);
                    CaptureSnapshot($"Param bind: {param.Name}");
                }

                foreach (var stmt in fn.Body)
                {
                    ExecuteStatement(stmt);

                    if (_hasReturn)
                        break;

                    if (_breakRequested || _continueRequested)
                        throw new Exception($"Execution Error: 'break' or 'continue' used outside of loop in function '{call.FunctionName}'");
                }

                int result = _returnValue == null ? 0 : Convert.ToInt32(_returnValue);
                CaptureSnapshot($"Exit function: {call.FunctionName}");
                return result;
            }
            finally
            {
                ExitScope();

                _hasReturn = savedHasReturn;
                _returnValue = savedReturnValue;
                _breakRequested = savedBreak;
                _continueRequested = savedContinue;
            }
        }
    }
}
