using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CInterpreterWpf
{
    public class VarInfo
    {
        public int Address { get; set; }
        public CTypeInfo TypeInfo { get; } = new CTypeInfo();
        public string Type { get => TypeInfo.Type; set => TypeInfo.Type = value; }
        public bool IsPointer { get => TypeInfo.IsPointer; set => TypeInfo.IsPointer = value; }
        public int PointerLevel { get => TypeInfo.PointerLevel; set => TypeInfo.PointerLevel = value; }
        public bool IsArray { get => TypeInfo.IsArray; set => TypeInfo.IsArray = value; }
        public int ArrayLength { get => TypeInfo.ArrayLength; set => TypeInfo.ArrayLength = value; }
        public List<int> ArrayDimensions => TypeInfo.ArrayDimensions;
        public bool IsStruct { get => TypeInfo.IsStruct; set => TypeInfo.IsStruct = value; }
        public string StructName { get => TypeInfo.StructName; set => TypeInfo.StructName = value; }
        public int StructSize { get; set; }

        public int ElementSize =>
            IsStruct && !IsPointer ? StructSize :
            IsPointer ? 4 :
            (Type == "char" ? 1 :
             Type == "short" ? 2 :
             Type == "long" || Type == "double" ? 8 : 
             4); // int, float 縺ｮ繝・ヵ繧ｩ繝ｫ繝医・ 4繝舌う繝・
        public int Size =>
            IsArray ? ElementSize * GetArrayTotalLength(TypeInfo) :
            IsPointer ? 4 :
            IsStruct ? StructSize :
            ElementSize;

        private static int GetArrayTotalLength(CTypeInfo typeInfo)
        {
            if (typeInfo.ArrayDimensions.Count == 0)
                return typeInfo.ArrayLength;

            int total = 1;
            foreach (int dim in typeInfo.ArrayDimensions)
                total *= dim;
            return total;
        }


        public VarInfo Clone()
        {
            var clone = new VarInfo
            {
                Address = Address,
                StructSize = StructSize
            };
            clone.TypeInfo.CopyFrom(TypeInfo);
            return clone;
        }
    }

    public class MemoryRegionInfo
    {
        public int Address { get; set; }
        public int Size { get; set; }
        public string Label { get; set; }
        public bool IsStringLiteral { get; set; }

        public MemoryRegionInfo Clone()
        {
            return new MemoryRegionInfo
            {
                Address = Address,
                Size = Size,
                Label = Label,
                IsStringLiteral = IsStringLiteral
            };
        }
    }

    public class ExecutionSnapshot
    {
        public int Step { get; set; }
        public int SourceLine { get; set; }
        public int BreakpointLine { get; set; }
        public string FunctionName { get; set; }
        public string Event { get; set; }
        public byte[] Memory { get; set; }
        public Dictionary<string, VarInfo> Env { get; set; }
        public List<MemoryRegionInfo> Regions { get; set; }
        public int StackPointer { get; set; }
        public int LiteralPointer { get; set; }
        public int ScopeDepth { get; set; }
    }

    public class StructMemberInspectItem
    {
        public string MemberName { get; set; }
        public string Type { get; set; }
        public string Address { get; set; }
        public int AddressValue { get; set; }
        public string BaseType { get; set; }
        public bool IsPointer { get; set; }
        public int Size { get; set; }
        public string Value { get; set; }
    }

    internal class ScopeFrame
    {
        public int SavedStackPtr { get; set; }
        public int SavedRegionCount { get; set; }
        public Dictionary<string, VarInfo> PreviousBindings { get; } = new Dictionary<string, VarInfo>();
        public HashSet<string> DeclaredNames { get; } = new HashSet<string>();
    }

    public partial class Evaluator
    {
        private readonly Action<string> _stdout;

        private const int MemorySize = 65536;

        public byte[] Memory { get; } = new byte[MemorySize];
        public Dictionary<string, VarInfo> Env { get; } = new Dictionary<string, VarInfo>();
        public List<MemoryRegionInfo> Regions { get; } = new List<MemoryRegionInfo>();
        public List<ExecutionSnapshot> Snapshots { get; } = new List<ExecutionSnapshot>();
        public ExecutionSnapshot LastSnapshot => Snapshots.Count == 0 ? null : Snapshots[Snapshots.Count - 1];

        private readonly Dictionary<string, int> _stringLiteralPool = new Dictionary<string, int>();
        private readonly Stack<ScopeFrame> _scopes = new Stack<ScopeFrame>();
        private readonly Dictionary<string, FunctionDeclNode> _functions = new Dictionary<string, FunctionDeclNode>();
        private readonly Dictionary<string, StructDeclNode> _structs = new Dictionary<string, StructDeclNode>();
        private readonly HashSet<int> _snapshotBreakpoints = new HashSet<int>();
        private readonly HashSet<int> _preExecutionBreakpoints = new HashSet<int>();
        private readonly Dictionary<int, int> _preExecutionBreakpointLines = new Dictionary<int, int>();

        private const int DataStartAddress = 4;

        private int _stackPtr = DataStartAddress;
        private int _literalPtr = MemorySize;
        private int _snapshotStep = 0;
        private int _currentSourceLine = 0;
        private string _currentFunctionName = "";

        private bool _hasReturn = false;
        private object _returnValue = null;
        private bool _breakRequested = false;
        private bool _continueRequested = false;

        public Evaluator(Action<string> stdout)
        {
            _stdout = stdout;
        }

        public void SetSnapshotBreakpoints(IEnumerable<int> breakpoints)
        {
            _snapshotBreakpoints.Clear();
            _preExecutionBreakpoints.Clear();
            _preExecutionBreakpointLines.Clear();

            if (breakpoints == null)
                return;

            foreach (int breakpoint in breakpoints)
            {
                if (breakpoint >= 0)
                    _snapshotBreakpoints.Add(breakpoint);
            }
        }

        public void SetSnapshotBreakpoints(IEnumerable<int> breakpoints, string sourceCode)
        {
            SetSnapshotBreakpoints(breakpoints);

            if (breakpoints == null || string.IsNullOrEmpty(sourceCode))
                return;

            string[] lines = sourceCode.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            foreach (int breakpoint in breakpoints)
            {
                if (breakpoint <= 0 || breakpoint > lines.Length)
                    continue;

                if (!string.IsNullOrWhiteSpace(lines[breakpoint - 1]))
                    continue;

                int nextLine = FindNextExecutableBreakpointLine(lines, breakpoint + 1);
                if (nextLine <= 0)
                    continue;

                _preExecutionBreakpoints.Add(nextLine);
                if (!_preExecutionBreakpointLines.ContainsKey(nextLine))
                    _preExecutionBreakpointLines[nextLine] = breakpoint;
            }
        }

        private static int FindNextExecutableBreakpointLine(string[] lines, int startLine)
        {
            bool inBlockComment = false;

            for (int lineNumber = 1; lineNumber <= lines.Length; lineNumber++)
            {
                string code = StripCommentsForBreakpoint(lines[lineNumber - 1], ref inBlockComment).Trim();
                if (lineNumber >= startLine && code.Length > 0)
                    return lineNumber;
            }

            return 0;
        }

        private static string StripCommentsForBreakpoint(string line, ref bool inBlockComment)
        {
            var builder = new StringBuilder();

            for (int i = 0; i < line.Length;)
            {
                if (inBlockComment)
                {
                    int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0)
                        return builder.ToString();

                    inBlockComment = false;
                    i = end + 2;
                    continue;
                }

                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                    break;

                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }

                builder.Append(line[i]);
                i++;
            }

            return builder.ToString();
        }

        private static void CopyTypeInfo(CTypeInfo destination, CTypeInfo source)
        {
            if (destination == null || source == null)
                return;

            destination.CopyFrom(source);
        }

        private static VarInfo CreateVarInfoFromDeclaration(VarDeclNode declaration, int address, int arrayLength, int structSize)
        {
            var info = new VarInfo
            {
                Address = address,
                StructSize = structSize
            };

            CopyTypeInfo(info.TypeInfo, declaration?.TypeInfo);
            info.ArrayLength = arrayLength;
            if (info.IsArray && info.ArrayDimensions.Count == 0)
                info.ArrayDimensions.Add(arrayLength);
            if (info.IsArray && info.ArrayDimensions.Count > 0)
                info.ArrayDimensions[0] = arrayLength;
            return info;
        }

        private static VarInfo CreateVarInfoFromParameter(FunctionParameter parameter, int address, int structSize)
        {
            var info = new VarInfo
            {
                Address = address,
                StructSize = structSize
            };

            CopyTypeInfo(info.TypeInfo, parameter?.TypeInfo);
            info.IsArray = false;
            info.ArrayLength = 0;
            return info;
        }

        private VarInfo CreateVarInfoFromStructField(StructFieldDecl field, int address)
        {
            var info = new VarInfo
            {
                Address = address,
                StructSize = field.IsStruct ? GetStructSize(field.StructName) : 0
            };

            CopyTypeInfo(info.TypeInfo, field?.TypeInfo);
            return info;
        }

        public void Evaluate(ProgramNode program)
        {
            _functions.Clear();
            _structs.Clear();

            Array.Clear(Memory, 0, Memory.Length);
            Env.Clear();
            Regions.Clear();
            Snapshots.Clear();
            _stringLiteralPool.Clear();
            _scopes.Clear();

            _stackPtr = DataStartAddress;
            _literalPtr = Memory.Length;
            _snapshotStep = 0;
            _currentSourceLine = 0;
            _currentFunctionName = "";

            _hasReturn = false;
            _returnValue = null;
            _breakRequested = false;
            _continueRequested = false;

            foreach (var d in program.Declarations)
            {
                if (d is StructDeclNode sd)
                {
                    if (_structs.ContainsKey(sd.Name))
                        throw new Exception($"Execution Error: duplicate struct '{sd.Name}'");
                    _structs[sd.Name] = sd;
                }
                else if (d is FunctionDeclNode f)
                {
                    if (_functions.TryGetValue(f.Name, out var existing))
                    {
                        if (!existing.IsPrototype && !f.IsPrototype)
                            throw new Exception($"Execution Error: duplicate function '{f.Name}'");

                        if (!f.IsPrototype)
                            _functions[f.Name] = f;
                    }
                    else
                    {
                        _functions[f.Name] = f;
                    }
                }
            }

            EnterScope();
            CaptureSnapshot("Program start");

            try
            {
                InitializeGlobals(program);

                if (!_functions.ContainsKey("main"))
                    throw new Exception("Execution Error: 'main' not found");

                CallUserFunction(new FunctionCallNode { FunctionName = "main" });

                if (_breakRequested || _continueRequested)
                    throw new Exception("Execution Error: 'break' or 'continue' used outside of loop");

                CaptureSnapshot("Program end");
            }
            finally
            {
                ExitScope();
            }
        }

        private void InitializeGlobals(ProgramNode program)
        {
            foreach (var d in program.Declarations)
            {
                if (d is VarDeclNode v)
                    ExecuteGlobalVarDecl(v);
            }
        }

        private void ExecuteGlobalVarDecl(VarDeclNode v)
        {
            int previousSourceLine = _currentSourceLine;
            _currentSourceLine = v.Line;

            try
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

                int addr = AllocateStackRegion(info.Size, $"global:{v.VarName}");
                info.Address = addr;
                Env[v.VarName] = info;
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

                CaptureSnapshot($"GlobalVarDecl: {v.VarName}");
            }
            finally
            {
                _currentSourceLine = previousSourceLine;
            }
        }

        public void EvaluateFromSnapshot(ProgramNode program, ExecutionSnapshot snapshot)
        {
            if (snapshot == null)
                throw new Exception("Execution Error: snapshot is not selected");

            _functions.Clear();
            _structs.Clear();
            Env.Clear();
            Regions.Clear();
            Snapshots.Clear();
            _stringLiteralPool.Clear();
            _scopes.Clear();

            RegisterDeclarations(program);
            RestoreSnapshot(snapshot);

            string functionName = string.IsNullOrWhiteSpace(snapshot.FunctionName) ||
                snapshot.FunctionName == "<global>"
                ? "main"
                : snapshot.FunctionName;

            if (!_functions.TryGetValue(functionName, out var fn) || fn.IsPrototype)
                throw new Exception($"Execution Error: function '{functionName}' not found for restart");

            _hasReturn = false;
            _returnValue = null;
            _breakRequested = false;
            _continueRequested = false;
            _currentFunctionName = functionName;
            _currentSourceLine = snapshot.SourceLine;

            EnterScope();
            CaptureSnapshot($"Restart from snapshot: {functionName}:{snapshot.SourceLine}");

            try
            {
                bool includeSourceLine = IsBeforeExecutionSnapshot(snapshot);
                ExecuteFunctionBodyFromLine(fn, snapshot.SourceLine, includeSourceLine);

                if (_breakRequested || _continueRequested)
                    throw new Exception($"Execution Error: 'break' or 'continue' used outside of loop in restart '{functionName}'");

                CaptureSnapshot($"Restart end: {functionName}");
            }
            finally
            {
                ExitScope();
            }
        }

        private void RegisterDeclarations(ProgramNode program)
        {
            foreach (var d in program.Declarations)
            {
                if (d is StructDeclNode sd)
                {
                    if (_structs.ContainsKey(sd.Name))
                        throw new Exception($"Execution Error: duplicate struct '{sd.Name}'");
                    _structs[sd.Name] = sd;
                }
                else if (d is FunctionDeclNode f)
                {
                    if (_functions.TryGetValue(f.Name, out var existing))
                    {
                        if (!existing.IsPrototype && !f.IsPrototype)
                            throw new Exception($"Execution Error: duplicate function '{f.Name}'");

                        if (!f.IsPrototype)
                            _functions[f.Name] = f;
                    }
                    else
                    {
                        _functions[f.Name] = f;
                    }
                }
            }
        }

        private void RestoreSnapshot(ExecutionSnapshot snapshot)
        {
            Array.Clear(Memory, 0, Memory.Length);
            if (snapshot.Memory != null)
                Array.Copy(snapshot.Memory, Memory, Math.Min(snapshot.Memory.Length, Memory.Length));

            Env.Clear();
            if (snapshot.Env != null)
            {
                foreach (var kvp in snapshot.Env)
                    Env[kvp.Key] = kvp.Value.Clone();
            }

            Regions.Clear();
            if (snapshot.Regions != null)
            {
                foreach (var region in snapshot.Regions)
                    Regions.Add(region.Clone());
            }

            _stackPtr = snapshot.StackPointer;
            _literalPtr = snapshot.LiteralPointer;
            _snapshotStep = 0;
            _currentSourceLine = snapshot.SourceLine;
        }

        private static bool IsBeforeExecutionSnapshot(ExecutionSnapshot snapshot)
        {
            return snapshot.BreakpointLine > 0 &&
                snapshot.BreakpointLine != snapshot.SourceLine &&
                snapshot.Event != null &&
                snapshot.Event.StartsWith("Before line ", StringComparison.Ordinal);
        }

        private void ExecuteFunctionBodyFromLine(FunctionDeclNode fn, int sourceLine, bool includeSourceLine)
        {
            bool started = false;

            foreach (var stmt in fn.Body)
            {
                int line = GetNodeLine(stmt);
                if (!started)
                {
                    bool shouldSkip = includeSourceLine
                        ? line < sourceLine
                        : line <= sourceLine;

                    if (shouldSkip)
                        continue;

                    started = true;
                }

                ExecuteStatement(stmt);

                if (_hasReturn)
                    break;

                if (_breakRequested || _continueRequested)
                    break;
            }
        }

        private static int GetNodeLine(IASTNode node)
        {
            return node is ISourceLineNode sourceLineNode ? sourceLineNode.Line : 0;
        }



























































    }
}
