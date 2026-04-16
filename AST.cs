using System;
using System.Collections.Generic;

namespace CInterpreterWpf
{
    public interface IASTNode { }

    public class CTypeInfo
    {
        private int _pointerLevel;

        public string Type { get; set; }

        public int PointerLevel
        {
            get => _pointerLevel;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(PointerLevel));

                _pointerLevel = value;
            }
        }

        public bool IsPointer
        {
            get => PointerLevel > 0;
            set
            {
                if (!value)
                {
                    PointerLevel = 0;
                }
                else if (PointerLevel == 0)
                {
                    PointerLevel = 1;
                }
            }
        }

        public bool IsStruct { get; set; }
        public string StructName { get; set; }
        public bool IsArray { get; set; }
        public int ArrayLength { get; set; }
        public bool IsArrayLengthInferred { get; set; }

        public CTypeInfo Clone()
        {
            return new CTypeInfo
            {
                Type = Type,
                PointerLevel = PointerLevel,
                IsStruct = IsStruct,
                StructName = StructName,
                IsArray = IsArray,
                ArrayLength = ArrayLength,
                IsArrayLengthInferred = IsArrayLengthInferred
            };
        }

        public void CopyFrom(CTypeInfo other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            Type = other.Type;
            PointerLevel = other.PointerLevel;
            IsStruct = other.IsStruct;
            StructName = other.StructName;
            IsArray = other.IsArray;
            ArrayLength = other.ArrayLength;
            IsArrayLengthInferred = other.IsArrayLengthInferred;
        }

        public string ToDisplayString()
        {
            string baseType = IsStruct && !string.IsNullOrEmpty(StructName) ? $"struct {StructName}" : Type;
            if (string.IsNullOrEmpty(baseType))
                baseType = "<unknown>";

            string pointerSuffix = new string('*', PointerLevel);
            string arraySuffix = IsArray ? $"[{ArrayLength}]" : "";
            return baseType + pointerSuffix + arraySuffix;
        }
    }

    public class ProgramNode : IASTNode
    {
        public List<IASTNode> Declarations { get; } = new List<IASTNode>();
    }

    public class StructFieldDecl
    {
        public CTypeInfo TypeInfo { get; } = new CTypeInfo();
        public string Type { get => TypeInfo.Type; set => TypeInfo.Type = value; }
        public string Name { get; set; }
        public bool IsPointer { get => TypeInfo.IsPointer; set => TypeInfo.IsPointer = value; }
        public int PointerLevel { get => TypeInfo.PointerLevel; set => TypeInfo.PointerLevel = value; }
        public bool IsStruct { get => TypeInfo.IsStruct; set => TypeInfo.IsStruct = value; }
        public string StructName { get => TypeInfo.StructName; set => TypeInfo.StructName = value; }
    }

    public class StructDeclNode : IASTNode
    {
        public string Name { get; set; }
        public List<StructFieldDecl> Fields { get; } = new List<StructFieldDecl>();
    }

    public class FunctionParameter
    {
        public CTypeInfo TypeInfo { get; } = new CTypeInfo();
        public string Type { get => TypeInfo.Type; set => TypeInfo.Type = value; }
        public string Name { get; set; }
        public bool IsPointer { get => TypeInfo.IsPointer; set => TypeInfo.IsPointer = value; }
        public int PointerLevel { get => TypeInfo.PointerLevel; set => TypeInfo.PointerLevel = value; }
        public bool IsStruct { get => TypeInfo.IsStruct; set => TypeInfo.IsStruct = value; }
        public string StructName { get => TypeInfo.StructName; set => TypeInfo.StructName = value; }
    }

    public class FunctionDeclNode : IASTNode
    {
        public CTypeInfo ReturnTypeInfo { get; } = new CTypeInfo();
        public string ReturnType { get => ReturnTypeInfo.Type; set => ReturnTypeInfo.Type = value; }
        public bool ReturnIsPointer { get => ReturnTypeInfo.IsPointer; set => ReturnTypeInfo.IsPointer = value; }
        public int ReturnPointerLevel { get => ReturnTypeInfo.PointerLevel; set => ReturnTypeInfo.PointerLevel = value; }
        public bool ReturnIsStruct { get => ReturnTypeInfo.IsStruct; set => ReturnTypeInfo.IsStruct = value; }
        public string ReturnStructName { get => ReturnTypeInfo.StructName; set => ReturnTypeInfo.StructName = value; }

        public string Name { get; set; }
        public List<FunctionParameter> Parameters { get; } = new List<FunctionParameter>();
        public List<IASTNode> Body { get; } = new List<IASTNode>();
    }

    public class BlockNode : IASTNode
    {
        public List<IASTNode> Statements { get; } = new List<IASTNode>();
    }

    public class ArrayInitializerNode : IASTNode
    {
        public List<IASTNode> Elements { get; } = new List<IASTNode>();
    }

    public class StructInitializerNode : IASTNode
    {
        public List<IASTNode> Elements { get; } = new List<IASTNode>();
    }

    public class VarDeclNode : IASTNode
    {
        public CTypeInfo TypeInfo { get; } = new CTypeInfo();
        public string Type { get => TypeInfo.Type; set => TypeInfo.Type = value; }
        public string VarName { get; set; }
        public IASTNode Initializer { get; set; }
        public bool IsPointer { get => TypeInfo.IsPointer; set => TypeInfo.IsPointer = value; }
        public int PointerLevel { get => TypeInfo.PointerLevel; set => TypeInfo.PointerLevel = value; }
        public bool IsArray { get => TypeInfo.IsArray; set => TypeInfo.IsArray = value; }
        public int ArrayLength { get => TypeInfo.ArrayLength; set => TypeInfo.ArrayLength = value; }
        public bool IsArrayLengthInferred { get => TypeInfo.IsArrayLengthInferred; set => TypeInfo.IsArrayLengthInferred = value; }
        public bool IsStruct { get => TypeInfo.IsStruct; set => TypeInfo.IsStruct = value; }
        public string StructName { get => TypeInfo.StructName; set => TypeInfo.StructName = value; }
    }

    public class FunctionCallNode : IASTNode
    {
        public string FunctionName { get; set; }
        public List<IASTNode> Arguments { get; } = new List<IASTNode>();
    }

    public class ReturnNode : IASTNode
    {
        public IASTNode Value { get; set; }
    }

    public class BreakNode : IASTNode { }

    public class ContinueNode : IASTNode { }

    public class NumberNode : IASTNode
    {
        public int Value { get; set; }
    }

    public class StringNode : IASTNode
    {
        public string Value { get; set; }
    }

    public class CharLiteralNode : IASTNode
    {
        public char Value { get; set; }
    }

    public class VariableNode : IASTNode
    {
        public string Name { get; set; }
    }

    public class ArrayAccessNode : IASTNode
    {
        public IASTNode Target { get; set; }
        public IASTNode Index { get; set; }
    }

    public class StructMemberAccessNode : IASTNode
    {
        public IASTNode Target { get; set; }
        public string MemberName { get; set; }
    }

    public class StructPointerMemberAccessNode : IASTNode
    {
        public IASTNode Target { get; set; }
        public string MemberName { get; set; }
    }

    public class BinaryOpNode : IASTNode
    {
        public IASTNode Left { get; set; }
        public string Operator { get; set; }
        public IASTNode Right { get; set; }
    }

    public class UnaryOpNode : IASTNode
    {
        public string Operator { get; set; }
        public IASTNode Target { get; set; }
    }

    public class PostfixOpNode : IASTNode
    {
        public IASTNode Target { get; set; }
        public string Operator { get; set; }
    }

    public class AssignmentNode : IASTNode
    {
        public IASTNode Left { get; set; }
        public string Operator { get; set; }
        public IASTNode Right { get; set; }
    }

    public class IfNode : IASTNode
    {
        public IASTNode Condition { get; set; }
        public IASTNode ThenBranch { get; set; }
        public IASTNode ElseBranch { get; set; }
    }

    public class WhileNode : IASTNode
    {
        public IASTNode Condition { get; set; }
        public IASTNode Body { get; set; }
    }

    public class DoWhileNode : IASTNode
    {
        public IASTNode Body { get; set; }
        public IASTNode Condition { get; set; }
    }

    public class ForNode : IASTNode
    {
        public IASTNode Initializer { get; set; }
        public IASTNode Condition { get; set; }
        public IASTNode Increment { get; set; }
        public IASTNode Body { get; set; }
    }
    public class TypedefNode : IASTNode
    {
        public string AliasName { get; set; }
    }
    public class FloatNode : IASTNode
    {
        public double Value { get; set; }
    }
}