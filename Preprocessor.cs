using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CInterpreterWpf
{
    public static class Preprocessor
    {
        private const int MaxExpansionDepth = 20;

        private class Macro
        {
            public string Name { get; set; }
            public List<string> Parameters { get; set; }
            public string Replacement { get; set; }
            public bool IsFunctionLike => Parameters != null;
        }

        private class ConditionalState
        {
            public bool ParentActive { get; set; }
            public bool ConditionActive { get; set; }
            public bool ElseSeen { get; set; }
            public bool IsActive => ParentActive && ConditionActive;
        }

        public static string Process(string source)
        {
            if (string.IsNullOrEmpty(source))
                return source ?? "";

            var macros = new Dictionary<string, Macro>();
            var conditionals = new Stack<ConditionalState>();
            var output = new StringBuilder();
            string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                bool isDirective = trimmed.StartsWith("#", StringComparison.Ordinal);
                bool active = IsActive(conditionals);

                if (isDirective)
                {
                    HandleDirective(trimmed.Substring(1).TrimStart(), macros, conditionals, active);
                    output.AppendLine();
                    continue;
                }

                output.AppendLine(active ? ExpandLine(line, macros) : "");
            }

            if (conditionals.Count > 0)
                throw new Exception("Preprocessor Error: unterminated #if/#ifdef/#ifndef");

            return output.ToString();
        }

        private static bool IsActive(Stack<ConditionalState> conditionals)
        {
            return conditionals.Count == 0 || conditionals.Peek().IsActive;
        }

        private static void HandleDirective(
            string directive,
            Dictionary<string, Macro> macros,
            Stack<ConditionalState> conditionals,
            bool active)
        {
            string keyword = ReadIdentifier(directive, 0, out int pos);
            string rest = pos < directive.Length ? directive.Substring(pos).TrimStart() : "";

            switch (keyword)
            {
                case "define":
                    if (active)
                        DefineMacro(rest, macros);
                    return;

                case "undef":
                    if (active)
                    {
                        string name = ReadIdentifier(rest, 0, out _);
                        if (name.Length > 0)
                            macros.Remove(name);
                    }
                    return;

                case "ifdef":
                    PushConditional(conditionals, active && macros.ContainsKey(ReadIdentifier(rest, 0, out _)));
                    return;

                case "ifndef":
                    PushConditional(conditionals, active && !macros.ContainsKey(ReadIdentifier(rest, 0, out _)));
                    return;

                case "if":
                    PushConditional(conditionals, active && EvaluatePreprocessorExpression(rest, macros) != 0);
                    return;

                case "else":
                    FlipConditional(conditionals);
                    return;

                case "endif":
                    if (conditionals.Count == 0)
                        throw new Exception("Preprocessor Error: #endif without #if");
                    conditionals.Pop();
                    return;

                default:
                    throw new Exception($"Preprocessor Error: unsupported directive '#{keyword}'");
            }
        }

        private static void PushConditional(Stack<ConditionalState> conditionals, bool condition)
        {
            bool parentActive = IsActive(conditionals);
            conditionals.Push(new ConditionalState
            {
                ParentActive = parentActive,
                ConditionActive = condition,
                ElseSeen = false
            });
        }

        private static void FlipConditional(Stack<ConditionalState> conditionals)
        {
            if (conditionals.Count == 0)
                throw new Exception("Preprocessor Error: #else without #if");

            var state = conditionals.Peek();
            if (state.ElseSeen)
                throw new Exception("Preprocessor Error: duplicate #else");

            state.ConditionActive = !state.ConditionActive;
            state.ElseSeen = true;
        }

        private static void DefineMacro(string text, Dictionary<string, Macro> macros)
        {
            string name = ReadIdentifier(text, 0, out int pos);
            if (name.Length == 0)
                throw new Exception("Preprocessor Error: invalid #define");

            var macro = new Macro { Name = name };

            if (pos < text.Length && text[pos] == '(')
            {
                int close = FindMatchingParen(text, pos);
                if (close < 0)
                    throw new Exception($"Preprocessor Error: unterminated parameter list for macro '{name}'");

                string paramText = text.Substring(pos + 1, close - pos - 1).Trim();
                macro.Parameters = paramText.Length == 0
                    ? new List<string>()
                    : paramText.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                macro.Replacement = close + 1 < text.Length ? text.Substring(close + 1).TrimStart() : "";
            }
            else
            {
                macro.Parameters = null;
                macro.Replacement = pos < text.Length ? text.Substring(pos).TrimStart() : "";
            }

            macros[name] = macro;
        }

        private static int EvaluatePreprocessorExpression(string expression, Dictionary<string, Macro> macros)
        {
            string expanded = ExpandLine(expression, macros).Trim();
            if (expanded.Length == 0)
                return 0;

            if (expanded.StartsWith("defined", StringComparison.Ordinal))
            {
                string rest = expanded.Substring("defined".Length).Trim();
                if (rest.StartsWith("(") && rest.EndsWith(")"))
                    rest = rest.Substring(1, rest.Length - 2).Trim();
                return macros.ContainsKey(rest) ? 1 : 0;
            }

            return int.TryParse(expanded, out int value) ? value : 0;
        }

        private static string ExpandLine(string line, Dictionary<string, Macro> macros)
        {
            string current = line;
            for (int i = 0; i < MaxExpansionDepth; i++)
            {
                string next = ExpandLineOnce(current, macros);
                if (next == current)
                    return next;
                current = next;
            }

            return current;
        }

        private static string ExpandLineOnce(string line, Dictionary<string, Macro> macros)
        {
            var result = new StringBuilder();

            for (int i = 0; i < line.Length;)
            {
                if (line[i] == '"' || line[i] == '\'')
                {
                    AppendQuoted(line, result, ref i);
                    continue;
                }

                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    result.Append(line.Substring(i));
                    break;
                }

                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        result.Append(line.Substring(i));
                        break;
                    }

                    result.Append(line.Substring(i, end - i + 2));
                    i = end + 2;
                    continue;
                }

                if (IsIdentifierStart(line[i]))
                {
                    string name = ReadIdentifier(line, i, out int next);
                    if (macros.TryGetValue(name, out var macro))
                    {
                        if (macro.IsFunctionLike)
                        {
                            int callStart = SkipSpaces(line, next);
                            if (callStart < line.Length && line[callStart] == '(')
                            {
                                int close = FindMatchingParen(line, callStart);
                                if (close < 0)
                                    throw new Exception($"Preprocessor Error: unterminated macro call '{name}'");

                                var args = SplitArguments(line.Substring(callStart + 1, close - callStart - 1));
                                result.Append(ExpandFunctionMacro(macro, args, macros));
                                i = close + 1;
                                continue;
                            }
                        }
                        else
                        {
                            result.Append(macro.Replacement);
                            i = next;
                            continue;
                        }
                    }

                    result.Append(name);
                    i = next;
                    continue;
                }

                result.Append(line[i]);
                i++;
            }

            return result.ToString();
        }

        private static string ExpandFunctionMacro(Macro macro, List<string> args, Dictionary<string, Macro> macros)
        {
            if (args.Count != macro.Parameters.Count)
                throw new Exception($"Preprocessor Error: macro '{macro.Name}' expects {macro.Parameters.Count} arguments, but got {args.Count}");

            string replacement = macro.Replacement;
            for (int i = 0; i < macro.Parameters.Count; i++)
                replacement = ReplaceIdentifier(replacement, macro.Parameters[i], args[i]);

            return ExpandLine(replacement, macros);
        }

        private static string ReplaceIdentifier(string text, string identifier, string replacement)
        {
            var result = new StringBuilder();
            for (int i = 0; i < text.Length;)
            {
                if (text[i] == '"' || text[i] == '\'')
                {
                    AppendQuoted(text, result, ref i);
                    continue;
                }

                if (IsIdentifierStart(text[i]))
                {
                    string name = ReadIdentifier(text, i, out int next);
                    result.Append(name == identifier ? replacement : name);
                    i = next;
                    continue;
                }

                result.Append(text[i]);
                i++;
            }

            return result.ToString();
        }

        private static List<string> SplitArguments(string text)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            int depth = 0;

            for (int i = 0; i < text.Length;)
            {
                if (text[i] == '"' || text[i] == '\'')
                {
                    AppendQuoted(text, current, ref i);
                    continue;
                }

                if (text[i] == '(')
                {
                    depth++;
                    current.Append(text[i++]);
                    continue;
                }

                if (text[i] == ')')
                {
                    depth--;
                    current.Append(text[i++]);
                    continue;
                }

                if (text[i] == ',' && depth == 0)
                {
                    args.Add(current.ToString().Trim());
                    current.Clear();
                    i++;
                    continue;
                }

                current.Append(text[i++]);
            }

            if (text.Trim().Length > 0)
                args.Add(current.ToString().Trim());

            return args;
        }

        private static string ReadIdentifier(string text, int start, out int next)
        {
            next = start;
            if (start >= text.Length || !IsIdentifierStart(text[start]))
                return "";

            next++;
            while (next < text.Length && IsIdentifierPart(text[next]))
                next++;

            return text.Substring(start, next - start);
        }

        private static int SkipSpaces(string text, int start)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
            return start;
        }

        private static int FindMatchingParen(string text, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < text.Length;)
            {
                if (text[i] == '"' || text[i] == '\'')
                {
                    var ignored = new StringBuilder();
                    AppendQuoted(text, ignored, ref i);
                    continue;
                }

                if (text[i] == '(')
                    depth++;
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }

                i++;
            }

            return -1;
        }

        private static void AppendQuoted(string text, StringBuilder result, ref int index)
        {
            char quote = text[index];
            result.Append(text[index++]);

            while (index < text.Length)
            {
                char c = text[index];
                result.Append(c);
                index++;

                if (c == '\\' && index < text.Length)
                {
                    result.Append(text[index]);
                    index++;
                    continue;
                }

                if (c == quote)
                    break;
            }
        }

        private static bool IsIdentifierStart(char c)
        {
            return char.IsLetter(c) || c == '_';
        }

        private static bool IsIdentifierPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
