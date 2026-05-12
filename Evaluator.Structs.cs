using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CInterpreterWpf
{
    public partial class Evaluator
    {
        public List<StructMemberInspectItem> InspectStructArrayElement(ExecutionSnapshot snapshot, string variableName, int index)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            if (string.IsNullOrWhiteSpace(variableName) ||
                !snapshot.Env.TryGetValue(variableName, out var info))
                throw new Exception("Struct inspector: select a variable from the memory view");

            if (!info.IsArray || !info.IsStruct || info.IsPointer)
                throw new Exception($"Struct inspector: '{variableName}' is not a struct array");

            if (index < 0 || index >= info.ArrayLength)
                throw new Exception($"Struct inspector: index must be 0..{info.ArrayLength - 1}");

            int baseAddress = info.Address + index * info.ElementSize;
            return InspectStructMembers(snapshot.Memory, info.StructName, baseAddress, "");
        }

        private List<StructMemberInspectItem> InspectStructMembers(byte[] memory, string structName, int baseAddress, string prefix)
        {
            if (!_structs.TryGetValue(structName, out var sd))
                throw new Exception($"Struct inspector: struct '{structName}' not found");

            var items = new List<StructMemberInspectItem>();
            int offset = 0;

            foreach (var field in sd.Fields)
            {
                int fieldAddress = baseAddress + offset;
                string memberName = string.IsNullOrEmpty(prefix) ? field.Name : $"{prefix}.{field.Name}";

                if (field.IsArray)
                {
                    int elementSize = GetStructFieldElementSize(field);
                    int length = GetArrayTotalLength(field.TypeInfo);

                    for (int i = 0; i < length; i++)
                    {
                        int elementAddress = fieldAddress + i * elementSize;
                        string indexedName = $"{memberName}[{i}]";

                        if (field.IsStruct && !field.IsPointer)
                        {
                            items.AddRange(InspectStructMembers(memory, field.StructName, elementAddress, indexedName));
                        }
                        else
                        {
                            items.Add(new StructMemberInspectItem
                            {
                                MemberName = indexedName,
                                Type = GetStructFieldDisplayType(field),
                                Address = $"0x{elementAddress:X4}",
                                Value = FormatStructFieldValue(memory, elementAddress, field.Type, field.IsPointer)
                            });
                        }
                    }
                }
                else if (field.IsStruct && !field.IsPointer)
                {
                    items.AddRange(InspectStructMembers(memory, field.StructName, fieldAddress, memberName));
                }
                else
                {
                    items.Add(new StructMemberInspectItem
                    {
                        MemberName = memberName,
                        Type = GetStructFieldDisplayType(field),
                        Address = $"0x{fieldAddress:X4}",
                        Value = FormatStructFieldValue(memory, fieldAddress, field.Type, field.IsPointer)
                    });
                }

                offset += GetStructFieldSize(field);
            }

            return items;
        }

        private static string GetStructFieldDisplayType(StructFieldDecl field)
        {
            string baseType = field.IsStruct && !string.IsNullOrEmpty(field.StructName)
                ? $"struct {field.StructName}"
                : field.Type;

            if (field.IsPointer)
                baseType += new string('*', field.PointerLevel);

            return baseType;
        }

        private static string FormatStructFieldValue(byte[] memory, int address, string type, bool isPointer)
        {
            if (address < 0 || address >= memory.Length)
                return "<out of range>";

            int value;
            if (!isPointer && type == "char")
            {
                value = memory[address];
                return $"{value} ('{(char)value}')";
            }

            if (!isPointer && type == "short")
            {
                if (address + 2 > memory.Length)
                    return "<out of range>";
                return BitConverter.ToInt16(memory, address).ToString();
            }

            if (address + 4 > memory.Length)
                return "<out of range>";

            value = BitConverter.ToInt32(memory, address);
            return isPointer ? $"{value} (0x{value:X4})" : value.ToString();
        }

        private int GetStructMemberAddress(StructMemberAccessNode access)
        {
            if (!TryGetStructValueInfo(access.Target, out string baseStructName, out int baseAddr))
                throw new Exception("Execution Error: left side of '.' is not a struct");

            var fieldInfo = GetStructFieldInfo(baseStructName, access.MemberName);
            return baseAddr + fieldInfo.offset;
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

        private int GetStructPointerMemberAddress(StructPointerMemberAccessNode access)
        {
            if (!TryGetStructPointerType(access.Target, out string structName))
                throw new Exception("Execution Error: left side of '->' is not a pointer to struct");

            int baseAddr = Convert.ToInt32(EvaluateExpression(access.Target));
            var fieldInfo = GetStructFieldInfo(structName, access.MemberName);
            return baseAddr + fieldInfo.offset;
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

        private int GetStructFieldSize(StructFieldDecl field)
        {
            int elementSize = GetStructFieldElementSize(field);
            return field.IsArray ? elementSize * GetArrayTotalLength(field.TypeInfo) : elementSize;
        }

        private int GetStructFieldElementSize(StructFieldDecl field)
        {
            if (field.IsPointer) return 4;
            if (field.IsStruct) return GetStructSize(field.StructName);
            return field.Type == "char" ? 1 : 4;
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
    }
}
