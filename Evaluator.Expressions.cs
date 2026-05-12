using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CInterpreterWpf
{
    public partial class Evaluator
    {

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
