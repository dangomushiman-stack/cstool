using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace CInterpreterWpf
{
    public partial class MainWindow : Window
    {
        private Evaluator _lastEvaluator;
        private ExecutionSnapshot _currentSnapshot;
        private List<int> _breakpoints = new List<int>();
        private bool _syncingSelection;
        private bool _syncingBreakpoints;

        public MainWindow()
        {
            InitializeComponent();

            CodeEditor.Text = @"int main() {
    int x = 1;
    {
        int x = 2;
        printf(""inner: %d\n"", x);
    }
    printf(""outer: %d\n"", x);
    return 0;
}";
            UpdateCaretInfo();
        }

        private void CodeEditor_SelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateCaretInfo();
        }

        private void CodeEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCaretInfo();
        }

        private void UpdateCaretInfo()
        {
            if (CaretInfoText == null || CodeEditor == null)
                return;

            int caretIndex = CodeEditor.CaretIndex;
            int lineIndex = CodeEditor.GetLineIndexFromCharacterIndex(caretIndex);
            if (lineIndex < 0)
                lineIndex = 0;

            int lineStartIndex = CodeEditor.GetCharacterIndexFromLineIndex(lineIndex);
            int column = Math.Max(0, caretIndex - lineStartIndex) + 1;
            int lineCount = Math.Max(1, CodeEditor.LineCount);

            CaretInfoText.Text = $"Line: {lineIndex + 1}, Col: {column} / Lines: {lineCount}";
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            ConsoleOutput.Clear();
            string sourceCode = CodeEditor.Text;
            ParseBreakpoints();

            Action<string> printCallback = (message) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ConsoleOutput.AppendText(message + Environment.NewLine);
                    ConsoleOutput.ScrollToEnd();
                });
            };

            try
            {
                var lexer = new Lexer(sourceCode, null, printCallback);
                var tokens = lexer.Tokenize();
                var parser = new Parser(tokens);
                var ast = parser.Parse();

                _lastEvaluator = new Evaluator(printCallback);
                _lastEvaluator.SetSnapshotBreakpoints(_breakpoints, sourceCode);
                printCallback("=== Program Output ===");
                printCallback(_breakpoints.Count == 0
                    ? "[Breakpoints] none"
                    : $"[Breakpoints] lines: {string.Join(", ", _breakpoints)}");
                _lastEvaluator.Evaluate(ast);
                printCallback("======================");
                printCallback($"[Snapshots] {_lastEvaluator.Snapshots.Count}");

                BindSnapshots();
            }
            catch (Exception ex)
            {
                printCallback($"[Error] {ex.Message}");
                SnapshotListBox.ItemsSource = null;
                MemoryGrid.ItemsSource = null;
                ByteMemoryGrid.ItemsSource = null;
                SnapshotInfoText.Text = "Execution failed";
            }
        }

        private void ParseBreakpoints()
        {
            _breakpoints = Regex.Matches(BreakpointTextBox.Text ?? "", @"\d+")
                .Cast<Match>()
                .Select(m => int.Parse(m.Value))
                .Where(n => n > 0)
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }

        private void SetBreakpointText(IEnumerable<int> breakpoints)
        {
            _syncingBreakpoints = true;
            BreakpointTextBox.Text = string.Join(", ", breakpoints.Distinct().OrderBy(n => n));
            _syncingBreakpoints = false;
            ParseBreakpoints();
            UpdateBreakpointStatus();
        }

        private void UpdateBreakpointStatus()
        {
            if (BreakpointTextBox == null)
                return;

            ParseBreakpoints();
            BreakpointTextBox.ToolTip = _breakpoints.Count == 0
                ? "複数指定できます。例: 12, 25, 80"
                : $"Breakpoints: {string.Join(", ", _breakpoints)}";

            if (_currentSnapshot != null)
                UpdateSnapshotViews(_currentSnapshot);
        }

        private void BreakpointTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingBreakpoints)
                return;

            UpdateBreakpointStatus();
        }

        private void AddBreakpointButton_Click(object sender, RoutedEventArgs e)
        {
            ParseBreakpoints();

            int line = GetCurrentEditorLine();
            if (line <= 0)
                return;

            var next = _breakpoints.Append(line).ToList();
            SetBreakpointText(next);
        }

        private void ClearBreakpointsButton_Click(object sender, RoutedEventArgs e)
        {
            SetBreakpointText(Array.Empty<int>());
        }

        private int GetCurrentEditorLine()
        {
            if (CodeEditor == null)
                return 0;

            int lineIndex = CodeEditor.GetLineIndexFromCharacterIndex(CodeEditor.CaretIndex);
            if (lineIndex < 0)
                lineIndex = 0;

            return lineIndex + 1;
        }

        private void BindSnapshots()
        {
            if (_lastEvaluator == null || _lastEvaluator.Snapshots.Count == 0)
            {
                SnapshotListBox.ItemsSource = null;
                MemoryGrid.ItemsSource = null;
                ByteMemoryGrid.ItemsSource = null;
                SnapshotInfoText.Text = "No snapshots";
                return;
            }

            SnapshotListBox.ItemsSource = _lastEvaluator.Snapshots;

            int targetIndex = _lastEvaluator.Snapshots.Count - 1;

            if (_breakpoints.Count > 0)
            {
                int idx = _lastEvaluator.Snapshots.FindIndex(IsSnapshotBreakpoint);
                if (idx >= 0)
                    targetIndex = idx;
            }

            SnapshotListBox.SelectedIndex = targetIndex;
            SnapshotListBox.ScrollIntoView(SnapshotListBox.SelectedItem);
        }

        private void SnapshotListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SnapshotListBox.SelectedItem is not ExecutionSnapshot snapshot)
                return;

            UpdateSnapshotViews(snapshot);
        }

        private void PrevStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (SnapshotListBox.Items.Count == 0) return;
            if (SnapshotListBox.SelectedIndex <= 0) return;

            SnapshotListBox.SelectedIndex -= 1;
            SnapshotListBox.ScrollIntoView(SnapshotListBox.SelectedItem);
        }

        private void NextStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (SnapshotListBox.Items.Count == 0) return;
            if (SnapshotListBox.SelectedIndex >= SnapshotListBox.Items.Count - 1) return;

            SnapshotListBox.SelectedIndex += 1;
            SnapshotListBox.ScrollIntoView(SnapshotListBox.SelectedItem);
        }

        private void RunToBreakpointButton_Click(object sender, RoutedEventArgs e)
        {
            ParseBreakpoints();

            if (_lastEvaluator == null || _lastEvaluator.Snapshots.Count == 0 || _breakpoints.Count == 0)
                return;

            int currentStep = -1;
            if (SnapshotListBox.SelectedItem is ExecutionSnapshot current)
                currentStep = current.Step;

            var next = _lastEvaluator.Snapshots
                .Where(s => s.Step > currentStep && IsSnapshotBreakpoint(s))
                .OrderBy(s => s.Step)
                .FirstOrDefault();

            if (next == null)
            {
                MessageBox.Show("次のブレークポイントはありません。", "Breakpoint",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SnapshotListBox.SelectedItem = next;
            SnapshotListBox.ScrollIntoView(next);
        }

        private bool IsSnapshotBreakpoint(ExecutionSnapshot snapshot)
        {
            if (snapshot == null)
                return false;

            return _breakpoints.Contains(snapshot.SourceLine) ||
                (snapshot.BreakpointLine > 0 && _breakpoints.Contains(snapshot.BreakpointLine));
        }

        private void RestartFromSnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSnapshot == null)
            {
                MessageBox.Show("再開するスナップショットを選択してください。", "Restart",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ConsoleOutput.Clear();
            ParseBreakpoints();

            Action<string> printCallback = (message) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ConsoleOutput.AppendText(message + Environment.NewLine);
                    ConsoleOutput.ScrollToEnd();
                });
            };

            try
            {
                var lexer = new Lexer(CodeEditor.Text, null, printCallback);
                var tokens = lexer.Tokenize();
                var parser = new Parser(tokens);
                var ast = parser.Parse();

                var restartSnapshot = CloneSnapshot(_currentSnapshot);

                _lastEvaluator = new Evaluator(printCallback);
                _lastEvaluator.SetSnapshotBreakpoints(_breakpoints, CodeEditor.Text);

                printCallback("=== Restart Output ===");
                printCallback($"[Restart] from step {_currentSnapshot.Step}, line {_currentSnapshot.SourceLine}, function {_currentSnapshot.FunctionName}");
                printCallback(_breakpoints.Count == 0
                    ? "[Breakpoints] none"
                    : $"[Breakpoints] lines: {string.Join(", ", _breakpoints)}");

                _lastEvaluator.EvaluateFromSnapshot(ast, restartSnapshot);

                printCallback("======================");
                printCallback($"[Snapshots] {_lastEvaluator.Snapshots.Count}");

                BindSnapshots();
            }
            catch (Exception ex)
            {
                printCallback($"[Error] {ex.Message}");
            }
        }

        private static ExecutionSnapshot CloneSnapshot(ExecutionSnapshot snapshot)
        {
            var memoryCopy = new byte[snapshot.Memory.Length];
            Array.Copy(snapshot.Memory, memoryCopy, snapshot.Memory.Length);

            var envCopy = new Dictionary<string, VarInfo>();
            foreach (var kvp in snapshot.Env)
                envCopy[kvp.Key] = kvp.Value.Clone();

            var regionCopy = new List<MemoryRegionInfo>();
            foreach (var region in snapshot.Regions)
                regionCopy.Add(region.Clone());

            return new ExecutionSnapshot
            {
                Step = snapshot.Step,
                SourceLine = snapshot.SourceLine,
                BreakpointLine = snapshot.BreakpointLine,
                FunctionName = snapshot.FunctionName,
                Event = snapshot.Event,
                Memory = memoryCopy,
                Env = envCopy,
                Regions = regionCopy,
                StackPointer = snapshot.StackPointer,
                LiteralPointer = snapshot.LiteralPointer,
                ScopeDepth = snapshot.ScopeDepth
            };
        }

        private void UpdateSnapshotViews(ExecutionSnapshot snapshot)
        {
            _currentSnapshot = snapshot;
            bool isBreakpoint = IsSnapshotBreakpoint(snapshot);
            string breakpointText = snapshot.BreakpointLine > 0 && snapshot.BreakpointLine != snapshot.SourceLine
                ? $" | BreakpointLine: {snapshot.BreakpointLine}"
                : "";

            SnapshotInfoText.Text =
                $"Step: {snapshot.Step} | Line: {snapshot.SourceLine}{breakpointText} | Func: {snapshot.FunctionName} | Event: {snapshot.Event} | ScopeDepth: {snapshot.ScopeDepth} | SP: 0x{snapshot.StackPointer:X4}" +
                (isBreakpoint ? " | BREAKPOINT" : "");

            UpdateMemoryView(snapshot);
            UpdateByteMemoryView(snapshot);
            UpdateStructInspector();
        }

        private void UpdateMemoryView(ExecutionSnapshot snapshot)
        {
            var memoryItems = new List<MemoryDisplayItem>();

            foreach (var region in snapshot.Regions.OrderBy(r => r.Address))
            {
                string typeStr;
                string nameStr = region.Label;
                string valueStr = "";

                var variableEntry = snapshot.Env.FirstOrDefault(kvp => kvp.Value.Address == region.Address);

                if (!string.IsNullOrEmpty(variableEntry.Key))
                {
                    string varName = variableEntry.Key;
                    VarInfo info = variableEntry.Value;

                    typeStr = info.TypeInfo.ToDisplayString();

                    nameStr = varName;

                    if (info.IsArray)
                    {
                        var parts = new List<string>();
                        for (int i = 0; i < info.ArrayLength; i++)
                        {
                            int addr = info.Address + i * info.ElementSize;
                            int value = info.ElementSize == 1
                                ? snapshot.Memory[addr]
                                : BitConverter.ToInt32(snapshot.Memory, addr);

                            parts.Add(info.Type == "char"
                                ? $"{value} ('{(char)value}')"
                                : value.ToString());
                        }
                        valueStr = "[ " + string.Join(", ", parts) + " ]";
                    }
                    else if (info.Size == 1)
                    {
                        byte b = snapshot.Memory[info.Address];
                        valueStr = $"{b} ('{(char)b}')";
                    }
                    else if (info.Type == "float" && !info.IsPointer)
                    {
                        float val = BitConverter.ToSingle(snapshot.Memory, info.Address);
                        valueStr = val.ToString("F3"); // 小数第3位まで表示
                    }
                    else if (info.Type == "double" && !info.IsPointer)
                    {
                        double val = BitConverter.ToDouble(snapshot.Memory, info.Address);
                        valueStr = val.ToString("F3");
                    }
                    else
                    {
                        // 既存の int 処理
                        int val = BitConverter.ToInt32(snapshot.Memory, info.Address);
                        valueStr = val.ToString();

                        if (info.IsPointer)
                            valueStr += $" (0x{val:X4})";
                    }
                }
                else
                {
                    typeStr = region.IsStringLiteral ? "string literal" : "region";

                    var bytes = new List<string>();
                    for (int i = 0; i < region.Size; i++)
                    {
                        byte b = snapshot.Memory[region.Address + i];
                        char ch = (b >= 32 && b <= 126) ? (char)b : '.';
                        bytes.Add($"{b} ('{ch}')");
                    }
                    valueStr = "[ " + string.Join(", ", bytes) + " ]";
                }

                memoryItems.Add(new MemoryDisplayItem
                {
                    Address = $"0x{region.Address:X4}",
                    AddressValue = region.Address,
                    Type = typeStr,
                    VariableName = nameStr,
                    Value = valueStr,
                    BaseType = !string.IsNullOrEmpty(variableEntry.Key) ? variableEntry.Value.Type : null,
                    IsPointer = !string.IsNullOrEmpty(variableEntry.Key) && variableEntry.Value.IsPointer,
                    Size = !string.IsNullOrEmpty(variableEntry.Key) ? variableEntry.Value.Size : region.Size,
                    IsScalar = !string.IsNullOrEmpty(variableEntry.Key) && !variableEntry.Value.IsArray && !(variableEntry.Value.IsStruct && !variableEntry.Value.IsPointer),
                    IsArray = !string.IsNullOrEmpty(variableEntry.Key) && variableEntry.Value.IsArray,
                    IsStruct = !string.IsNullOrEmpty(variableEntry.Key) && variableEntry.Value.IsStruct,
                    IsStructArray = !string.IsNullOrEmpty(variableEntry.Key) && variableEntry.Value.IsArray && variableEntry.Value.IsStruct && !variableEntry.Value.IsPointer,
                    ArrayLength = !string.IsNullOrEmpty(variableEntry.Key) ? variableEntry.Value.ArrayLength : 0,
                    ElementSize = !string.IsNullOrEmpty(variableEntry.Key) ? variableEntry.Value.ElementSize : 0
                });
            }

            MemoryGrid.ItemsSource = memoryItems;
        }

        private void MemoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_syncingSelection && MemoryGrid.SelectedItem != null)
            {
                _syncingSelection = true;
                ByteMemoryGrid.SelectedItem = null;
                _syncingSelection = false;
            }

            UpdateStructInspector();
            UpdateEditTargetInfo();
        }

        private void StructIndexTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateStructInspector();
        }

        private void UpdateStructInspector()
        {
            if (StructMemberGrid == null || StructInspectorInfoText == null)
                return;

            StructMemberGrid.ItemsSource = null;

            if (_lastEvaluator == null || _currentSnapshot == null)
            {
                StructInspectorInfoText.Text = "No snapshot selected";
                return;
            }

            if (MemoryGrid.SelectedItem is not MemoryDisplayItem selected ||
                string.IsNullOrWhiteSpace(selected.VariableName))
            {
                StructInspectorInfoText.Text = "Select a struct or array";
                return;
            }

            if (selected.IsScalar)
            {
                StructInspectorInfoText.Text = $"Scalar variable: {selected.VariableName}";
                UpdateEditTargetInfo();
                return;
            }

            try
            {
                List<StructMemberInspectItem> members;
                string label;

                if (selected.IsStructArray)
                {
                    if (!int.TryParse(StructIndexTextBox.Text, out int index))
                    {
                        StructInspectorInfoText.Text = "Index must be a number";
                        return;
                    }

                    members = _lastEvaluator.InspectStructArrayElement(
                        _currentSnapshot,
                        selected.VariableName,
                        index);
                    label = $"{selected.VariableName}[{index}]";
                }
                else if (selected.IsArray)
                {
                    members = InspectArrayElements(_currentSnapshot, selected);
                    label = selected.VariableName;
                }
                else
                {
                    members = _lastEvaluator.InspectStructVariable(
                        _currentSnapshot,
                        selected.VariableName);
                    label = selected.VariableName;
                }

                StructMemberGrid.ItemsSource = members;
                StructInspectorInfoText.Text = label;
                UpdateEditTargetInfo();
            }
            catch (Exception ex)
            {
                StructInspectorInfoText.Text = ex.Message;
                UpdateEditTargetInfo();
            }
        }

        private static List<StructMemberInspectItem> InspectArrayElements(ExecutionSnapshot snapshot, MemoryDisplayItem selected)
        {
            var items = new List<StructMemberInspectItem>();

            if (selected.IsStruct)
                throw new Exception($"Struct inspector: '{selected.VariableName}' is not a scalar array");

            for (int i = 0; i < selected.ArrayLength; i++)
            {
                int address = selected.AddressValue + i * selected.ElementSize;
                items.Add(new StructMemberInspectItem
                {
                    MemberName = $"{selected.VariableName}[{i}]",
                    Type = selected.Type.Replace($"[{selected.ArrayLength}]", ""),
                    Address = $"0x{address:X4}",
                    AddressValue = address,
                    BaseType = selected.BaseType,
                    IsPointer = selected.IsPointer,
                    Size = selected.ElementSize,
                    Value = FormatArrayElementValue(snapshot.Memory, address, selected.BaseType, selected.IsPointer, selected.ElementSize)
                });
            }

            return items;
        }

        private static string FormatArrayElementValue(byte[] memory, int address, string baseType, bool isPointer, int size)
        {
            if (address < 0 || address >= memory.Length)
                return "<out of range>";

            if (!isPointer && baseType == "char")
            {
                int value = memory[address];
                return $"{value} ('{(char)value}')";
            }

            if (!isPointer && baseType == "short")
            {
                if (address + 2 > memory.Length)
                    return "<out of range>";
                return BitConverter.ToInt16(memory, address).ToString();
            }

            if (address + Math.Max(size, 4) > memory.Length)
                return "<out of range>";

            int intValue = BitConverter.ToInt32(memory, address);
            return isPointer ? $"{intValue} (0x{intValue:X4})" : intValue.ToString();
        }

        private void StructMemberGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_syncingSelection && StructMemberGrid.SelectedItem != null)
            {
                _syncingSelection = true;
                ByteMemoryGrid.SelectedItem = null;
                _syncingSelection = false;
            }

            UpdateEditTargetInfo();
        }

        private void ByteMemoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_syncingSelection && ByteMemoryGrid.SelectedItem != null)
            {
                _syncingSelection = true;
                MemoryGrid.SelectedItem = null;
                StructMemberGrid.SelectedItem = null;
                _syncingSelection = false;
            }

            UpdateEditTargetInfo();
        }

        private void ApplyValueButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSnapshot == null)
            {
                EditValueInfoText.Text = "No snapshot selected";
                return;
            }

            try
            {
                if (StructMemberGrid.SelectedItem is StructMemberInspectItem member)
                {
                    WriteSnapshotValue(_currentSnapshot, member.AddressValue, member.BaseType, member.IsPointer, member.Size, EditValueTextBox.Text);
                    UpdateByteMemoryView(_currentSnapshot);
                    UpdateStructInspector();
                    EditValueInfoText.Text = $"Updated {member.MemberName}";
                    return;
                }

                if (ByteMemoryGrid.SelectedItem is ByteMemoryDisplayItem byteItem)
                {
                    WriteSnapshotValue(_currentSnapshot, byteItem.AddressValue, "char", false, 1, EditValueTextBox.Text);
                    UpdateMemoryView(_currentSnapshot);
                    UpdateByteMemoryView(_currentSnapshot);
                    EditValueInfoText.Text = $"Updated {byteItem.Address}";
                    return;
                }

                if (MemoryGrid.SelectedItem is MemoryDisplayItem memoryItem)
                {
                    if (!memoryItem.IsScalar)
                    {
                        EditValueInfoText.Text = "Select a scalar variable, struct member, or byte";
                        return;
                    }

                    string variableName = memoryItem.VariableName;
                    WriteSnapshotValue(_currentSnapshot, memoryItem.AddressValue, memoryItem.BaseType, memoryItem.IsPointer, memoryItem.Size, EditValueTextBox.Text);
                    UpdateMemoryView(_currentSnapshot);
                    UpdateByteMemoryView(_currentSnapshot);
                    ReselectMemoryItem(variableName);
                    UpdateStructInspector();
                    EditValueInfoText.Text = $"Updated {variableName}";
                    return;
                }

                EditValueInfoText.Text = "Select a value";
            }
            catch (Exception ex)
            {
                EditValueInfoText.Text = ex.Message;
            }
        }

        private void ReselectMemoryItem(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName) || MemoryGrid?.Items == null)
                return;

            foreach (var item in MemoryGrid.Items)
            {
                if (item is MemoryDisplayItem memoryItem && memoryItem.VariableName == variableName)
                {
                    MemoryGrid.SelectedItem = memoryItem;
                    MemoryGrid.ScrollIntoView(memoryItem);
                    return;
                }
            }
        }

        private void UpdateEditTargetInfo()
        {
            if (EditValueInfoText == null)
                return;

            if (StructMemberGrid?.SelectedItem is StructMemberInspectItem member)
            {
                EditValueInfoText.Text = $"Target: {member.MemberName}";
                return;
            }

            if (ByteMemoryGrid?.SelectedItem is ByteMemoryDisplayItem byteItem)
            {
                EditValueInfoText.Text = $"Target: {byteItem.Address}";
                return;
            }

            if (MemoryGrid?.SelectedItem is MemoryDisplayItem memoryItem)
            {
                EditValueInfoText.Text = memoryItem.IsScalar
                    ? $"Target: {memoryItem.VariableName}"
                    : "Select a scalar variable, struct member, or byte";
                return;
            }

            EditValueInfoText.Text = "Select a value";
        }

        private static void WriteSnapshotValue(ExecutionSnapshot snapshot, int address, string baseType, bool isPointer, int size, string text)
        {
            if (snapshot?.Memory == null)
                throw new Exception("No snapshot memory");

            int value = ParseEditValue(text);
            int writeSize = isPointer ? 4 : size;

            if (writeSize <= 0)
                throw new Exception("Invalid value size");

            if (address < 0 || address + writeSize > snapshot.Memory.Length)
                throw new Exception($"Address out of range: 0x{address:X4}");

            if (!isPointer && baseType == "char")
            {
                snapshot.Memory[address] = unchecked((byte)value);
                return;
            }

            if (!isPointer && baseType == "short")
            {
                Array.Copy(BitConverter.GetBytes((short)value), 0, snapshot.Memory, address, 2);
                return;
            }

            if (writeSize == 1)
            {
                snapshot.Memory[address] = unchecked((byte)value);
                return;
            }

            Array.Copy(BitConverter.GetBytes(value), 0, snapshot.Memory, address, 4);
        }

        private static int ParseEditValue(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0)
                throw new Exception("Enter a value");

            if (text.Length >= 3 && text.StartsWith("'") && text.EndsWith("'"))
            {
                string body = text.Substring(1, text.Length - 2);
                return body switch
                {
                    @"\n" => '\n',
                    @"\t" => '\t',
                    @"\0" => 0,
                    @"\\" => '\\',
                    @"\'" => '\'',
                    _ => body.Length == 1 ? body[0] : throw new Exception("Invalid char literal")
                };
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt32(text.Substring(2), 16);

            return int.Parse(text);
        }

        private void UpdateByteMemoryView(ExecutionSnapshot snapshot)
        {
            var byteItems = new List<ByteMemoryDisplayItem>();

            int usedSize = snapshot.Regions.Count == 0
                ? 0
                : snapshot.Regions.Max(r => r.Address + r.Size);

            for (int addr = 0; addr < usedSize; addr++)
            {
                byte b = snapshot.Memory[addr];

                string charValue = (b >= 32 && b <= 126)
                    ? ((char)b).ToString()
                    : ".";

                string note = "";

                foreach (var region in snapshot.Regions.OrderBy(r => r.Address))
                {
                    if (addr >= region.Address && addr < region.Address + region.Size)
                    {
                        var variableEntry = snapshot.Env.FirstOrDefault(kvp => kvp.Value.Address == region.Address);

                        if (!string.IsNullOrEmpty(variableEntry.Key))
                        {
                            string varName = variableEntry.Key;
                            VarInfo info = variableEntry.Value;

                            if (info.IsArray)
                            {
                                int offset = addr - info.Address;
                                int elementIndex = offset / info.ElementSize;
                                int elementOffset = offset % info.ElementSize;

                                note = elementOffset == 0
                                    ? $"{varName}[{elementIndex}] start"
                                    : $"{varName}[{elementIndex}] +{elementOffset}";
                            }
                            else
                            {
                                note = addr == info.Address
                                    ? $"{varName} ({info.TypeInfo.ToDisplayString()}) start"
                                    : $"{varName} +{addr - info.Address}";
                            }
                        }
                        else
                        {
                            int offset = addr - region.Address;
                            note = offset == 0
                                ? $"{region.Label} start"
                                : $"{region.Label} +{offset}";
                        }

                        break;
                    }
                }

                byteItems.Add(new ByteMemoryDisplayItem
                {
                    Address = $"0x{addr:X4}",
                    AddressValue = addr,
                    HexValue = $"0x{b:X2}",
                    DecimalValue = b,
                    CharValue = charValue,
                    Note = note
                });
            }

            ByteMemoryGrid.ItemsSource = byteItems;
        }
    }

    public class MemoryDisplayItem
    {
        public string Address { get; set; }
        public int AddressValue { get; set; }
        public string Type { get; set; }
        public string VariableName { get; set; }
        public string Value { get; set; }
        public string BaseType { get; set; }
        public bool IsPointer { get; set; }
        public int Size { get; set; }
        public bool IsScalar { get; set; }
        public bool IsArray { get; set; }
        public bool IsStruct { get; set; }
        public bool IsStructArray { get; set; }
        public int ArrayLength { get; set; }
        public int ElementSize { get; set; }
    }

    public class ByteMemoryDisplayItem
    {
        public string Address { get; set; }
        public int AddressValue { get; set; }
        public string HexValue { get; set; }
        public int DecimalValue { get; set; }
        public string CharValue { get; set; }
        public string Note { get; set; }
    }
}
