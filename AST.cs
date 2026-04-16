using System.Collections.Generic;

namespace CInterpreterWpf
{
    public interface IASTNode { }

    public class ProgramNode : IASTNode
    {
        public List<IASTNode> Declarations { get; } = new List<IASTNode>();
    }

    public class StructFieldDecl
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public bool IsPointer { get; set; }
        public bool IsStruct { get; set; }
        public string StructName { get; set; }
    }

    public class StructDeclNode : IASTNode
    {
        public string Name { get; set; }
        public List<StructFieldDecl> Fields { get; } = new List<StructFieldDecl>();
    }

    public class FunctionParameter
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public bool IsPointer { get; set; }
        public bool IsStruct { get; set; }
        public string StructName { get; set; }
    }

    public class FunctionDeclNode : IASTNode
    {
        public string ReturnType { get; set; }
        public bool ReturnIsPointer { get; set; }
        public bool ReturnIsStruct { get; set; }
        public string ReturnStructName { get; set; }

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

    public class VarDeclNode : IASTNode
    {
        public string Type { get; set; }
        public string VarName { get; set; }
        public IASTNode Initializer { get; set; }
        public bool IsPointer { get; set; }
        public bool IsArray { get; set; }
        public int ArrayLength { get; set; }
        public bool IsArrayLengthInferred { get; set; }
        public bool IsStruct { get; set; }
        public string StructName { get; set; }
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
}