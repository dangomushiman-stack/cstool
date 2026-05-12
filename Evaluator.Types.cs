using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CInterpreterWpf
{
    public partial class Evaluator
    {

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

        private int GetInitializerArrayLength(VarDeclNode v)
        {
            if (v.Initializer is ArrayInitializerNode arrayInit)
                return arrayInit.Elements.Count;

            if (v.Initializer is StringNode strInit && v.Type == "char")
                return strInit.Value.Length + 1;

            return v.ArrayLength;
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

        private static bool IsPointerToArray(VarInfo info)
        {
            return info != null && !info.IsArray && info.PointerLevel > 0 && info.ArrayDimensions.Count > 0;
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

        private static int GetArrayStride(CTypeInfo typeInfo, int dimensionIndex, int scalarElementSize)
        {
            if (typeInfo.ArrayDimensions.Count == 0)
                return scalarElementSize;

            int stride = scalarElementSize;
            for (int i = dimensionIndex + 1; i < typeInfo.ArrayDimensions.Count; i++)
                stride *= typeInfo.ArrayDimensions[i];
            return stride;
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

    }
}
