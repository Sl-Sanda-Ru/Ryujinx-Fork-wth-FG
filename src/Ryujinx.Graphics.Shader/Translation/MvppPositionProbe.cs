
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppPositionProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_P21_SHADER") == "1";

        private const int MaxSliceSteps = 20000;
        private const int MaxIndexSliceSteps = 512;

        private static readonly object _sync = new();
        private static readonly Dictionary<string, int> _patterns = new();
        private static int _analyzed;
        private static int _withPosition;
        private static int _withCbufEvidence;
        private static int _dynUnresolved;

        internal static void Analyze(IReadOnlyList<Function> funcs, ResourceManager resourceManager, ShaderStage stage, ulong address)
        {
            if (!Enabled || stage != ShaderStage.Vertex)
            {
                return;
            }

            try
            {
                AnalyzeImpl(funcs, resourceManager, address);
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP P21 pos-slice: analysis failed for vs @0x{address:X}: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void AnalyzeImpl(IReadOnlyList<Function> funcs, ResourceManager resourceManager, ulong address)
        {
            HashSet<(int Slot, int WordOffset)> staticReads = new();
            SortedSet<string> dynamicReads = new();
            SortedSet<string> inputs = new();
            bool sawPosition = false;
            bool hasCalls = false;

            HashSet<INode> visitedNodes = new();
            Stack<Operand> stack = new();

            foreach (Function func in funcs)
            {
                foreach (BasicBlock block in func.Blocks)
                {
                    foreach (INode node in block.Operations)
                    {
                        if (node is not Operation op ||
                            op.Inst != Instruction.Store ||
                            op.StorageKind != StorageKind.Output ||
                            op.SourcesCount < 2)
                        {
                            continue;
                        }

                        Operand target = op.GetSource(0);
                        if (target.Type != OperandType.Constant || target.Value != (int)IoVariable.Position)
                        {
                            continue;
                        }

                        sawPosition = true;

                        for (int i = 1; i < op.SourcesCount; i++)
                        {
                            stack.Push(op.GetSource(i));
                        }
                    }
                }
            }

            int steps = 0;
            while (stack.Count > 0 && steps++ < MaxSliceSteps)
            {
                Operand oper = stack.Pop();
                if (oper == null)
                {
                    continue;
                }

                if (oper.Type == OperandType.ConstantBuffer)
                {
                    staticReads.Add((oper.GetCbufSlot(), oper.GetCbufOffset()));

                    continue;
                }

                if (oper.Type != OperandType.LocalVariable || oper.AsgOp == null || !visitedNodes.Add(oper.AsgOp))
                {
                    continue;
                }

                if (oper.AsgOp is PhiNode phi)
                {
                    for (int i = 0; i < phi.SourcesCount; i++)
                    {
                        stack.Push(phi.GetSource(i));
                    }

                    continue;
                }

                if (oper.AsgOp is not Operation defOp)
                {
                    continue;
                }

                if (defOp.Inst == Instruction.Load && defOp.StorageKind == StorageKind.ConstantBuffer)
                {
                    HandleCbufLoad(defOp, resourceManager, staticReads, dynamicReads);

                    continue;
                }

                if (defOp.Inst == Instruction.Load && defOp.StorageKind == StorageKind.Input && defOp.SourcesCount > 0)
                {
                    inputs.Add(InputLabel(defOp));

                    continue;
                }

                if (defOp.Inst == Instruction.Call)
                {
                    hasCalls = true;
                }

                for (int i = 0; i < defOp.SourcesCount; i++)
                {
                    stack.Push(defOp.GetSource(i));
                }
            }

            Report(address, sawPosition, staticReads, dynamicReads, inputs, hasCalls, steps >= MaxSliceSteps);
        }

        private static void HandleCbufLoad(
            Operation op,
            ResourceManager resourceManager,
            HashSet<(int, int)> staticReads,
            SortedSet<string> dynamicReads)
        {
            int slot = -1;
            Operand binding = op.GetSource(0);
            if (binding.Type == OperandType.Constant && resourceManager.TryGetConstantBufferSlot(binding.Value, out int s))
            {
                slot = s;
            }

            if (slot < 0)
            {
                dynamicReads.Add("rxsup");

                return;
            }

            Operand vecIndex = op.SourcesCount > 2 ? op.GetSource(2) : null;
            Operand elemIndex = op.SourcesCount > 3 ? op.GetSource(3) : null;

            if (vecIndex != null && vecIndex.Type == OperandType.Constant &&
                elemIndex != null && elemIndex.Type == OperandType.Constant)
            {
                staticReads.Add((slot, vecIndex.Value * 4 + elemIndex.Value));

                return;
            }

            dynamicReads.Add($"cb{slot}[idx<-{ClassifyIndex(vecIndex, resourceManager)}]");
        }

        private static string ClassifyIndex(Operand start, ResourceManager resourceManager)
        {
            if (start == null)
            {
                return "none";
            }

            SortedSet<string> labels = new();
            HashSet<INode> visited = new();
            Stack<Operand> stack = new();
            stack.Push(start);

            int steps = 0;
            while (stack.Count > 0 && steps++ < MaxIndexSliceSteps)
            {
                Operand oper = stack.Pop();
                if (oper == null)
                {
                    continue;
                }

                if (oper.Type == OperandType.ConstantBuffer)
                {
                    labels.Add($"cb{oper.GetCbufSlot()}[0x{oper.GetCbufOffset() * 4:X}]");

                    continue;
                }

                if (oper.Type != OperandType.LocalVariable || oper.AsgOp == null || !visited.Add(oper.AsgOp))
                {
                    continue;
                }

                if (oper.AsgOp is PhiNode phi)
                {
                    for (int i = 0; i < phi.SourcesCount; i++)
                    {
                        stack.Push(phi.GetSource(i));
                    }

                    continue;
                }

                if (oper.AsgOp is not Operation defOp)
                {
                    continue;
                }

                if (defOp.Inst == Instruction.Load && defOp.StorageKind == StorageKind.ConstantBuffer)
                {
                    int slot = -1;
                    Operand binding = defOp.GetSource(0);
                    if (binding.Type == OperandType.Constant && resourceManager.TryGetConstantBufferSlot(binding.Value, out int s))
                    {
                        slot = s;
                    }

                    Operand vec = defOp.SourcesCount > 2 ? defOp.GetSource(2) : null;
                    Operand elem = defOp.SourcesCount > 3 ? defOp.GetSource(3) : null;
                    labels.Add(vec != null && vec.Type == OperandType.Constant && elem != null && elem.Type == OperandType.Constant
                        ? $"cb{slot}[0x{(vec.Value * 4 + elem.Value) * 4:X}]"
                        : $"cb{slot}[dyn]");

                    continue;
                }

                if (defOp.Inst == Instruction.Load && defOp.StorageKind == StorageKind.Input && defOp.SourcesCount > 0)
                {
                    labels.Add(InputLabel(defOp));

                    continue;
                }

                for (int i = 0; i < defOp.SourcesCount; i++)
                {
                    stack.Push(defOp.GetSource(i));
                }
            }

            return labels.Count == 0 ? "unresolved" : string.Join("+", labels);
        }

        private static string InputLabel(Operation load)
        {
            Operand io = load.GetSource(0);
            if (io.Type != OperandType.Constant)
            {
                return "input?";
            }

            IoVariable v = (IoVariable)io.Value;
            if (v == IoVariable.UserDefined)
            {
                Operand loc = load.SourcesCount > 1 ? load.GetSource(1) : null;

                return loc != null && loc.Type == OperandType.Constant ? $"vtx-attr{loc.Value}" : "vtx-attr?";
            }

            return v.ToString();
        }

        private static void Report(
            ulong address,
            bool sawPosition,
            HashSet<(int Slot, int WordOffset)> staticReads,
            SortedSet<string> dynamicReads,
            SortedSet<string> inputs,
            bool hasCalls,
            bool truncated)
        {
            Dictionary<int, List<int>> yPerSlot = new();
            foreach ((int slot, int wordOffset) in staticReads)
            {
                if (!yPerSlot.TryGetValue(slot, out List<int> list))
                {
                    yPerSlot[slot] = list = new List<int>();
                }

                list.Add(wordOffset * 4);
            }

            StringBuilder sb = new();
            sb.Append(!sawPosition ? "NO-POSITION" : staticReads.Count == 0 && dynamicReads.Count == 0 ? "NO-CBUF" : "ok");

            foreach (KeyValuePair<int, List<int>> kv in yPerSlot)
            {
                kv.Value.Sort();
                sb.Append($" cb{kv.Key}[");
                if (kv.Value.Count <= 8)
                {
                    sb.Append(string.Join(",", kv.Value.ConvertAll(o => $"0x{o:X}")));
                }
                else
                {
                    sb.Append($"{kv.Value.Count} reads 0x{kv.Value[0]:X}-0x{kv.Value[^1]:X}");
                }

                sb.Append(']');
            }

            foreach (string dyn in dynamicReads)
            {
                sb.Append(' ').Append(dyn);
            }

            if (inputs.Count > 0)
            {
                sb.Append(" in:{").Append(string.Join(",", inputs)).Append('}');
            }

            if (hasCalls)
            {
                sb.Append(" [calls]");
            }

            if (truncated)
            {
                sb.Append(" [truncated]");
            }

            string pattern = sb.ToString();

            lock (_sync)
            {
                _analyzed++;
                if (sawPosition)
                {
                    _withPosition++;
                }

                if (staticReads.Count > 0 || dynamicReads.Count > 0)
                {
                    _withCbufEvidence++;
                }

                foreach (string dyn in dynamicReads)
                {
                    if (dyn.Contains("unresolved"))
                    {
                        _dynUnresolved++;
                        break;
                    }
                }

                if (_patterns.TryGetValue(pattern, out int count))
                {
                    _patterns[pattern] = count + 1;
                }
                else
                {
                    _patterns[pattern] = 1;
                    Logger.Info?.Print(LogClass.Gpu, $"MVPP P21 pos-slice NEW #{_patterns.Count}: vs @0x{address:X} {pattern}");
                }

                if (_analyzed % 500 == 0)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP P21 pos-slice progress: {_analyzed} vs analyzed, {_withPosition} with position, " +
                        $"{_withCbufEvidence} with cbuf evidence, {_patterns.Count} distinct patterns, {_dynUnresolved} with unresolved dynamic index.");
                }
            }
        }
    }
}
