using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CInterpreterWpf
{
    public partial class MainWindow : Window
    {
        private Evaluator _lastEvaluator;
        private List<int> _breakpoints = new List<int>();

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
                var lexer = new Lexer(sourceCode);
                var tokens = lexer.Tokenize();
                var parser = new Parser(tokens);
                var ast = parser.Parse();

                _lastEvaluator = new Evaluator(printCallback);
                printCallback("=== Program Output ===");
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
            _breakpoints = BreakpointTextBox.Text
                .Split(new[] { ',', ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out int n) ? (int?)n : null)
                .Where(n => n.HasValue)
                .Select(n => n.Value)
                .Distinct()
                .OrderBy(n => n)
                .ToList();
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
                int idx = _lastEvaluator.Snapshots.FindIndex(s => _breakpoints.Contains(s.Step));
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
                .Where(s => s.Step > currentStep && _breakpoints.Contains(s.Step))
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

        private void UpdateSnapshotViews(ExecutionSnapshot snapshot)
        {
            bool isBreakpoint = _breakpoints.Contains(snapshot.Step);

            SnapshotInfoText.Text =
                $"Step: {snapshot.Step} | Event: {snapshot.Event} | ScopeDepth: {snapshot.ScopeDepth} | SP: 0x{snapshot.StackPointer:X4}" +
                (isBreakpoint ? " | BREAKPOINT" : "");

            UpdateMemoryView(snapshot);
            UpdateByteMemoryView(snapshot);
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
                    Type = typeStr,
                    VariableName = nameStr,
                    Value = valueStr
                });
            }

            MemoryGrid.ItemsSource = memoryItems;
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
        public string Type { get; set; }
        public string VariableName { get; set; }
        public string Value { get; set; }
    }

    public class ByteMemoryDisplayItem
    {
        public string Address { get; set; }
        public string HexValue { get; set; }
        public int DecimalValue { get; set; }
        public string CharValue { get; set; }
        public string Note { get; set; }
    }
}