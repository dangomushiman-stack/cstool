using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CInterpreterWpf
{
    public partial class Evaluator
    {

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

        private string ReadCString(int addr)
        {
            var bytes = new List<byte>();
            int current = addr;

            while (true)
            {
                EnsureMemoryRange(current, 1);
                byte b = Memory[current];
                if (b == 0) break;
                bytes.Add(b);
                current++;
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private void CopyBytes(int srcAddr, int dstAddr, int count)
        {
            EnsureMemoryRange(srcAddr, count);
            EnsureMemoryRange(dstAddr, count);
            Array.Copy(Memory, srcAddr, Memory, dstAddr, count);
        }

        private void WriteByte(int addr, int value)
        {
            EnsureMemoryRange(addr, 1);
            Memory[addr] = (byte)value;
        }

        private int ReadScalarAtAddress(string type, bool isPointer, int addr)
        {
            int size = isPointer ? 4 : (type == "char" ? 1 : 4);
            return size == 1 ? ReadByte(addr) : ReadInt(addr);
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

        private void EnsureMemoryRange(int addr, int size)
        {
            if (addr < 0 || size < 0 || addr + size > Memory.Length)
                throw new Exception($"Execution Error: memory access out of range at 0x{addr:X4}");
        }

        private void EnsureSpaceForStackAllocation(int size, string label = null)
        {
            if (size <= 0)
                throw new Exception($"Execution Error: invalid allocation size for '{label}'");

            if (_stackPtr + size > _literalPtr)
                throw new Exception($"Execution Error: out of memory allocating '{label}' size {size}");
        }

        private void ZeroMemory(int addr, int size)
        {
            EnsureMemoryRange(addr, size);
            Array.Clear(Memory, addr, size);
        }

        private void CaptureSnapshot(string evt)
        {
            int step = _snapshotStep++;
            int sourceLine = _currentSourceLine;
            if (!_snapshotBreakpoints.Contains(sourceLine))
                return;

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
                Step = step,
                SourceLine = sourceLine,
                Event = evt,
                Memory = memoryCopy,
                Env = envCopy,
                Regions = regionCopy,
                StackPointer = _stackPtr,
                LiteralPointer = _literalPtr,
                ScopeDepth = _scopes.Count
            });
        }

        private void EnterScope()
        {
            _scopes.Push(new ScopeFrame
            {
                SavedStackPtr = _stackPtr,
                SavedRegionCount = Regions.Count
            });
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

            // C#縺ｮ譁・ｭ怜・繧旦TF-8縺ｮ繝舌う繝磯・蛻励↓螟画鋤
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(value);
            int size = utf8Bytes.Length + 1; // null邨らｫｯ譁・ｭ・+1)
            int addr = AllocateLiteralRegion(size, $"string literal \"{value}\"");

            // UTF-8縺ｮ繝舌う繝亥・繧偵Γ繝｢繝ｪ縺ｫ譖ｸ縺崎ｾｼ繧
            for (int i = 0; i < utf8Bytes.Length; i++)
                WriteByte(addr + i, utf8Bytes[i]);

            WriteByte(addr + utf8Bytes.Length, 0); // null邨らｫｯ
            _stringLiteralPool[value] = addr;

            CaptureSnapshot($"String literal allocated: \"{value}\"");
            return addr;
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
            EnsureSpaceForStackAllocation(size, label);

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
    }
}
