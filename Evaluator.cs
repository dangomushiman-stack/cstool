using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CInterpreterWpf
{
    public enum MemoryRegionKind
    {
        Local,
        Global,
        Literal
    }

    public class VarInfo
    {
        public int Address { get; set; }
        public string Type { get; set; }
        public bool IsPointer { get; set; }
        public bool IsArray { get; set; }
        public int ArrayLength { get; set; }
        public bool IsStruct { get; set; }
        public string StructName { get; set; }
        public int StructSize { get; set; }

        public int ElementSize => IsPointer ? 4 : (Type == "char" ? 1 : 4);
        public int Size =>
            IsArray ? ElementSize * ArrayLength :
            IsPointer ? 4 :
            IsStruct ? StructSize :
            ElementSize;

        public VarInfo Clone()
        {
            return new VarInfo
            {
                Address = Address,
                Type = Type,
                IsPointer = IsPointer,
                IsArray = IsArray,
                ArrayLength = ArrayLength,
                IsStruct = IsStruct,
                StructName = StructName,
                StructSize = StructSize
            };
        }
    }

    public class MemoryRegionInfo
    {
        public int Address { get; set; }
        public int Size { get; set; }
        public string Label { get; set; }
        public bool IsStringLiteral { get; set; }
        public MemoryRegionKind Kind { get; set; }

        public MemoryRegionInfo Clone()
        {
            return new MemoryRegionInfo
            {
                Address = Address,
                Size = Size,
                Label = Label,
                IsStringLiteral = IsStringLiteral,
                Kind = Kind
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

        public void Evaluate(ProgramNode program)
        {
            _functions.Clear();
            _structs.Clear();

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

            if (!_functions.ContainsKey("main"))
                throw new Exception("Execution Error: 'main' not found");

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

            EnterScope();
            CaptureSnapshot("Program start");

            try
            {
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

        private int GetStructMemberAddress(StructMemberAccessNode access)
        {
            if (access.Target is VariableNode v)
            {
                if (!Env.TryGetValue(v.Name, out var info))
                    throw new Exception($"Execution Error: variable '{v.Name}' not found");

                if (!info.IsStruct || info.IsPointer)
                    throw new Exception($"Execution Error: '{v.Name}' is not a struct value");

                var fieldInfo = GetStructFieldInfo(info.StructName, access.MemberName);
                return info.Address + fieldInfo.offset;
            }

            throw new Exception("Execution Error: nested struct member access is not supported yet");
        }

        private int GetStructPointerMemberAddress(StructPointerMemberAccessNode access)
        {
            string structName = null;
            int baseAddr;

            if (access.Target is VariableNode v)
            {
                if (!Env.TryGetValue(v.Name, out var info))
                    throw new Exception($"Execution Error: variable '{v.Name}' not found");

                if (!info.IsStruct || !info.IsPointer)
                    throw new Exception($"Execution Error: '{v.Name}' is not a pointer to struct");

                structName = info.StructName;
                baseAddr = ReadScalarAtAddress(info.Type, true, info.Address);
            }
            else
            {
                throw new Exception("Execution Error: complex pointer member access is not supported yet");
            }

            var fieldInfo = GetStructFieldInfo(structName, access.MemberName);
            return baseAddr + fieldInfo.offset;
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
                    var info = new VarInfo
                    {
                        Address = _stackPtr,
                        Type = param.Type,
                        IsPointer = param.IsPointer,
                        IsArray = false,
                        ArrayLength = 0,
                        IsStruct = param.IsStruct,
                        StructName = param.StructName,
                        StructSize = param.IsStruct && !param.IsPointer ? GetStructSize(param.StructName) : 0
                    };

                    int addr = AllocateStackRegion(info.Size, param.Name, MemoryRegionKind.Local);
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

        private int AllocateStackRegion(int size, string label, MemoryRegionKind kind = MemoryRegionKind.Local)
        {
            EnsureSpaceForStackAllocation(size);

            int addr = _stackPtr;

            Regions.Add(new MemoryRegionInfo
            {
                Address = addr,
                Size = size,
                Label = label,
                IsStringLiteral = false,
                Kind = kind
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
                IsStringLiteral = true,
                Kind = MemoryRegionKind.Literal
            });

            return _literalPtr;
        }

        private int EnsureStringLiteral(string value)
        {
            if (_stringLiteralPool.TryGetValue(value, out int existingAddr))
                return existingAddr;

            int size = value.Length + 1;
            int addr = AllocateLiteralRegion(size, $"string literal \"{value}\"");

            for (int i = 0; i < value.Length; i++)
                WriteByte(addr + i, value[i]);

            WriteByte(addr + value.Length, 0);
            _stringLiteralPool[value] = addr;

            CaptureSnapshot($"String literal allocated: \"{value}\"");
            return addr;
        }

        private string ReadCString(int addr)
        {
            EnsureMemoryRange(addr, 1);

            var sb = new StringBuilder();
            int current = addr;

            while (true)
            {
                EnsureMemoryRange(current, 1);
                byte b = Memory[current];
                if (b == 0) break;
                sb.Append((char)b);
                current++;
            }

            return sb.ToString();
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
            return isPointer ? 4 : (type == "char" ? 1 : 4);
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
            if (expr is StructMemberAccessNode member)
            {
                if (member.Target is VariableNode mv &&
                    Env.TryGetValue(mv.Name, out var baseInfo) &&
                    baseInfo.IsStruct && !baseInfo.IsPointer)
                {
                    var field = GetStructFieldInfo(baseInfo.StructName, member.MemberName).field;
                    type = field.Type;
                    isPointer = field.IsPointer;
                    return true;
                }
            }

            if (expr is StructPointerMemberAccessNode pointerMember)
            {
                if (pointerMember.Target is VariableNode pv &&
                    Env.TryGetValue(pv.Name, out var baseInfo) &&
                    baseInfo.IsStruct && baseInfo.IsPointer)
                {
                    var field = GetStructFieldInfo(baseInfo.StructName, pointerMember.MemberName).field;
                    type = field.Type;
                    isPointer = field.IsPointer;
                    return true;
                }
            }

            if (expr is VariableNode v && Env.TryGetValue(v.Name, out var varInfo))
            {
                if (varInfo.IsArray)
                {
                    type = varInfo.Type;
                    isPointer = false;
                    return true;
                }

                if (varInfo.IsPointer)
                {
                    type = varInfo.Type;
                    isPointer = false;
                    return true;
                }
            }

            if (expr is StringNode)
            {
                type = "char";
                isPointer = false;
                return true;
            }

            if (expr is BinaryOpNode b && (b.Operator == "+" || b.Operator == "-"))
            {
                if (TryGetPointeeType(b.Left, out type, out isPointer))
                    return true;

                if (TryGetPointeeType(b.Right, out type, out isPointer))
                    return true;
            }

            if (expr is ArrayAccessNode access)
            {
                if (TryGetPointeeType(access.Target, out type, out isPointer))
                    return true;
            }

            if (expr is UnaryOpNode u && u.Operator == "&")
            {
                if (u.Target is VariableNode vt && Env.TryGetValue(vt.Name, out var addrInfo))
                {
                    type = addrInfo.Type;
                    isPointer = addrInfo.IsPointer;
                    return true;
                }

                if (u.Target is StructMemberAccessNode memberTarget &&
                    memberTarget.Target is VariableNode smv &&
                    Env.TryGetValue(smv.Name, out var structVar) &&
                    structVar.IsStruct)
                {
                    var field = GetStructFieldInfo(structVar.StructName, memberTarget.MemberName).field;
                    type = field.Type;
                    isPointer = field.IsPointer;
                    return true;
                }

                if (u.Target is StructPointerMemberAccessNode ptrMemberTarget &&
                    ptrMemberTarget.Target is VariableNode pmv &&
                    Env.TryGetValue(pmv.Name, out var ptrStructVar) &&
                    ptrStructVar.IsStruct && ptrStructVar.IsPointer)
                {
                    var field = GetStructFieldInfo(ptrStructVar.StructName, ptrMemberTarget.MemberName).field;
                    type = field.Type;
                    isPointer = field.IsPointer;
                    return true;
                }

                if (u.Target is ArrayAccessNode aa && TryGetPointeeType(aa.Target, out type, out isPointer))
                    return true;
            }

            type = null;
            isPointer = false;
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

            int elementSize = TryGetPointeeType(access.Target, out string t, out bool p)
                ? GetTypeElementSize(t, p)
                : 4;

            return baseAddr + index * elementSize;
        }

        private int ReadTarget(IASTNode target)
        {
            if (target is VariableNode varNode)
            {
                var info = Env[varNode.Name];

                if (info.IsStruct && !info.IsPointer)
                    throw new Exception($"Execution Error: struct variable '{varNode.Name}' cannot be used as scalar value");

                if (info.IsArray)
                    return info.Address;

                return ReadScalarAtAddress(info.Type, info.IsPointer, info.Address);
            }

            if (target is StructMemberAccessNode memberAccess)
            {
                int addr = GetStructMemberAddress(memberAccess);

                if (memberAccess.Target is VariableNode mv &&
                    Env.TryGetValue(mv.Name, out var structInfo) &&
                    structInfo.IsStruct)
                {
                    var field = GetStructFieldInfo(structInfo.StructName, memberAccess.MemberName).field;
                    return ReadScalarAtAddress(field.Type, field.IsPointer, addr);
                }

                throw new Exception("Execution Error: invalid struct member access");
            }

            if (target is StructPointerMemberAccessNode pointerMemberAccess)
            {
                int addr = GetStructPointerMemberAddress(pointerMemberAccess);

                if (pointerMemberAccess.Target is VariableNode pv &&
                    Env.TryGetValue(pv.Name, out var structPtrInfo) &&
                    structPtrInfo.IsStruct && structPtrInfo.IsPointer)
                {
                    var field = GetStructFieldInfo(structPtrInfo.StructName, pointerMemberAccess.MemberName).field;
                    return ReadScalarAtAddress(field.Type, field.IsPointer, addr);
                }

                throw new Exception("Execution Error: invalid struct pointer member access");
            }

            if (target is ArrayAccessNode arrayAccess)
            {
                int addr = GetIndexedAddress(arrayAccess);

                if (TryGetPointeeType(arrayAccess.Target, out string elementType, out bool elementIsPointer))
                    return ReadScalarAtAddress(elementType, elementIsPointer, addr);

                return ReadInt(addr);
            }

            if (target is UnaryOpNode unary && unary.Operator == "*")
            {
                int addr = Convert.ToInt32(EvaluateExpression(unary.Target));

                if (TryGetPointeeType(unary.Target, out string pointeeType, out bool pointeeIsPointer))
                    return ReadScalarAtAddress(pointeeType, pointeeIsPointer, addr);

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

                if (memberAccess.Target is VariableNode mv &&
                    Env.TryGetValue(mv.Name, out var structInfo) &&
                    structInfo.IsStruct)
                {
                    var field = GetStructFieldInfo(structInfo.StructName, memberAccess.MemberName).field;
                    WriteScalarAtAddress(field.Type, field.IsPointer, addr, value);
                    return;
                }

                throw new Exception("Execution Error: invalid struct member access");
            }

            if (target is StructPointerMemberAccessNode pointerMemberAccess)
            {
                int addr = GetStructPointerMemberAddress(pointerMemberAccess);

                if (pointerMemberAccess.Target is VariableNode pv &&
                    Env.TryGetValue(pv.Name, out var structPtrInfo) &&
                    structPtrInfo.IsStruct && structPtrInfo.IsPointer)
                {
                    var field = GetStructFieldInfo(structPtrInfo.StructName, pointerMemberAccess.MemberName).field;
                    WriteScalarAtAddress(field.Type, field.IsPointer, addr, value);
                    return;
                }

                throw new Exception("Execution Error: invalid struct pointer member access");
            }

            if (target is ArrayAccessNode arrayAccess)
            {
                int addr = GetIndexedAddress(arrayAccess);

                if (TryGetPointeeType(arrayAccess.Target, out string elementType, out bool elementIsPointer))
                {
                    WriteScalarAtAddress(elementType, elementIsPointer, addr, value);
                    return;
                }

                WriteInt(addr, value);
                return;
            }

            if (target is UnaryOpNode unary && unary.Operator == "*")
            {
                int addr = Convert.ToInt32(EvaluateExpression(unary.Target));

                if (TryGetPointeeType(unary.Target, out string pointeeType, out bool pointeeIsPointer))
                {
                    WriteScalarAtAddress(pointeeType, pointeeIsPointer, addr, value);
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

                var info = new VarInfo
                {
                    Address = _stackPtr,
                    Type = v.Type,
                    IsPointer = v.IsPointer,
                    IsArray = v.IsArray,
                    ArrayLength = resolvedArrayLength,
                    IsStruct = v.IsStruct,
                    StructName = v.StructName,
                    StructSize = v.IsStruct && !v.IsPointer ? GetStructSize(v.StructName) : 0
                };

                if (info.IsArray && info.ArrayLength <= 0)
                    throw new Exception($"Execution Error: invalid array length for '{v.VarName}'");

                int addr = AllocateStackRegion(info.Size, v.VarName, MemoryRegionKind.Local);
                info.Address = addr;
                BindVariable(v.VarName, info);

                if (v.IsStruct && !v.IsPointer)
                {
                    if (v.Initializer != null)
                        throw new Exception("Struct initializer is not supported yet");
                }
                else if (v.IsArray)
                {
                    if (v.Initializer != null)
                        throw new Exception("Struct array initializer is not supported yet");
                }
                else
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

                    if (u.Target is StructMemberAccessNode memberTarget &&
                        memberTarget.Target is VariableNode memberVar &&
                        Env.TryGetValue(memberVar.Name, out var svi) &&
                        svi.IsStruct)
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

                if ((b.Operator == "+" || b.Operator == "-") &&
                    TryGetPointeeType(b.Left, out string leftType, out bool leftIsPointer))
                {
                    right *= GetTypeElementSize(leftType, leftIsPointer);
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