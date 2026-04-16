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
            IsArray ? ElementSize * ArrayLength :
            IsPointer ? 4 :
            IsStruct ? StructSize :
            ElementSize;

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

        public byte[] Memory { get; } = new byte[1024];
        public Dictionary<string, VarInfo> Env { get; } = new Dictionary<string, VarInfo>();
        public List<MemoryRegionInfo> Regions { get; } = new List<MemoryRegionInfo>();
        public List<ExecutionSnapshot> Snapshots { get; } = new List<ExecutionSnapshot>();
        public ExecutionSnapshot LastSnapshot => Snapshots.Count == 0 ? null : Snapshots[Snapshots.Count - 1];

        private readonly Dictionary<string, int> _stringLiteralPool = new Dictionary<string, int>();
        private readonly Stack<ScopeFrame> _scopes = new Stack<ScopeFrame>();
        private readonly Dictionary<string, FunctionDeclNode> _functions = new Dictionary<string, FunctionDeclNode>();
        private readonly Dictionary<string, StructDeclNode> _structs = new Dictionary<string, StructDeclNode>();

        private int _stackPtr = 0;
        private int _literalPtr = 1024;
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

            _stackPtr = 0;
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
                    if (_functions.ContainsKey(f.Name))
                        throw new Exception($"Execution Error: duplicate function '{f.Name}'");
                    _functions[f.Name] = f;
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

        private int GetStructFieldSize(StructFieldDecl field)
        {
            if (field.IsPointer) return 4;
            if (field.IsStruct) return GetStructSize(field.StructName);
            return field.Type == "char" ? 1 : 4;
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

                    if (field.IsStruct && !field.IsPointer)
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

            if (TryGetPointeeType(expr, out string type, out int pointerLevel))
                return GetTypeElementSize(type, pointerLevel > 0);

            return 4;
        }

        private int CallUserFunction(FunctionCallNode call)
        {
            if (!_functions.TryGetValue(call.FunctionName, out var fn))
                throw new Exception($"Execution Error: function '{call.FunctionName}' not found");

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

        private void EnsureSpaceForStackAllocation(int size)
        {
            if (size <= 0)
                throw new Exception("Execution Error: invalid allocation size");

            if (_stackPtr + size > _literalPtr)
                throw new Exception("Execution Error: out of memory");
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
            EnsureSpaceForStackAllocation(size);

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
            if (expr is StructMemberAccessNode member)
            {
                if (TryGetStructValueInfo(member.Target, out string baseStructName, out _))
                {
                    var field = GetStructFieldInfo(baseStructName, member.MemberName).field;
                    type = field.Type;
                    pointerLevel = Math.Max(0, field.PointerLevel - 1);
                    return true;
                }
            }

            if (expr is StructPointerMemberAccessNode pointerMember)
            {
                if (TryGetStructPointerType(pointerMember.Target, out string structName))
                {
                    var field = GetStructFieldInfo(structName, pointerMember.MemberName).field;
                    type = field.Type;
                    pointerLevel = Math.Max(0, field.PointerLevel - 1);
                    return true;
                }
            }

            if (expr is VariableNode v && Env.TryGetValue(v.Name, out var varInfo))
            {
                if (varInfo.IsArray)
                {
                    type = varInfo.Type;
                    pointerLevel = Math.Max(0, varInfo.PointerLevel - 1);
                    return true;
                }

                if (varInfo.IsPointer)
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
                if (TryGetPointeeType(access.Target, out type, out pointerLevel))
                    return true;
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
            int index = Convert.ToInt32(EvaluateExpression(access.Index));
            int baseAddr = Convert.ToInt32(EvaluateExpression(access.Target));

            if (access.Target is VariableNode varNode &&
                Env.TryGetValue(varNode.Name, out var baseInfo) &&
                baseInfo.IsArray)
            {
                if (index < 0 || index >= baseInfo.ArrayLength)
                    throw new Exception($"Execution Error: array index out of range: {varNode.Name}[{index}]");

                return baseInfo.Address + index * baseInfo.ElementSize;
            }

            int elementSize = GetPointeeElementSize(access.Target);
            return baseAddr + index * elementSize;
        }

        private int ReadTarget(IASTNode target)
        {
            if (target is VariableNode varNode)
            {
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
                    if (field.IsStruct && !field.IsPointer)
                        return addr;
                    return ReadScalarAtAddress(field.Type, field.IsPointer, addr);
                }

                throw new Exception("Execution Error: invalid struct pointer member access");
            }

            if (target is ArrayAccessNode arrayAccess)
            {
                int addr = GetIndexedAddress(arrayAccess);

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

        private int ApplyAssignmentOperator(string op, int currentValue, int rightValue)
        {
            return op switch
            {
                "=" => rightValue,
                "+=" => currentValue + rightValue,
                "-=" => currentValue - rightValue,
                "*=" => currentValue * rightValue,
                "/=" => currentValue / rightValue,
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
                if (arrayInit.Elements.Count > info.ArrayLength)
                    throw new Exception($"Execution Error: too many initializer elements for array at 0x{info.Address:X4}");

                for (int i = 0; i < arrayInit.Elements.Count; i++)
                {
                    int value = Convert.ToInt32(EvaluateExpression(arrayInit.Elements[i]));
                    int addr = info.Address + i * info.ElementSize;
                    WriteScalarAtAddress(info.Type, false, addr, value);
                }
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
                string output = Regex.Replace(fmt, @"%[dc]", m =>
                {
                    var v = args[idx++];
                    return m.Value == "%c" ? ((char)Convert.ToInt32(v)).ToString() : v.ToString();
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

            if (stmt is AssignmentNode assign)
            {
                int rightValue = Convert.ToInt32(EvaluateExpression(assign.Right));
                int currentValue = assign.Operator == "=" ? 0 : ReadTarget(assign.Left);
                int newValue = ApplyAssignmentOperator(assign.Operator, currentValue, rightValue);
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
                        if (forNode.Initializer is AssignmentNode or VarDeclNode or PostfixOpNode or UnaryOpNode or FunctionCallNode)
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

                        if (forNode.Increment != null)
                        {
                            if (forNode.Increment is AssignmentNode or PostfixOpNode or UnaryOpNode or FunctionCallNode)
                                ExecuteStatement(forNode.Increment);
                            else
                                EvaluateExpression(forNode.Increment);
                        }

                        if (_continueRequested)
                            _continueRequested = false;
                    }
                }
                finally
                {
                    ExitScope();
                }

                CaptureSnapshot("For completed");
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
                _returnValue = EvaluateExpression(ret.Value);
                _hasReturn = true;
                CaptureSnapshot("Return");
                return;
            }
        }

        private object EvaluateExpression(IASTNode expr)
        {
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
                int newValue = postfix.Operator == "++" ? oldValue + 1 : oldValue - 1;
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

                if (u.Operator == "++" || u.Operator == "--")
                {
                    int oldValue = ReadTarget(u.Target);
                    int newValue = u.Operator == "++" ? oldValue + 1 : oldValue - 1;
                    WriteTarget(u.Target, newValue);
                    return newValue;
                }
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