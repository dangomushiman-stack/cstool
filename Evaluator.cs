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

        private const int DataStartAddress = 4;

        private int _stackPtr = DataStartAddress;
        private int _literalPtr = MemorySize;
        private int _snapshotStep = 0;
        private int _currentSourceLine = 0;

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

            if (breakpoints == null)
                return;

            foreach (int breakpoint in breakpoints)
            {
                if (breakpoint >= 0)
                    _snapshotBreakpoints.Add(breakpoint);
            }
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



























































    }
}
