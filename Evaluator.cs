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
             4); // int, float のデフォルトは 4バイト

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
        public string Event { get; set; }
        public byte[] Memory { get; set; }
        public Dictionary<string, VarInfo> Env { get; set; }
        public List<MemoryRegionInfo> Regions { get; set; }
        public int StackPointer { get; set; }
        public int LiteralPointer { get; set; }
        public int ScopeDepth { get; set; }
    }

    internal class ScopeFrame
    {
        public int SavedStackPtr { get; set; }
        public int SavedRegionCount { get; set; }
        public Dictionary<string, VarInfo> PreviousBindings { get; } = new Dictionary<string, VarInfo>();
        public HashSet<string> DeclaredNames { get; } = new HashSet<string>();
    }

    public class Evaluator
    {
        private readonly Action<string> _stdout;

        private const int MemorySize = 4096;

        public byte[] Memory { get; } = new byte[MemorySize];
        public Dictionary<string, VarInfo> Env { get; } = new Dictionary<string, VarInfo>();
        public List<MemoryRegionInfo> Regions { get; } = new List<MemoryRegionInfo>();
        public List<ExecutionSnapshot> Snapshots { get; } = new List<ExecutionSnapshot>();
        public ExecutionSnapshot LastSnapshot => Snapshots.Count == 0 ? null : Snapshots[Snapshots.Count - 1];

        private readonly Dictionary<string, int> _stringLiteralPool = new Dictionary<string, int>();
        private readonly Stack<ScopeFrame> _scopes = new Stack<ScopeFrame>();
        private readonly Dictionary<string, FunctionDeclNode> _functions = new Dictionary<string, FunctionDeclNode>();
        private readonly Dictionary<string, StructDeclNode> _structs = new Dictionary<string, StructDeclNode>();

        private const int DataStartAddress = 4;

        private int _stackPtr = DataStartAddress;
        private int _literalPtr = MemorySize;
        private int _snapshotStep = 0;

        private bool _hasReturn = false;
        private object _returnValue = null;
        private bool _breakRequested = false;
        private bool _continueRequested = false;

        public Evaluator(Action<string> stdout)
        {
            _stdout = stdout;
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

        private int GetStructFieldElementSize(StructFieldDecl field)
        {
            if (field.IsPointer) return 4;
            if (field.IsStruct) return GetStructSize(field.StructName);
            return field.Type == "char" ? 1 : 4;
        }

        private int GetStructFieldSize(StructFieldDecl field)
        {
            int elementSize = GetStructFieldElementSize(field);
            return field.IsArray ? elementSize * GetArrayTotalLength(field.TypeInfo) : elementSize;
        }

        private int GetStructSize(string structName)
        {
            if (!_structs.TryGetValue(structName, out var sd))
                throw new Exception($"Execution Error: struct '{structName}' not found");

            int size = 0;
            foreach (var field in sd.Fields)
                size += GetStructFieldSize(field);

            return size;
        }

        private (StructFieldDecl field, int offset) GetStructFieldInfo(string structName, string memberName)
        {
            if (!_structs.TryGetValue(structName, out var sd))
                throw new Exception($"Execution Error: struct '{structName}' not found");

            int offset = 0;
            foreach (var field in sd.Fields)
            {
                if (field.Name == memberName)
                    return (field, offset);

                offset += GetStructFieldSize(field);
            }

            throw new Exception($"Execution Error: struct '{structName}' has no member '{memberName}'");
        }

        private void ZeroMemory(int addr, int size)
        {
            EnsureMemoryRange(addr, size);
            Array.Clear(Memory, addr, size);
        }

        private void InitializeStruct(VarInfo info, IASTNode initializer)
        {
            if (initializer == null)
                return;

            if (initializer is not StructInitializerNode structInit)
                throw new Exception("Execution Error: invalid struct initializer");

            if (!_structs.TryGetValue(info.StructName, out var sd))
                throw new Exception($"Execution Error: struct '{info.StructName}' not found");

            if (structInit.Elements.Count > sd.Fields.Count)
                throw new Exception($"Execution Error: too many initializer elements for struct '{info.StructName}'");

            int offset = 0;
            for (int i = 0; i < sd.Fields.Count; i++)
            {
                var field = sd.Fields[i];
                int fieldAddr = info.Address + offset;

                if (i < structInit.Elements.Count)
                {
                    var elem = structInit.Elements[i];

                    if (field.IsArray)
                    {
                        var arrayInfo = CreateVarInfoFromStructField(field, fieldAddr);
                        InitializeArray(arrayInfo, elem);
                    }
                    else if (field.IsStruct && !field.IsPointer)
                    {
                        var nestedInfo = new VarInfo
                        {
                            Address = fieldAddr,
                            StructSize = GetStructSize(field.StructName)
                        };
                        nestedInfo.TypeInfo.Type = "struct";
                        nestedInfo.TypeInfo.IsPointer = false;
                        nestedInfo.TypeInfo.IsArray = false;
                        nestedInfo.TypeInfo.ArrayLength = 0;
                        nestedInfo.TypeInfo.IsStruct = true;
                        nestedInfo.TypeInfo.StructName = field.StructName;

                        InitializeStruct(nestedInfo, elem);
                    }
                    else
                    {
                        int value = Convert.ToInt32(EvaluateExpression(elem));
                        WriteScalarAtAddress(field.Type, field.IsPointer, fieldAddr, value);
                    }
                }

                offset += GetStructFieldSize(field);
            }
        }

        private bool TryGetStructValueInfo(IASTNode expr, out string structName, out int address)
        {
            if (expr is VariableNode v)
            {
                if (!Env.TryGetValue(v.Name, out var info))
                    throw new Exception($"Execution Error: variable '{v.Name}' not found");

                if (info.IsArray && info.IsStruct)
                {
                    structName = info.StructName;
                    address = info.Address;
                    return true;
                }

                if (info.IsStruct && !info.IsPointer)
                {
                    structName = info.StructName;
                    address = info.Address;
                    return true;
                }

                structName = null;
                address = 0;
                return false;
            }

            if (expr is ArrayAccessNode aa)
            {
                if (TryGetArrayAccessStructType(aa, out structName))
                {
                    address = GetIndexedAddress(aa);
                    return true;
                }

                structName = null;
                address = 0;
                return false;
            }

            if (expr is StructMemberAccessNode sm)
            {
                if (!TryGetStructValueInfo(sm.Target, out string baseStructName, out int baseAddr))
                {
                    structName = null;
                    address = 0;
                    return false;
                }

                var fieldInfo = GetStructFieldInfo(baseStructName, sm.MemberName);
                if (!fieldInfo.field.IsStruct || fieldInfo.field.IsPointer)
                {
                    structName = null;
                    address = 0;
                    return false;
                }

                structName = fieldInfo.field.StructName;
                address = baseAddr + fieldInfo.offset;
                return true;
            }

            if (expr is StructPointerMemberAccessNode spm)
            {
                if (!TryGetStructPointerType(spm.Target, out string baseStructName))
                {
                    structName = null;
                    address = 0;
                    return false;
                }

                int baseAddr = Convert.ToInt32(EvaluateExpression(spm.Target));
                var fieldInfo = GetStructFieldInfo(baseStructName, spm.MemberName);
                if (!fieldInfo.field.IsStruct || fieldInfo.field.IsPointer)
                {
                    structName = null;
                    address = 0;
                    return false;
                }

                structName = fieldInfo.field.StructName;
                address = baseAddr + fieldInfo.offset;
                return true;
            }

            structName = null;
            address = 0;
            return false;
        }

        private bool TryGetStructPointerType(IASTNode expr, out string structName)
        {
            if (expr is CastNode cast &&
                cast.TargetTypeInfo.IsStruct &&
                cast.TargetTypeInfo.PointerLevel == 1)
            {
                structName = cast.TargetTypeInfo.StructName;
                return true;
            }

            if (expr is VariableNode v &&
                Env.TryGetValue(v.Name, out var info) &&
                info.IsStruct && info.PointerLevel == 1)
            {
                structName = info.StructName;
                return true;
            }

            if (expr is FunctionCallNode call &&
                _functions.TryGetValue(call.FunctionName, out var fn) &&
                fn.ReturnIsStruct && fn.ReturnPointerLevel == 1)
            {
                structName = fn.ReturnStructName;
                return true;
            }

            if (expr is BinaryOpNode b && (b.Operator == "+" || b.Operator == "-"))
            {
                if (TryGetStructPointerType(b.Left, out structName))
                    return true;

                if (TryGetStructPointerType(b.Right, out structName))
                    return true;
            }

            if (expr is UnaryOpNode u && u.Operator == "&")
            {
                if (TryGetStructValueInfo(u.Target, out structName, out _))
                    return true;
            }

            if (expr is UnaryOpNode deref && deref.Operator == "*")
            {
                if (deref.Target is VariableNode dv &&
                    Env.TryGetValue(dv.Name, out var derefInfo) &&
                    derefInfo.IsStruct && derefInfo.PointerLevel == 2)
                {
                    structName = derefInfo.StructName;
                    return true;
                }

                if (deref.Target is FunctionCallNode dcall &&
                    _functions.TryGetValue(dcall.FunctionName, out var dfn) &&
                    dfn.ReturnIsStruct && dfn.ReturnPointerLevel == 2)
                {
                    structName = dfn.ReturnStructName;
                    return true;
                }
            }

            structName = null;
            return false;
        }

        private bool TryGetArrayAccessStructType(ArrayAccessNode access, out string structName)
        {
            if (access.Target is VariableNode v &&
                Env.TryGetValue(v.Name, out var info))
            {
                if (info.IsArray && info.IsStruct)
                {
                    structName = info.StructName;
                    return true;
                }
            }

            if (access.Target is StructMemberAccessNode sm &&
                TryGetStructValueInfo(sm.Target, out string baseStructName, out _))
            {
                var field = GetStructFieldInfo(baseStructName, sm.MemberName).field;
                if (field.IsArray && field.IsStruct)
                {
                    structName = field.StructName;
                    return true;
                }
            }

            if (access.Target is StructPointerMemberAccessNode spm &&
                TryGetStructPointerType(spm.Target, out string ptrStructName))
            {
                var field = GetStructFieldInfo(ptrStructName, spm.MemberName).field;
                if (field.IsArray && field.IsStruct)
                {
                    structName = field.StructName;
                    return true;
                }
            }

            if (TryGetStructPointerType(access.Target, out structName))
                return true;

            structName = null;
            return false;
        }

        private int GetStructMemberAddress(StructMemberAccessNode access)
        {
            if (!TryGetStructValueInfo(access.Target, out string baseStructName, out int baseAddr))
                throw new Exception("Execution Error: left side of '.' is not a struct");

            var fieldInfo = GetStructFieldInfo(baseStructName, access.MemberName);
            return baseAddr + fieldInfo.offset;
        }

        private int GetStructPointerMemberAddress(StructPointerMemberAccessNode access)
        {
            if (!TryGetStructPointerType(access.Target, out string structName))
                throw new Exception("Execution Error: left side of '->' is not a pointer to struct");

            int baseAddr = Convert.ToInt32(EvaluateExpression(access.Target));
            var fieldInfo = GetStructFieldInfo(structName, access.MemberName);
            return baseAddr + fieldInfo.offset;
        }

        private int GetPointeeElementSize(IASTNode expr)
        {
            if (TryGetStructPointerType(expr, out string structName))
                return GetStructSize(structName);

            if (expr is VariableNode v && Env.TryGetValue(v.Name, out var info) && info.IsArray)
                return GetArrayStride(info.TypeInfo, 0, GetArrayScalarElementSize(info.TypeInfo, info.StructSize));

            if (expr is VariableNode pointerArrayVar &&
                Env.TryGetValue(pointerArrayVar.Name, out var pointerArrayInfo) &&
                IsPointerToArray(pointerArrayInfo))
                return GetArrayStride(pointerArrayInfo.TypeInfo, 0, GetArrayScalarElementSize(pointerArrayInfo.TypeInfo, pointerArrayInfo.StructSize));

            if (TryGetPointeeType(expr, out string type, out int pointerLevel))
                return GetTypeElementSize(type, pointerLevel > 0);

            return 4;
        }

        private int GetIncDecStep(IASTNode target)
        {
            if (target is VariableNode v && Env.TryGetValue(v.Name, out var info))
            {
                if (info.IsStruct && info.PointerLevel == 1)
                    return GetStructSize(info.StructName);

                if (IsPointerToArray(info))
                    return GetArrayStride(info.TypeInfo, 0, GetArrayScalarElementSize(info.TypeInfo, info.StructSize));

                if (info.PointerLevel > 0)
                    return GetTypeElementSize(info.Type, info.PointerLevel > 1);
            }

            if (TryGetStructPointerType(target, out string structName))
                return GetStructSize(structName);

            if (TryGetPointeeType(target, out string type, out int pointerLevel))
                return GetTypeElementSize(type, pointerLevel > 0);

            return 1;
        }

        private static bool IsPointerToArray(VarInfo info)
        {
            return info != null && !info.IsArray && info.PointerLevel > 0 && info.ArrayDimensions.Count > 0;
        }

        private bool IsPointerToArrayExpression(IASTNode expr, out VarInfo info)
        {
            if (expr is VariableNode v &&
                Env.TryGetValue(v.Name, out info) &&
                IsPointerToArray(info))
                return true;

            info = null;
            return false;
        }

        private bool TryGetPointerDifferenceElementSize(IASTNode left, IASTNode right, out int elementSize)
        {
            if (left is VariableNode leftVar &&
                right is VariableNode rightVar &&
                Env.TryGetValue(leftVar.Name, out var leftInfo) &&
                Env.TryGetValue(rightVar.Name, out var rightInfo))
            {
                if (leftInfo.IsStruct &&
                    rightInfo.IsStruct &&
                    leftInfo.PointerLevel == 1 &&
                    rightInfo.PointerLevel == 1 &&
                    leftInfo.StructName == rightInfo.StructName)
                {
                    elementSize = GetStructSize(leftInfo.StructName);
                    return true;
                }

                if (leftInfo.PointerLevel > 0 &&
                    rightInfo.PointerLevel > 0 &&
                    leftInfo.Type == rightInfo.Type &&
                    leftInfo.PointerLevel == rightInfo.PointerLevel)
                {
                    elementSize = GetTypeElementSize(leftInfo.Type, leftInfo.PointerLevel > 1);
                    return true;
                }
            }

            if (TryGetStructPointerType(left, out string leftStructName) &&
                TryGetStructPointerType(right, out string rightStructName) &&
                leftStructName == rightStructName)
            {
                elementSize = GetStructSize(leftStructName);
                return true;
            }

            if (TryGetPointeeType(left, out string leftType, out int leftPointerLevel) &&
                TryGetPointeeType(right, out string rightType, out int rightPointerLevel) &&
                leftType == rightType &&
                leftPointerLevel == rightPointerLevel)
            {
                elementSize = GetTypeElementSize(leftType, leftPointerLevel > 0);
                return true;
            }

            elementSize = 0;
            return false;
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

        private void CaptureSnapshot(string evt)
        {
            var memoryCopy = new byte[Memory.Length];
            Array.Copy(Memory, memoryCopy, Memory.Length);

            var envCopy = new Dictionary<string, VarInfo>();
            foreach (var kvp in Env)
                envCopy[kvp.Key] = kvp.Value.Clone();

            var regionCopy = new List<MemoryRegionInfo>();
            foreach (var region in Regions)
                regionCopy.Add(region.Clone());

            Snapshots.Add(new ExecutionSnapshot
            {
                Step = _snapshotStep++,
                Event = evt,
                Memory = memoryCopy,
                Env = envCopy,
                Regions = regionCopy,
                StackPointer = _stackPtr,
                LiteralPointer = _literalPtr,
                ScopeDepth = _scopes.Count
            });
        }

        private void EnsureMemoryRange(int addr, int size)
        {
            if (addr < 0 || size < 0 || addr + size > Memory.Length)
                throw new Exception($"Execution Error: memory access out of range at 0x{addr:X4}");
        }

        private void EnsureSpaceForStackAllocation(int size, string label = null)
        {
            if (size <= 0)
                throw new Exception($"Execution Error: invalid allocation size for '{label}'");

            if (_stackPtr + size > _literalPtr)
                throw new Exception($"Execution Error: out of memory allocating '{label}' size {size}");
        }

        private void EnterScope()
        {
            _scopes.Push(new ScopeFrame
            {
                SavedStackPtr = _stackPtr,
                SavedRegionCount = Regions.Count
            });
        }

        private void ExitScope()
        {
            if (_scopes.Count == 0)
                throw new Exception("Execution Error: scope stack underflow");

            var frame = _scopes.Pop();

            foreach (var name in frame.DeclaredNames)
            {
                if (frame.PreviousBindings.TryGetValue(name, out var previous))
                    Env[name] = previous;
                else
                    Env.Remove(name);
            }

            _stackPtr = frame.SavedStackPtr;

            if (Regions.Count > frame.SavedRegionCount)
                Regions.RemoveRange(frame.SavedRegionCount, Regions.Count - frame.SavedRegionCount);
        }

        private int AllocateStackRegion(int size, string label)
        {
            EnsureSpaceForStackAllocation(size, label);

            int addr = _stackPtr;

            Regions.Add(new MemoryRegionInfo
            {
                Address = addr,
                Size = size,
                Label = label,
                IsStringLiteral = false
            });

            _stackPtr += size;
            return addr;
        }

        private int AllocateLiteralRegion(int size, string label)
        {
            if (size <= 0)
                throw new Exception("Execution Error: invalid allocation size");

            int newLiteralPtr = _literalPtr - size;
            if (newLiteralPtr < _stackPtr)
                throw new Exception("Execution Error: out of memory");

            _literalPtr = newLiteralPtr;

            Regions.Add(new MemoryRegionInfo
            {
                Address = _literalPtr,
                Size = size,
                Label = label,
                IsStringLiteral = true
            });

            return _literalPtr;
        }

        private int EnsureStringLiteral(string value)
        {
            if (_stringLiteralPool.TryGetValue(value, out int existingAddr))
                return existingAddr;

            // C#の文字列をUTF-8のバイト配列に変換
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(value);
            int size = utf8Bytes.Length + 1; // null終端文字(+1)
            int addr = AllocateLiteralRegion(size, $"string literal \"{value}\"");

            // UTF-8のバイト列をメモリに書き込む
            for (int i = 0; i < utf8Bytes.Length; i++)
                WriteByte(addr + i, utf8Bytes[i]);

            WriteByte(addr + utf8Bytes.Length, 0); // null終端
            _stringLiteralPool[value] = addr;

            CaptureSnapshot($"String literal allocated: \"{value}\"");
            return addr;
        }

        private string ReadCString(int addr)
        {
            var bytes = new List<byte>();
            int current = addr;

            while (true)
            {
                EnsureMemoryRange(current, 1);
                byte b = Memory[current];
                if (b == 0) break; // null終端で終了
                bytes.Add(b);
                current++;
            }

            // メモリ上のUTF-8バイト配列をC#の文字列に復元
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private void CopyBytes(int srcAddr, int dstAddr, int count)
        {
            EnsureMemoryRange(srcAddr, count);
            EnsureMemoryRange(dstAddr, count);
            Array.Copy(Memory, srcAddr, Memory, dstAddr, count);
        }

        private void WriteInt(int addr, int val)
        {
            EnsureMemoryRange(addr, 4);
            Array.Copy(BitConverter.GetBytes(val), 0, Memory, addr, 4);
        }

        private int ReadInt(int addr)
        {
            EnsureMemoryRange(addr, 4);
            return BitConverter.ToInt32(Memory, addr);
        }

        private void WriteByte(int addr, int value)
        {
            EnsureMemoryRange(addr, 1);
            Memory[addr] = (byte)value;
        }

        private int ReadByte(int addr)
        {
            EnsureMemoryRange(addr, 1);
            return Memory[addr];
        }

        private void WriteScalarAtAddress(string type, bool isPointer, int addr, int value)
        {
            int size = isPointer ? 4 : (type == "char" ? 1 : 4);
            if (size == 1) WriteByte(addr, value);
            else WriteInt(addr, value);
        }

        private int ReadScalarAtAddress(string type, bool isPointer, int addr)
        {
            int size = isPointer ? 4 : (type == "char" ? 1 : 4);
            return size == 1 ? ReadByte(addr) : ReadInt(addr);
        }

        private int GetTypeElementSize(string type, bool isPointer)
        {
            if (isPointer) return 4;
            return type switch {
                "char" => 1,
                "short" => 2,
                "long" => 8,
                "double" => 8,
                _ => 4 // int, float
            };
        }

        private static int GetArrayTotalLength(CTypeInfo typeInfo)
        {
            if (typeInfo.ArrayDimensions.Count == 0)
                return typeInfo.ArrayLength;

            int total = 1;
            foreach (int dim in typeInfo.ArrayDimensions)
                total *= dim;
            return total;
        }

        private int GetArrayScalarElementSize(CTypeInfo typeInfo, int structSize)
        {
            if (typeInfo.IsStruct && !typeInfo.IsPointer)
                return structSize > 0 ? structSize : GetStructSize(typeInfo.StructName);

            bool elementIsPointer = typeInfo.IsArray
                ? typeInfo.PointerLevel > 0
                : typeInfo.PointerLevel > 1;

            return GetTypeElementSize(typeInfo.Type, elementIsPointer);
        }

        private static int GetArrayStride(CTypeInfo typeInfo, int dimensionIndex, int scalarElementSize)
        {
            if (typeInfo.ArrayDimensions.Count == 0)
                return scalarElementSize;

            int stride = scalarElementSize;
            for (int i = dimensionIndex + 1; i < typeInfo.ArrayDimensions.Count; i++)
                stride *= typeInfo.ArrayDimensions[i];
            return stride;
        }

        private int GetSizeOfType(CTypeInfo typeInfo)
        {
            if (typeInfo == null)
                throw new Exception("Execution Error: invalid sizeof type");

            if (typeInfo.IsArray)
            {
                int elementSize = typeInfo.IsStruct && !typeInfo.IsPointer
                    ? GetStructSize(typeInfo.StructName)
                    : GetTypeElementSize(typeInfo.Type, typeInfo.PointerLevel > 0);
                return elementSize * GetArrayTotalLength(typeInfo);
            }

            if (typeInfo.PointerLevel > 0)
                return 4;

            if (typeInfo.IsStruct)
                return GetStructSize(typeInfo.StructName);

            return GetTypeElementSize(typeInfo.Type, false);
        }

        private bool TryGetStructValueType(IASTNode expr, out string structName)
        {
            if (expr is VariableNode v && Env.TryGetValue(v.Name, out var info))
            {
                if (info.IsStruct && !info.IsPointer)
                {
                    structName = info.StructName;
                    return true;
                }
            }

            if (expr is ArrayAccessNode aa)
                return TryGetArrayAccessStructTypeNoEval(aa, out structName);

            if (expr is StructMemberAccessNode sm &&
                TryGetStructValueType(sm.Target, out string baseStructName))
            {
                var field = GetStructFieldInfo(baseStructName, sm.MemberName).field;
                if (field.IsStruct && !field.IsPointer)
                {
                    structName = field.StructName;
                    return true;
                }
            }

            if (expr is StructPointerMemberAccessNode spm &&
                TryGetStructPointerType(spm.Target, out string pointerStructName))
            {
                var field = GetStructFieldInfo(pointerStructName, spm.MemberName).field;
                if (field.IsStruct && !field.IsPointer)
                {
                    structName = field.StructName;
                    return true;
                }
            }

            if (expr is UnaryOpNode deref && deref.Operator == "*" &&
                TryGetStructPointerType(deref.Target, out structName))
                return true;

            structName = null;
            return false;
        }

        private bool TryGetArrayAccessStructTypeNoEval(ArrayAccessNode access, out string structName)
        {
            if (access.Target is VariableNode v &&
                Env.TryGetValue(v.Name, out var info) &&
                info.IsArray && info.IsStruct)
            {
                structName = info.StructName;
                return true;
            }

            if (access.Target is StructMemberAccessNode sm &&
                TryGetStructValueType(sm.Target, out string baseStructName))
            {
                var field = GetStructFieldInfo(baseStructName, sm.MemberName).field;
                if (field.IsArray && field.IsStruct)
                {
                    structName = field.StructName;
                    return true;
                }
            }

            if (access.Target is StructPointerMemberAccessNode spm &&
                TryGetStructPointerType(spm.Target, out string ptrStructName))
            {
                var field = GetStructFieldInfo(ptrStructName, spm.MemberName).field;
                if (field.IsArray && field.IsStruct)
                {
                    structName = field.StructName;
                    return true;
                }
            }

            if (TryGetStructPointerType(access.Target, out structName))
                return true;

            structName = null;
            return false;
        }

        private int GetSizeOfExpression(IASTNode expr)
        {
            if (expr is VariableNode varNode)
            {
                var info = Env[varNode.Name];
                return info.Size;
            }

            if (expr is StructMemberAccessNode memberAccess &&
                TryGetStructValueType(memberAccess.Target, out string baseStructName))
            {
                var field = GetStructFieldInfo(baseStructName, memberAccess.MemberName).field;
                return GetStructFieldSize(field);
            }

            if (expr is StructPointerMemberAccessNode pointerMemberAccess &&
                TryGetStructPointerType(pointerMemberAccess.Target, out string structName))
            {
                var field = GetStructFieldInfo(structName, pointerMemberAccess.MemberName).field;
                return GetStructFieldSize(field);
            }

            if (expr is ArrayAccessNode arrayAccess)
            {
                if (TryGetIndexedArrayRoot(arrayAccess, out var rootInfo, out var indices, out _))
                {
                    int scalarSize = GetArrayScalarElementSize(rootInfo.TypeInfo, rootInfo.StructSize);
                    int rank = rootInfo.ArrayDimensions.Count > 0 ? rootInfo.ArrayDimensions.Count : 1;
                    if (indices.Count >= rank)
                        return scalarSize;

                    return GetArrayStride(rootInfo.TypeInfo, indices.Count - 1, scalarSize);
                }

                if (TryGetArrayAccessStructTypeNoEval(arrayAccess, out string elementStructName))
                    return GetStructSize(elementStructName);

                if (arrayAccess.Target is VariableNode baseVar &&
                    Env.TryGetValue(baseVar.Name, out var baseInfo) &&
                    baseInfo.IsArray)
                    return baseInfo.ElementSize;

                if (arrayAccess.Target is StructMemberAccessNode memberArray &&
                    TryGetStructValueType(memberArray.Target, out string memberBaseStructName))
                {
                    var field = GetStructFieldInfo(memberBaseStructName, memberArray.MemberName).field;
                    if (field.IsArray)
                        return GetStructFieldElementSize(field);
                }

                if (arrayAccess.Target is StructPointerMemberAccessNode pointerMemberArray &&
                    TryGetStructPointerType(pointerMemberArray.Target, out string pointerBaseStructName))
                {
                    var field = GetStructFieldInfo(pointerBaseStructName, pointerMemberArray.MemberName).field;
                    if (field.IsArray)
                        return GetStructFieldElementSize(field);
                }

                if (TryGetPointeeType(arrayAccess.Target, out string elementType, out int elementPointerLevel))
                    return GetTypeElementSize(elementType, elementPointerLevel > 0);

                return 4;
            }

            if (expr is UnaryOpNode unary)
            {
                if (unary.Operator == "&")
                    return 4;

                if (unary.Operator == "*")
                {
                    if (TryGetStructPointerType(unary.Target, out string pointeeStructName))
                        return GetStructSize(pointeeStructName);

                    if (TryGetPointeeType(unary.Target, out string pointeeType, out int pointeeLevel))
                        return GetTypeElementSize(pointeeType, pointeeLevel > 0);

                    return 4;
                }

                return GetSizeOfExpression(unary.Target);
            }

            if (expr is CastNode cast)
                return GetSizeOfType(cast.TargetTypeInfo);

            if (expr is FunctionCallNode call &&
                _functions.TryGetValue(call.FunctionName, out var fn))
                return GetSizeOfType(fn.ReturnTypeInfo);

            if (expr is StringNode str)
                return Encoding.UTF8.GetByteCount(str.Value) + 1;

            if (expr is SizeOfNode)
                return 4;

            return 4;
        }

        private void BindVariable(string name, VarInfo info)
        {
            if (_scopes.Count == 0)
                throw new Exception("Execution Error: no active scope");

            var frame = _scopes.Peek();

            if (!frame.DeclaredNames.Contains(name))
            {
                if (Env.TryGetValue(name, out var previous))
                    frame.PreviousBindings[name] = previous.Clone();

                frame.DeclaredNames.Add(name);
            }

            Env[name] = info;
        }

        private bool TryGetPointeeType(IASTNode expr, out string type, out bool isPointer)
        {
            if (TryGetPointeeType(expr, out type, out int pointerLevel))
            {
                isPointer = pointerLevel > 0;
                return true;
            }

            isPointer = false;
            return false;
        }

        private bool TryGetPointeeType(IASTNode expr, out string type, out int pointerLevel)
        {
            if (expr is CastNode cast && cast.TargetTypeInfo.PointerLevel > 0)
            {
                type = cast.TargetTypeInfo.Type;
                pointerLevel = cast.TargetTypeInfo.PointerLevel - 1;
                return true;
            }

            if (expr is StructMemberAccessNode member)
            {
                if (TryGetStructValueInfo(member.Target, out string baseStructName, out _))
                {
                    var field = GetStructFieldInfo(baseStructName, member.MemberName).field;
                    type = field.Type;
                    pointerLevel = field.IsArray ? field.PointerLevel : Math.Max(0, field.PointerLevel - 1);
                    return true;
                }
            }

            if (expr is StructPointerMemberAccessNode pointerMember)
            {
                if (TryGetStructPointerType(pointerMember.Target, out string structName))
                {
                    var field = GetStructFieldInfo(structName, pointerMember.MemberName).field;
                    type = field.Type;
                    pointerLevel = field.IsArray ? field.PointerLevel : Math.Max(0, field.PointerLevel - 1);
                    return true;
                }
            }

            if (expr is VariableNode v && Env.TryGetValue(v.Name, out var varInfo))
            {
                if (varInfo.IsArray)
                {
                    type = varInfo.Type;
                    pointerLevel = varInfo.PointerLevel;
                    return true;
                }

                if (varInfo.PointerLevel > 0)
                {
                    type = varInfo.Type;
                    pointerLevel = Math.Max(0, varInfo.PointerLevel - 1);
                    return true;
                }
            }

            if (expr is FunctionCallNode call &&
                _functions.TryGetValue(call.FunctionName, out var fn) &&
                fn.ReturnPointerLevel > 0)
            {
                type = fn.ReturnType;
                pointerLevel = fn.ReturnPointerLevel - 1;
                return true;
            }

            if (expr is StringNode)
            {
                type = "char";
                pointerLevel = 0;
                return true;
            }

            if (expr is BinaryOpNode b && (b.Operator == "+" || b.Operator == "-"))
            {
                if (TryGetPointeeType(b.Left, out type, out pointerLevel))
                    return true;

                if (TryGetPointeeType(b.Right, out type, out pointerLevel))
                    return true;
            }

            if (expr is ArrayAccessNode access)
            {
                if (TryGetIndexedArrayRoot(access, out var baseInfo, out var indices, out _))
                {
                    int rank = baseInfo.ArrayDimensions.Count > 0 ? baseInfo.ArrayDimensions.Count : 1;
                    int remainingDimensions = rank - indices.Count;
                    if (remainingDimensions > 0)
                    {
                        type = baseInfo.Type;
                        pointerLevel = baseInfo.IsArray
                            ? baseInfo.PointerLevel
                            : Math.Max(0, baseInfo.PointerLevel - 1);
                        return true;
                    }

                    int expressionPointerLevel = baseInfo.IsArray
                        ? baseInfo.PointerLevel
                        : Math.Max(0, baseInfo.PointerLevel - rank);
                    expressionPointerLevel = Math.Max(0, expressionPointerLevel - Math.Max(0, indices.Count - rank));
                    if (expressionPointerLevel > 0)
                    {
                        type = baseInfo.Type;
                        pointerLevel = expressionPointerLevel - 1;
                        return true;
                    }

                    type = null;
                    pointerLevel = 0;
                    return false;
                }

                if (TryGetPointeeType(access.Target, out type, out pointerLevel) &&
                    pointerLevel > 0)
                {
                    pointerLevel -= 1;
                    return true;
                }
            }

            if (expr is UnaryOpNode u && u.Operator == "&")
            {
                if (u.Target is VariableNode vt && Env.TryGetValue(vt.Name, out var addrInfo))
                {
                    type = addrInfo.Type;
                    pointerLevel = addrInfo.PointerLevel;
                    return true;
                }

                if (u.Target is StructMemberAccessNode memberTarget)
                {
                    if (TryGetStructValueInfo(memberTarget.Target, out string baseStructName, out _))
                    {
                        var field = GetStructFieldInfo(baseStructName, memberTarget.MemberName).field;
                        type = field.Type;
                        pointerLevel = field.PointerLevel;
                        return true;
                    }
                }

                if (u.Target is StructPointerMemberAccessNode ptrMemberTarget &&
                    TryGetStructPointerType(ptrMemberTarget.Target, out string ptrStructName))
                {
                    var field = GetStructFieldInfo(ptrStructName, ptrMemberTarget.MemberName).field;
                    type = field.Type;
                    pointerLevel = field.PointerLevel;
                    return true;
                }

                if (u.Target is ArrayAccessNode aa && TryGetPointeeType(aa.Target, out type, out pointerLevel))
                    return true;
            }

            type = null;
            pointerLevel = 0;
            return false;
        }

        private int GetIndexedAddress(ArrayAccessNode access)
        {
            if (TryGetDereferencedPointerArrayRoot(access, out var pointerArrayInfo, out var derefIndices, out string pointerArrayName))
            {
                int scalarSize = GetArrayScalarElementSize(pointerArrayInfo.TypeInfo, pointerArrayInfo.StructSize);
                int addr = ReadScalarAtAddress(pointerArrayInfo.Type, pointerArrayInfo.IsPointer, pointerArrayInfo.Address);

                for (int i = 0; i < derefIndices.Count; i++)
                {
                    int dimIndex = i + 1;
                    int indexValue = Convert.ToInt32(EvaluateExpression(derefIndices[i]));
                    int dimLength = pointerArrayInfo.ArrayDimensions.Count > dimIndex ? pointerArrayInfo.ArrayDimensions[dimIndex] : 0;
                    if (dimLength > 0 && (indexValue < 0 || indexValue >= dimLength))
                        throw new Exception($"Execution Error: array index out of range: {pointerArrayName}[{indexValue}]");

                    addr += indexValue * GetArrayStride(pointerArrayInfo.TypeInfo, dimIndex, scalarSize);
                }

                return addr;
            }

            if (TryGetIndexedArrayRoot(access, out var baseInfo, out var indices, out string baseName))
            {
                int scalarSize = GetArrayScalarElementSize(baseInfo.TypeInfo, baseInfo.StructSize);
                int addr = baseInfo.IsArray ? baseInfo.Address : ReadScalarAtAddress(baseInfo.Type, baseInfo.IsPointer, baseInfo.Address);
                int rank = baseInfo.ArrayDimensions.Count > 0 ? baseInfo.ArrayDimensions.Count : 1;

                if (indices.Count > rank && baseInfo.PointerLevel > 0)
                {
                    for (int i = 0; i < rank; i++)
                    {
                        int indexValue = Convert.ToInt32(EvaluateExpression(indices[i]));
                        int dimLength = baseInfo.ArrayDimensions.Count > i ? baseInfo.ArrayDimensions[i] : baseInfo.ArrayLength;
                        if (dimLength > 0 && (indexValue < 0 || indexValue >= dimLength))
                            throw new Exception($"Execution Error: array index out of range: {baseName}[{indexValue}]");

                        addr += indexValue * GetArrayStride(baseInfo.TypeInfo, i, scalarSize);
                    }

                    addr = ReadScalarAtAddress(baseInfo.Type, true, addr);
                    int pointerLevel = baseInfo.IsArray
                        ? baseInfo.PointerLevel
                        : Math.Max(0, baseInfo.PointerLevel - 1);

                    for (int i = rank; i < indices.Count; i++)
                    {
                        int indexValue = Convert.ToInt32(EvaluateExpression(indices[i]));
                        int pointerElementSize = GetTypeElementSize(baseInfo.Type, pointerLevel > 1);
                        addr += indexValue * pointerElementSize;
                        if (pointerLevel > 0)
                            pointerLevel--;
                    }

                    return addr;
                }

                for (int i = 0; i < indices.Count; i++)
                {
                    int indexValue = Convert.ToInt32(EvaluateExpression(indices[i]));
                    int dimLength = baseInfo.ArrayDimensions.Count > i ? baseInfo.ArrayDimensions[i] : baseInfo.ArrayLength;
                    if (dimLength > 0 && (indexValue < 0 || indexValue >= dimLength))
                        throw new Exception($"Execution Error: array index out of range: {baseName}[{indexValue}]");

                    addr += indexValue * GetArrayStride(baseInfo.TypeInfo, i, scalarSize);
                }

                return addr;
            }

            int index = Convert.ToInt32(EvaluateExpression(access.Index));
            int baseAddr = Convert.ToInt32(EvaluateExpression(access.Target));

            if (access.Target is VariableNode varNode &&
                Env.TryGetValue(varNode.Name, out var variableArrayInfo) &&
                variableArrayInfo.IsArray)
            {
                if (index < 0 || index >= variableArrayInfo.ArrayLength)
                    throw new Exception($"Execution Error: array index out of range: {varNode.Name}[{index}]");

                return variableArrayInfo.Address + index * variableArrayInfo.ElementSize;
            }

            if (access.Target is StructMemberAccessNode memberAccess &&
                TryGetStructValueInfo(memberAccess.Target, out string baseStructName, out _))
            {
                var field = GetStructFieldInfo(baseStructName, memberAccess.MemberName).field;
                if (field.IsArray)
                {
                    if (index < 0 || index >= field.ArrayLength)
                        throw new Exception($"Execution Error: array index out of range: {memberAccess.MemberName}[{index}]");

                    return baseAddr + index * GetStructFieldElementSize(field);
                }
            }

            if (access.Target is StructPointerMemberAccessNode pointerMemberAccess &&
                TryGetStructPointerType(pointerMemberAccess.Target, out string structName))
            {
                var field = GetStructFieldInfo(structName, pointerMemberAccess.MemberName).field;
                if (field.IsArray)
                {
                    if (index < 0 || index >= field.ArrayLength)
                        throw new Exception($"Execution Error: array index out of range: {pointerMemberAccess.MemberName}[{index}]");

                    return baseAddr + index * GetStructFieldElementSize(field);
                }
            }

            int elementSize = GetPointeeElementSize(access.Target);
            return baseAddr + index * elementSize;
        }

        private bool TryGetDereferencedPointerArrayRoot(ArrayAccessNode access, out VarInfo baseInfo, out List<IASTNode> indices, out string baseName)
        {
            indices = new List<IASTNode>();
            IASTNode node = access;
            while (node is ArrayAccessNode current)
            {
                indices.Insert(0, current.Index);
                node = current.Target;
            }

            if (node is UnaryOpNode { Operator: "*" } deref &&
                deref.Target is VariableNode variable &&
                Env.TryGetValue(variable.Name, out baseInfo) &&
                IsPointerToArray(baseInfo))
            {
                baseName = variable.Name;
                return true;
            }

            baseInfo = null;
            baseName = null;
            return false;
        }

        private bool TryGetIndexedArrayRoot(ArrayAccessNode access, out VarInfo baseInfo, out List<IASTNode> indices, out string baseName)
        {
            indices = new List<IASTNode>();
            IASTNode node = access;
            while (node is ArrayAccessNode current)
            {
                indices.Insert(0, current.Index);
                node = current.Target;
            }

            if (node is VariableNode variable &&
                Env.TryGetValue(variable.Name, out baseInfo) &&
                (baseInfo.IsArray || (baseInfo.PointerLevel > 0 && baseInfo.ArrayDimensions.Count > 0)))
            {
                baseName = variable.Name;
                return true;
            }

            baseInfo = null;
            baseName = null;
            return false;
        }

        private bool IsArrayAccessToSubarray(ArrayAccessNode access)
        {
            if (!TryGetIndexedArrayRoot(access, out var baseInfo, out var indices, out _))
                return false;

            int rank = baseInfo.ArrayDimensions.Count > 0 ? baseInfo.ArrayDimensions.Count : 1;
            return indices.Count < rank;
        }

        private int ReadTarget(IASTNode target)
        {
            if (target is VariableNode varNode)
            {
                if (varNode.Name == "NULL")
                    return 0;

                var info = Env[varNode.Name];

                if (info.IsArray)
                    return info.Address;

                if (info.IsStruct && !info.IsPointer)
                    throw new Exception($"Execution Error: struct variable '{varNode.Name}' cannot be used as scalar value");

                return ReadScalarAtAddress(info.Type, info.IsPointer, info.Address);
            }

            if (target is StructMemberAccessNode memberAccess)
            {
                int addr = GetStructMemberAddress(memberAccess);

                if (TryGetStructValueInfo(memberAccess.Target, out string baseStructName, out _))
                {
                    var field = GetStructFieldInfo(baseStructName, memberAccess.MemberName).field;
                    if (field.IsArray)
                        return addr;
                    if (field.IsStruct && !field.IsPointer)
                        return addr;
                    return ReadScalarAtAddress(field.Type, field.IsPointer, addr);
                }

                throw new Exception("Execution Error: invalid struct member access");
            }

            if (target is StructPointerMemberAccessNode pointerMemberAccess)
            {
                int addr = GetStructPointerMemberAddress(pointerMemberAccess);

                if (TryGetStructPointerType(pointerMemberAccess.Target, out string structName))
                {
                    var field = GetStructFieldInfo(structName, pointerMemberAccess.MemberName).field;
                    if (field.IsArray)
                        return addr;
                    if (field.IsStruct && !field.IsPointer)
                        return addr;
                    return ReadScalarAtAddress(field.Type, field.IsPointer, addr);
                }

                throw new Exception("Execution Error: invalid struct pointer member access");
            }

            if (target is ArrayAccessNode arrayAccess)
            {
                int addr = GetIndexedAddress(arrayAccess);

                if (IsArrayAccessToSubarray(arrayAccess))
                    return addr;

                if (TryGetArrayAccessStructType(arrayAccess, out _))
                    return addr;

                if (TryGetPointeeType(arrayAccess.Target, out string elementType, out int elementPointerLevel))
                    return ReadScalarAtAddress(elementType, elementPointerLevel > 0, addr);

                return ReadInt(addr);
            }

            if (target is UnaryOpNode unary && unary.Operator == "*")
            {
                int addr = Convert.ToInt32(EvaluateExpression(unary.Target));

                if (TryGetStructPointerType(unary.Target, out _))
                    return addr;

                if (IsPointerToArrayExpression(unary.Target, out _))
                    return addr;

                if (TryGetPointeeType(unary.Target, out string pointeeType, out int pointeeLevel))
                    return ReadScalarAtAddress(pointeeType, pointeeLevel > 0, addr);

                return ReadInt(addr);
            }

            throw new Exception("Execution Error: invalid read target");
        }

        private void WriteTarget(IASTNode target, int value)
        {
            if (target is VariableNode varNode)
            {
                var info = Env[varNode.Name];

                if (info.IsStruct && !info.IsPointer)
                    throw new Exception($"Execution Error: cannot assign scalar value to struct variable '{varNode.Name}'");

                if (info.IsArray)
                    throw new Exception($"Execution Error: cannot assign to array '{varNode.Name}' directly");

                WriteScalarAtAddress(info.Type, info.IsPointer, info.Address, value);
                return;
            }

            if (target is StructMemberAccessNode memberAccess)
            {
                int addr = GetStructMemberAddress(memberAccess);

                if (TryGetStructValueInfo(memberAccess.Target, out string baseStructName, out _))
                {
                    var field = GetStructFieldInfo(baseStructName, memberAccess.MemberName).field;
                    if (field.IsArray)
                        throw new Exception("Execution Error: cannot assign to array field directly");
                    if (field.IsStruct && !field.IsPointer)
                        throw new Exception("Execution Error: cannot assign to struct field as scalar directly");
                    WriteScalarAtAddress(field.Type, field.IsPointer, addr, value);
                    return;
                }

                throw new Exception("Execution Error: invalid struct member access");
            }

            if (target is StructPointerMemberAccessNode pointerMemberAccess)
            {
                int addr = GetStructPointerMemberAddress(pointerMemberAccess);

                if (TryGetStructPointerType(pointerMemberAccess.Target, out string structName))
                {
                    var field = GetStructFieldInfo(structName, pointerMemberAccess.MemberName).field;
                    if (field.IsArray)
                        throw new Exception("Execution Error: cannot assign to array field directly");
                    if (field.IsStruct && !field.IsPointer)
                        throw new Exception("Execution Error: cannot assign to struct field as scalar directly");
                    WriteScalarAtAddress(field.Type, field.IsPointer, addr, value);
                    return;
                }

                throw new Exception("Execution Error: invalid struct pointer member access");
            }

            if (target is ArrayAccessNode arrayAccess)
            {
                int addr = GetIndexedAddress(arrayAccess);

                if (IsArrayAccessToSubarray(arrayAccess))
                    throw new Exception("Execution Error: cannot assign to array element subarray directly");

                if (TryGetPointeeType(arrayAccess.Target, out string elementType, out int elementPointerLevel))
                {
                    WriteScalarAtAddress(elementType, elementPointerLevel > 0, addr, value);
                    return;
                }

                WriteInt(addr, value);
                return;
            }

            if (target is UnaryOpNode unary && unary.Operator == "*")
            {
                int addr = Convert.ToInt32(EvaluateExpression(unary.Target));

                if (TryGetStructPointerType(unary.Target, out _))
                    throw new Exception("Execution Error: cannot assign to struct value through '*' as scalar directly");

                if (TryGetPointeeType(unary.Target, out string pointeeType, out int pointeeLevel))
                {
                    WriteScalarAtAddress(pointeeType, pointeeLevel > 0, addr, value);
                    return;
                }

                WriteInt(addr, value);
                return;
            }

            throw new Exception("Execution Error: invalid write target");
        }

        private int ApplyAssignmentOperator(string op, IASTNode left, int currentValue, int rightValue)
        {
            if (op == "+=" || op == "-=")
            {
                int elementSize = GetPointeeElementSize(left);
                int pointeeLevel;
                if (elementSize != 4 || TryGetPointeeType(left, out _, out pointeeLevel))
                    rightValue *= elementSize;
            }

            return op switch
            {
                "=" => rightValue,
                "+=" => currentValue + rightValue,
                "-=" => currentValue - rightValue,
                "*=" => currentValue * rightValue,
                "/=" => currentValue / rightValue,
                "%=" => currentValue % rightValue,
                "&=" => currentValue & rightValue,
                "|=" => currentValue | rightValue,
                "^=" => currentValue ^ rightValue,
                "<<=" => currentValue << rightValue,
                ">>=" => currentValue >> rightValue,
                _ => throw new Exception($"Execution Error: unsupported assignment operator '{op}'")
            };
        }

        private int GetInitializerArrayLength(VarDeclNode v)
        {
            if (v.Initializer is ArrayInitializerNode arrayInit)
                return arrayInit.Elements.Count;

            if (v.Initializer is StringNode strInit && v.Type == "char")
                return strInit.Value.Length + 1;

            return v.ArrayLength;
        }

        private void InitializeArray(VarInfo info, IASTNode initializer)
        {
            if (initializer == null)
                return;

            if (initializer is ArrayInitializerNode arrayInit)
            {
                InitializeArrayElements(info, arrayInit.Elements);
                return;
            }

            if (initializer is StructInitializerNode structInit)
            {
                InitializeArrayElements(info, structInit.Elements);
                return;
            }

            if (initializer is StringNode strInit && info.Type == "char")
            {
                int needed = strInit.Value.Length + 1;
                if (needed > info.ArrayLength)
                    throw new Exception("Execution Error: string initializer is too long for char array");

                int literalAddr = EnsureStringLiteral(strInit.Value);
                CopyBytes(literalAddr, info.Address, needed);
                return;
            }

            throw new Exception("Execution Error: invalid array initializer");
        }

        private void InitializeArrayElements(VarInfo info, List<IASTNode> elements)
        {
            if (info.ArrayDimensions.Count > 1)
            {
                InitializeArrayElements(info, elements, 0, info.Address);
                return;
            }

            if (elements.Count > info.ArrayLength)
                throw new Exception($"Execution Error: too many initializer elements for array at 0x{info.Address:X4}");

            for (int i = 0; i < elements.Count; i++)
            {
                int addr = info.Address + i * info.ElementSize;
                var elem = elements[i];

                if (info.IsStruct && !info.IsPointer)
                {
                    var elementInfo = new VarInfo
                    {
                        Address = addr,
                        StructSize = info.StructSize
                    };
                    elementInfo.TypeInfo.Type = "struct";
                    elementInfo.TypeInfo.IsPointer = false;
                    elementInfo.TypeInfo.IsArray = false;
                    elementInfo.TypeInfo.ArrayLength = 0;
                    elementInfo.TypeInfo.IsStruct = true;
                    elementInfo.TypeInfo.StructName = info.StructName;

                    InitializeStruct(elementInfo, elem);
                    continue;
                }

                int value = Convert.ToInt32(EvaluateExpression(elem));
                WriteScalarAtAddress(info.Type, info.IsPointer, addr, value);
            }
        }

        private void InitializeArrayElements(VarInfo info, List<IASTNode> elements, int dimensionIndex, int baseAddress)
        {
            int rank = info.ArrayDimensions.Count;
            int length = info.ArrayDimensions[dimensionIndex];
            if (elements.Count > length)
                throw new Exception($"Execution Error: too many initializer elements for array at 0x{baseAddress:X4}");

            int scalarSize = GetArrayScalarElementSize(info.TypeInfo, info.StructSize);
            int stride = GetArrayStride(info.TypeInfo, dimensionIndex, scalarSize);

            for (int i = 0; i < elements.Count; i++)
            {
                int addr = baseAddress + i * stride;
                var elem = elements[i];

                if (dimensionIndex < rank - 1)
                {
                    if (elem is not ArrayInitializerNode nested)
                        throw new Exception("Execution Error: nested array initializer is required");

                    InitializeArrayElements(info, nested.Elements, dimensionIndex + 1, addr);
                    continue;
                }

                if (info.IsStruct && !info.IsPointer)
                {
                    var elementInfo = new VarInfo
                    {
                        Address = addr,
                        StructSize = info.StructSize
                    };
                    elementInfo.TypeInfo.Type = "struct";
                    elementInfo.TypeInfo.IsPointer = false;
                    elementInfo.TypeInfo.IsArray = false;
                    elementInfo.TypeInfo.ArrayLength = 0;
                    elementInfo.TypeInfo.IsStruct = true;
                    elementInfo.TypeInfo.StructName = info.StructName;

                    InitializeStruct(elementInfo, elem);
                    continue;
                }

                int value = Convert.ToInt32(EvaluateExpression(elem));
                WriteScalarAtAddress(info.Type, info.IsPointer, addr, value);
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

        private int EvaluateFunctionCall(FunctionCallNode call)
        {
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

        private void ExecuteStatement(IASTNode stmt)
        {
            if (_hasReturn || stmt == null) return;
            if (_breakRequested || _continueRequested) return;

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

        private object EvaluateExpression(IASTNode expr)
        {


            if (expr is SizeOfNode sizeOf)
                return sizeOf.IsTypeName
                    ? GetSizeOfType(sizeOf.TypeInfo)
                    : GetSizeOfExpression(sizeOf.Expression);

            if (expr is CastNode cast)
            {
                int value = Convert.ToInt32(EvaluateExpression(cast.Expression));

                if (cast.TargetTypeInfo.PointerLevel > 0)
                    return value;

                return cast.TargetTypeInfo.Type switch
                {
                    "char" => (byte)value,
                    "short" => (short)value,
                    "int" => value,
                    "long" => value,
                    _ => value
                };
            }


            if (expr is NumberNode n) return n.Value;
            if (expr is CharLiteralNode c) return (int)c.Value;
            if (expr is StringNode s) return EnsureStringLiteral(s.Value);

            if (expr is FunctionCallNode call)
                return EvaluateFunctionCall(call);

            if (expr is VariableNode v)
                return ReadTarget(v);

            if (expr is StructMemberAccessNode memberAccessExpr)
                return ReadTarget(memberAccessExpr);

            if (expr is StructPointerMemberAccessNode pointerMemberAccessExpr)
                return ReadTarget(pointerMemberAccessExpr);

            if (expr is ArrayAccessNode access)
                return ReadTarget(access);

            if (expr is PostfixOpNode postfix)
            {
                int oldValue = ReadTarget(postfix.Target);
                int step = GetIncDecStep(postfix.Target);
                int newValue = postfix.Operator == "++" ? oldValue + step : oldValue - step;
                WriteTarget(postfix.Target, newValue);
                return oldValue;
            }

            if (expr is UnaryOpNode u)
            {
                if (u.Operator == "&")
                {
                    if (u.Target is VariableNode vt)
                        return Env[vt.Name].Address;

                    if (u.Target is StructMemberAccessNode memberTarget)
                        return GetStructMemberAddress(memberTarget);

                    if (u.Target is StructPointerMemberAccessNode pointerMemberTarget)
                        return GetStructPointerMemberAddress(pointerMemberTarget);

                    if (u.Target is ArrayAccessNode aa)
                        return GetIndexedAddress(aa);

                    throw new Exception("Execution Error: '&' target must be a variable, struct member, pointer member, or indexed element");
                }

                if (u.Operator == "*")
                    return ReadTarget(u);

                if (u.Operator == "-")
                    return -Convert.ToInt32(EvaluateExpression(u.Target));

                if (u.Operator == "!")
                    return Convert.ToInt32(EvaluateExpression(u.Target)) == 0 ? 1 : 0;

                if (u.Operator == "~")
                    return ~Convert.ToInt32(EvaluateExpression(u.Target));

                if (u.Operator == "++" || u.Operator == "--")
                {
                    int oldValue = ReadTarget(u.Target);
                    int step = GetIncDecStep(u.Target);
                    int newValue = u.Operator == "++" ? oldValue + step : oldValue - step;
                    WriteTarget(u.Target, newValue);
                    return newValue;
                }
            }

            if (expr is ConditionalOpNode conditional)
            {
                int conditionValue = Convert.ToInt32(EvaluateExpression(conditional.Condition));
                return conditionValue != 0
                    ? EvaluateExpression(conditional.TrueExpression)
                    : EvaluateExpression(conditional.FalseExpression);
            }

            if (expr is BinaryOpNode b)
            {
                if (b.Operator == "&&")
                {
                    int leftBool = Convert.ToInt32(EvaluateExpression(b.Left));
                    if (leftBool == 0) return 0;

                    int rightBool = Convert.ToInt32(EvaluateExpression(b.Right));
                    return rightBool != 0 ? 1 : 0;
                }

                if (b.Operator == "||")
                {
                    int leftBool = Convert.ToInt32(EvaluateExpression(b.Left));
                    if (leftBool != 0) return 1;

                    int rightBool = Convert.ToInt32(EvaluateExpression(b.Right));
                    return rightBool != 0 ? 1 : 0;
                }

                int left = Convert.ToInt32(EvaluateExpression(b.Left));
                int right = Convert.ToInt32(EvaluateExpression(b.Right));

                if (b.Operator == "-" &&
                    TryGetPointerDifferenceElementSize(b.Left, b.Right, out int differenceElementSize))
                {
                    return (left - right) / differenceElementSize;
                }

                if (b.Operator == "+" || b.Operator == "-")
                {
                    int elementSize = GetPointeeElementSize(b.Left);
                    int pointeeLevel;
                    if (elementSize != 4 || TryGetPointeeType(b.Left, out _, out pointeeLevel))
                        right *= elementSize;
                }

                return b.Operator switch
                {
                    "+" => left + right,
                    "-" => left - right,
                    "*" => left * right,
                    "/" => left / right,
                    "%" => left % right,
                    "<<" => left << right,
                    ">>" => left >> right,
                    "&" => left & right,
                    "^" => left ^ right,
                    "|" => left | right,
                    "==" => left == right ? 1 : 0,
                    "!=" => left != right ? 1 : 0,
                    "<" => left < right ? 1 : 0,
                    "<=" => left <= right ? 1 : 0,
                    ">" => left > right ? 1 : 0,
                    ">=" => left >= right ? 1 : 0,
                    _ => 0
                };
            }

            return null;
        }
    }
}
