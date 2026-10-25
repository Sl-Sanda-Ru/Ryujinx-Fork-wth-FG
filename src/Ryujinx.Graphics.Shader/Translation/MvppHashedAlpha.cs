
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using System;
using System.Collections.Generic;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppHashedAlpha
    {
        public static readonly bool ProbeEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_HASHPROBE") == "1";

        public static readonly bool RewriteEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_HASHED_ALPHA") == "1";

        private static readonly float HashBand = ParseBand();

        private static float ParseBand()
        {
            string s = Environment.GetEnvironmentVariable("RYUJINX_MVPP_HASHED_BAND");

            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0f && v <= 1f
                ? v
                : 0.3f;
        }

        private const float MinCutoutThreshold = 0.05f;

        public static readonly bool CaptureEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_ALPHA_HISTO") == "1";

        public static readonly float AlphaLodBias = ParseLodBias();
        public static bool LodBiasEnabled => !float.IsNaN(AlphaLodBias);

        private static float ParseLodBias()
        {
            string s = Environment.GetEnvironmentVariable("RYUJINX_MVPP_ALPHA_LODBIAS");

            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0f && v <= 8f
                ? v
                : float.NaN;
        }

        private const int MaxTraceSteps = 256;

        private static readonly object _sync = new();
        private static readonly Dictionary<string, int> _patterns = new();
        private static int _shadersWithKil;
        private static int _totalDiscards;
        private static int _totalMatched;
        private static int _chainDirect;
        private static int _chainIndirect;
        private static long _lastSummaryMs;

        internal static void Analyze(IReadOnlyList<Function> funcs, ShaderStage stage, ulong address)
        {
            if (!ProbeEnabled || stage != ShaderStage.Fragment)
            {
                return;
            }

            try
            {
                AnalyzeImpl(funcs, address);
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"MVPP-HASHPROBE: analysis failed for fs @0x{address:X}: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void AnalyzeImpl(IReadOnlyList<Function> funcs, ulong address)
        {
            int discards = 0;
            int matched = 0;
            SortedSet<string> forms = new();

            foreach (Function func in funcs)
            {
                foreach (BasicBlock block in func.Blocks)
                {
                    foreach (INode node in block.Operations)
                    {
                        if (node is not Operation op || op.Inst != Instruction.Discard)
                        {
                            continue;
                        }

                        discards++;

                        string form = MatchDiscard(block, out bool ok);

                        if (ok)
                        {
                            matched++;
                        }

                        forms.Add(form);
                    }
                }
            }

            if (discards == 0)
            {
                return;
            }

            string pattern = $"kil={discards} match={matched} [{string.Join(" ; ", forms)}]";

            lock (_sync)
            {
                _shadersWithKil++;
                _totalDiscards += discards;
                _totalMatched += matched;

                if (_patterns.TryGetValue(pattern, out int count))
                {
                    _patterns[pattern] = count + 1;
                }
                else
                {
                    _patterns[pattern] = 1;

                    Logger.Info?.Print(LogClass.Gpu, $"MVPP-HASHPROBE NEW: {pattern} (fs @0x{address:X})");
                }

                long now = Environment.TickCount64;
                if (now - _lastSummaryMs >= 3000)
                {
                    _lastSummaryMs = now;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-HASHPROBE (~3s): cutoutShaders={_shadersWithKil} discards={_totalDiscards} " +
                        $"matched={_totalMatched} ({(100.0 * _totalMatched / Math.Max(1, _totalDiscards)):F0}%) " +
                        $"v1Direct={_chainDirect} v2Indirect={_chainIndirect} " +
                        $"(direct={(100.0 * _chainDirect / Math.Max(1, _chainDirect + _chainIndirect)):F0}%) " +
                        $"distinctShapes={_patterns.Count}");
                }
            }
        }

        private static string MatchDiscard(BasicBlock block, out bool ok)
        {
            ok = false;

            if (block.Predecessors.Count != 1)
            {
                return $"preds={block.Predecessors.Count}";
            }

            BasicBlock pred = block.Predecessors[0];

            if (pred.GetLastOp() is not Operation branch ||
                (branch.Inst != Instruction.BranchIfTrue && branch.Inst != Instruction.BranchIfFalse))
            {
                return "uncond-entry";
            }

            bool discardOnTaken = pred.Branch == block;
            bool discardWhenCondTrue = discardOnTaken == (branch.Inst == Instruction.BranchIfTrue);

            Operand cond = branch.GetSource(0);

            if (cond == null || cond.AsgOp is not Operation cmp)
            {
                return cond?.AsgOp is PhiNode ? "cond=phi" : "cond=no-def";
            }

            Instruction baseInst = cmp.Inst & Instruction.Mask;
            bool isFloat = (cmp.Inst & Instruction.FP32) != 0;

            bool isCompare = baseInst == Instruction.CompareLess ||
                             baseInst == Instruction.CompareLessOrEqual ||
                             baseInst == Instruction.CompareGreater ||
                             baseInst == Instruction.CompareGreaterOrEqual ||
                             baseInst == Instruction.CompareEqual ||
                             baseInst == Instruction.CompareNotEqual;

            if (!isCompare || !isFloat || cmp.SourcesCount < 2)
            {
                return $"cond={baseInst}{(isFloat ? "f" : "")}";
            }

            Operand srcA = cmp.GetSource(0);
            Operand srcB = cmp.GetSource(1);

            (string thr, bool thrOk) = ClassifyThreshold(srcB);
            (string alpha, bool alphaOk) = TraceToTexture(srcA, out string chain);

            if (!(thrOk && alphaOk))
            {
                (string thr2, bool thrOk2) = ClassifyThreshold(srcA);
                (string alpha2, bool alphaOk2) = TraceToTexture(srcB, out string chain2);

                if (thrOk2 && alphaOk2)
                {
                    (thr, alpha, chain) = (thr2, alpha2, chain2);
                    (thrOk, alphaOk) = (thrOk2, alphaOk2);
                }
            }

            ok = thrOk && alphaOk;

            if (ok)
            {
                if (chain == "direct")
                {
                    System.Threading.Interlocked.Increment(ref _chainDirect);
                }
                else
                {
                    System.Threading.Interlocked.Increment(ref _chainIndirect);
                }
            }

            return $"{baseInst}(when={(discardWhenCondTrue ? "T" : "F")}) thr={thr} a={alpha} chain={chain}";
        }

        private static (string, bool) ClassifyThreshold(Operand oper)
        {
            if (oper == null)
            {
                return ("null", false);
            }

            if (oper.Type == OperandType.Constant)
            {
                float v = BitConverter.Int32BitsToSingle(oper.Value);

                if (v >= 0f && v <= 1f)
                {
                    return ($"const({v:0.###})", true);
                }

                return ($"constOut({v:0.###})", false);
            }

            if (oper.Type == OperandType.ConstantBuffer)
            {
                return ("cbuf", true);
            }

            if (oper.Type == OperandType.LocalVariable &&
                oper.AsgOp is Operation def &&
                def.Inst == Instruction.Load &&
                def.StorageKind == StorageKind.ConstantBuffer)
            {
                return ("cbufLoad", true);
            }

            return ("var", false);
        }

        private static (string, bool) TraceToTexture(Operand start)
        {
            return TraceToTexture(start, out _);
        }

        private static (string, bool) TraceToTexture(Operand start, out string chain)
        {
            chain = "none";

            if (start == null)
            {
                return ("null", false);
            }

            Stack<(Operand Oper, string Path)> stack = new();
            HashSet<INode> visited = new();
            stack.Push((start, ""));

            int steps = 0;

            while (stack.Count > 0 && steps++ < MaxTraceSteps)
            {
                (Operand oper, string path) = stack.Pop();

                if (oper == null || oper.Type != OperandType.LocalVariable || oper.AsgOp == null || !visited.Add(oper.AsgOp))
                {
                    continue;
                }

                if (oper.AsgOp is PhiNode phi)
                {
                    string phiPath = Extend(path, "phi");

                    for (int i = 0; i < phi.SourcesCount; i++)
                    {
                        stack.Push((phi.GetSource(i), phiPath));
                    }

                    continue;
                }

                if (oper.AsgOp is not Operation def)
                {
                    continue;
                }

                if (def.Inst == Instruction.TextureSample)
                {
                    chain = path.Length == 0 ? "direct" : path;

                    return ($"tex.{def.Index}", true);
                }

                string extended = Extend(path, (def.Inst & Instruction.Mask).ToString());

                for (int i = 0; i < def.SourcesCount; i++)
                {
                    stack.Push((def.GetSource(i), extended));
                }
            }

            return (steps >= MaxTraceSteps ? "deep" : "noTex", false);

            static string Extend(string path, string op)
            {
                if (path.Length > 60)
                {
                    return path.EndsWith(">...") ? path : path + ">...";
                }

                return path.Length == 0 ? op : path + ">" + op;
            }
        }

        private static int _rwShaders;
        private static int _rwRewritten;
        private static int _rwSkipShape;
        private static int _rwSkipOrder;
        private static int _rwSkipMultiUse;
        private static int _rwSkipTiny;
        private static int _rwSkipNode;
        private static long _rwLastSummaryMs;

        internal static void Rewrite(IReadOnlyList<Function> funcs, ShaderStage stage, ulong address)
        {
            if ((!RewriteEnabled && !LodBiasEnabled && !CaptureEnabled) || stage != ShaderStage.Fragment)
            {
                return;
            }

            try
            {
                RewriteImpl(funcs, address);
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"MVPP-HASHED: rewrite failed for fs @0x{address:X}: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void RewriteImpl(IReadOnlyList<Function> funcs, ulong address)
        {
            int rewritten = 0;
            int skipped = 0;
            int captured = 0;
            HashSet<Operation> doneCmps = new();

            foreach (Function func in funcs)
            {
                Dictionary<Operation, LinkedListNode<INode>> nodeIndex = null;

                foreach (BasicBlock block in func.Blocks)
                {
                    foreach (INode node in block.Operations)
                    {
                        if (node is not Operation op || op.Inst != Instruction.Discard)
                        {
                            continue;
                        }

                        bool forCapture = CaptureEnabled && !RewriteEnabled && !LodBiasEnabled;

                        if (!TryGetRewritableCompare(block, out Operation cmp, out string skipReason, forCapture))
                        {
                            skipped++;
                            CountSkip(skipReason);

                            continue;
                        }

                        if (!doneCmps.Add(cmp))
                        {
                            continue;
                        }

                        if (nodeIndex == null)
                        {
                            nodeIndex = new Dictionary<Operation, LinkedListNode<INode>>();

                            foreach (BasicBlock b in func.Blocks)
                            {
                                for (LinkedListNode<INode> n = b.Operations.First; n != null; n = n.Next)
                                {
                                    if (n.Value is Operation o)
                                    {
                                        nodeIndex[o] = n;
                                    }
                                }
                            }
                        }

                        if (!nodeIndex.TryGetValue(cmp, out LinkedListNode<INode> cmpNode))
                        {
                            skipped++;
                            _rwSkipNode++;

                            continue;
                        }

                        if (CaptureEnabled)
                        {
                            InsertAlphaCapture(cmpNode, cmp);
                            captured++;
                        }

                        if (RewriteEnabled)
                        {
                            InsertHashThreshold(cmpNode, cmp);
                            rewritten++;
                        }

                        if (LodBiasEnabled)
                        {
                            TryApplyLodBias(cmp, nodeIndex);
                        }
                    }
                }
            }

            if (rewritten == 0 && skipped == 0 && captured == 0)
            {
                return;
            }

            lock (_sync)
            {
                _rwShaders++;
                _rwRewritten += rewritten;
                _capInserted += captured;

                long now = Environment.TickCount64;
                if (now - _rwLastSummaryMs >= 3000)
                {
                    _rwLastSummaryMs = now;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-HASHED (~3s): cutoutShaders={_rwShaders} rewritten={_rwRewritten} band={HashBand:0.##} " +
                        $"skips: shape={_rwSkipShape} order={_rwSkipOrder} multiUse={_rwSkipMultiUse} tinyThr={_rwSkipTiny} node={_rwSkipNode}" +
                        (LodBiasEnabled
                            ? $" | lodBias={AlphaLodBias:0.##}: dupDirect={_lbDirect} inPlaceAlphaOnly={_lbInPlace} skipped={_lbSkipped}"
                            : "") +
                        (CaptureEnabled ? $" | alphaKilCaptures={_capInserted}" : ""));
                }
            }
        }

        private static void CountSkip(string reason)
        {
            lock (_sync)
            {
                switch (reason)
                {
                    case "order":
                        _rwSkipOrder++;
                        break;
                    case "multiUse":
                        _rwSkipMultiUse++;
                        break;
                    case "tinyThr":
                        _rwSkipTiny++;
                        break;
                    default:
                        _rwSkipShape++;
                        break;
                }
            }
        }

        private static bool TryGetRewritableCompare(BasicBlock block, out Operation cmp, out string skipReason, bool forCapture = false)
        {
            cmp = null;
            skipReason = "shape";

            if (block.Predecessors.Count != 1)
            {
                return false;
            }

            BasicBlock pred = block.Predecessors[0];

            if (pred.GetLastOp() is not Operation branch ||
                (branch.Inst != Instruction.BranchIfTrue && branch.Inst != Instruction.BranchIfFalse))
            {
                return false;
            }

            bool discardOnTaken = pred.Branch == block;
            bool discardWhenCondTrue = discardOnTaken == (branch.Inst == Instruction.BranchIfTrue);

            if (!discardWhenCondTrue)
            {
                return false;
            }

            Operand cond = branch.GetSource(0);

            if (cond == null || cond.AsgOp is not Operation candidate)
            {
                return false;
            }

            if (!forCapture && cond.UseOps.Count > 1)
            {
                skipReason = "multiUse";

                return false;
            }

            Instruction baseInst = candidate.Inst & Instruction.Mask;

            if ((candidate.Inst & Instruction.FP32) == 0 ||
                (baseInst != Instruction.CompareLess && baseInst != Instruction.CompareLessOrEqual) ||
                candidate.SourcesCount < 2)
            {
                return false;
            }

            Operand thrOper = candidate.GetSource(1);
            (_, bool thrOk) = ClassifyThreshold(thrOper);

            if (!thrOk)
            {
                (_, bool thrWrongSide) = ClassifyThreshold(candidate.GetSource(0));
                skipReason = thrWrongSide ? "order" : "shape";

                return false;
            }

            if (!forCapture && thrOper.Type == OperandType.Constant &&
                BitConverter.Int32BitsToSingle(thrOper.Value) < MinCutoutThreshold)
            {
                skipReason = "tinyThr";

                return false;
            }

            (_, bool alphaOk) = TraceToTexture(candidate.GetSource(0));

            if (!alphaOk)
            {
                return false;
            }

            cmp = candidate;
            skipReason = null;

            return true;
        }

        private static void InsertHashThreshold(LinkedListNode<INode> cmpNode, Operation cmp)
        {
            LinkedList<INode> list = cmpNode.List;

            Operand Emit(Instruction inst, StorageKind storageKind, params Operand[] sources)
            {
                Operand dest = Local();
                list.AddBefore(cmpNode, new Operation(inst, storageKind, dest, sources));

                return dest;
            }

            Operand EmitOp(Instruction inst, params Operand[] sources)
            {
                Operand dest = Local();
                list.AddBefore(cmpNode, new Operation(inst, dest, sources));

                return dest;
            }

            Operand fx = Emit(Instruction.Load, StorageKind.Input, Const((int)IoVariable.FragmentCoord), Const(0));
            Operand fy = Emit(Instruction.Load, StorageKind.Input, Const((int)IoVariable.FragmentCoord), Const(1));
            Operand ux = EmitOp(Instruction.ConvertFP32ToU32, fx);
            Operand uy = EmitOp(Instruction.ConvertFP32ToU32, fy);

            Operand uyG = EmitOp(Instruction.Multiply, uy, Const(unchecked((int)0x9E3779B9)));
            Operand seed = EmitOp(Instruction.BitwiseExclusiveOr, ux, uyG);

            Operand a = EmitOp(Instruction.BitwiseExclusiveOr, seed, Const(61));
            Operand b = EmitOp(Instruction.ShiftRightU32, seed, Const(16));
            Operand h1 = EmitOp(Instruction.BitwiseExclusiveOr, a, b);
            Operand h2 = EmitOp(Instruction.Multiply, h1, Const(9));
            Operand c = EmitOp(Instruction.ShiftRightU32, h2, Const(4));
            Operand h3 = EmitOp(Instruction.BitwiseExclusiveOr, h2, c);
            Operand h4 = EmitOp(Instruction.Multiply, h3, Const(unchecked((int)0x27d4eb2d)));
            Operand d = EmitOp(Instruction.ShiftRightU32, h4, Const(15));
            Operand h5 = EmitOp(Instruction.BitwiseExclusiveOr, h4, d);

            Operand hTop = EmitOp(Instruction.ShiftRightU32, h5, Const(8));
            Operand hf = EmitOp(Instruction.ConvertU32ToFP32, hTop);
            Operand hash01 = EmitOp(Instruction.FP32 | Instruction.Multiply, hf, ConstF(1f / 16777216f));

            Operand originalThr = cmp.GetSource(1);
            Operand centered = EmitOp(Instruction.FP32 | Instruction.Add, hash01, ConstF(-0.5f));
            Operand scaled = EmitOp(Instruction.FP32 | Instruction.Multiply, centered, ConstF(HashBand));
            Operand banded = EmitOp(Instruction.FP32 | Instruction.Add, originalThr, scaled);

            cmp.SetSource(1, banded);
        }

        private static int _capInserted;

        private static void InsertAlphaCapture(LinkedListNode<INode> cmpNode, Operation cmp)
        {
            LinkedList<INode> list = cmpNode.List;

            Operand Emit(Instruction inst, StorageKind storageKind, params Operand[] sources)
            {
                Operand dest = Local();
                list.AddBefore(cmpNode, new Operation(inst, storageKind, dest, sources));

                return dest;
            }

            Operand EmitOp(Instruction inst, params Operand[] sources)
            {
                Operand dest = Local();
                list.AddBefore(cmpNode, new Operation(inst, dest, sources));

                return dest;
            }

            Operand Clamp01(Operand v)
            {
                return EmitOp(Instruction.FP32 | Instruction.Minimum,
                    EmitOp(Instruction.FP32 | Instruction.Maximum, v, ConstF(0f)), ConstF(1f));
            }

            Operand fx = Emit(Instruction.Load, StorageKind.Input, Const((int)IoVariable.FragmentCoord), Const(0));
            Operand fy = Emit(Instruction.Load, StorageKind.Input, Const((int)IoVariable.FragmentCoord), Const(1));
            Operand cx = EmitOp(Instruction.MinimumU32,
                EmitOp(Instruction.ConvertFP32ToU32, fx), Const(MvppSurviveProbe.MapStride - 1));
            Operand cy = EmitOp(Instruction.MinimumU32,
                EmitOp(Instruction.ConvertFP32ToU32, fy), Const(MvppSurviveProbe.MapHeight - 1));
            Operand index = EmitOp(Instruction.Add,
                EmitOp(Instruction.Multiply, cy, Const(MvppSurviveProbe.MapStride)), cx);

            Operand alpha = cmp.GetSource(0);
            Operand a8 = EmitOp(Instruction.ConvertFP32ToU32,
                EmitOp(Instruction.FP32 | Instruction.Multiply, Clamp01(alpha), ConstF(255f)));

            Operand delta = EmitOp(Instruction.FP32 | Instruction.Subtract, alpha, cmp.GetSource(1));
            Operand dClamped = EmitOp(Instruction.FP32 | Instruction.Minimum,
                EmitOp(Instruction.FP32 | Instruction.Maximum, delta, ConstF(-1f)), ConstF(1f));
            Operand d8 = EmitOp(Instruction.ConvertFP32ToU32,
                EmitOp(Instruction.FP32 | Instruction.Add,
                    EmitOp(Instruction.FP32 | Instruction.Multiply, dClamped, ConstF(127f)), ConstF(128f)));

            Operand fz = Emit(Instruction.Load, StorageKind.Input, Const((int)IoVariable.FragmentCoord), Const(2));
            Operand z16 = EmitOp(Instruction.ConvertFP32ToU32,
                EmitOp(Instruction.FP32 | Instruction.Multiply, Clamp01(fz), ConstF(65535f)));

            Operand packed = EmitOp(Instruction.BitwiseOr,
                EmitOp(Instruction.BitwiseOr, a8, EmitOp(Instruction.ShiftLeft, d8, Const(8))),
                EmitOp(Instruction.ShiftLeft, z16, Const(16)));

            list.AddBefore(cmpNode, new Operation(Instruction.Store, StorageKind.StorageBuffer, (Operand)null,
                Const(MvppSurviveProbe.HostBinding), Const(0), index, packed));
        }

        private static int _lbDirect;
        private static int _lbInPlace;
        private static int _lbSkipped;
        private static readonly HashSet<Operation> _lbDone = new();

        private const TextureFlags LodBiasExcludedFlags =
            TextureFlags.Bindless | TextureFlags.LodLevel | TextureFlags.LodBias |
            TextureFlags.Gather | TextureFlags.Derivatives | TextureFlags.IntCoords;

        private static void TryApplyLodBias(Operation cmp, Dictionary<Operation, LinkedListNode<INode>> nodeIndex)
        {
            Operand alphaSrc = cmp.GetSource(0);

            if (alphaSrc == null || alphaSrc.Type != OperandType.LocalVariable)
            {
                return;
            }

            if (alphaSrc.AsgOp is TextureOperation directTex &&
                directTex.Inst == Instruction.TextureSample)
            {
                if ((directTex.Flags & LodBiasExcludedFlags) != 0 ||
                    (directTex.Type & (SamplerType.Multisample | SamplerType.Shadow)) != 0 ||
                    directTex.SamplerBinding != -1 ||
                    !nodeIndex.TryGetValue(directTex, out LinkedListNode<INode> texNode))
                {
                    lock (_sync) { _lbSkipped++; }

                    return;
                }

                int destIndex = -1;

                for (int i = 0; i < directTex.DestsCount; i++)
                {
                    if (directTex.GetDest(i) == alphaSrc)
                    {
                        destIndex = i;
                        break;
                    }
                }

                if (destIndex < 0)
                {
                    lock (_sync) { _lbSkipped++; }

                    return;
                }

                int component = NthSetBit(directTex.Index, destIndex);

                if (component < 0)
                {
                    lock (_sync) { _lbSkipped++; }

                    return;
                }

                Operand[] dupSources = new Operand[directTex.SourcesCount + 1];

                for (int i = 0; i < directTex.SourcesCount; i++)
                {
                    dupSources[i] = directTex.GetSource(i);
                }

                dupSources[^1] = ConstF(AlphaLodBias);

                Operand dupDest = Local();

                TextureOperation dup = new(
                    Instruction.TextureSample,
                    directTex.Type,
                    directTex.Format,
                    directTex.Flags | TextureFlags.LodBias,
                    directTex.Set,
                    directTex.Binding,
                    1 << component,
                    [dupDest],
                    dupSources);

                texNode.List.AddAfter(texNode, dup);
                cmp.SetSource(0, dupDest);

                lock (_sync) { _lbDirect++; }

                return;
            }

            if (TraceToTextureOp(alphaSrc) is TextureOperation chainTex &&
                chainTex.Index == 8 &&
                chainTex.DestsCount == 1 &&
                (chainTex.Flags & LodBiasExcludedFlags) == 0 &&
                (chainTex.Type & (SamplerType.Multisample | SamplerType.Shadow)) == 0)
            {
                lock (_sync)
                {
                    if (!_lbDone.Add(chainTex))
                    {
                        return;
                    }

                    _lbInPlace++;
                }

                chainTex.AppendSources([ConstF(AlphaLodBias)]);
                chainTex.SetLodBiasFlag();

                return;
            }

            lock (_sync) { _lbSkipped++; }
        }

        private static int NthSetBit(int mask, int n)
        {
            for (int bit = 0; bit < 32; bit++)
            {
                if ((mask & (1 << bit)) != 0 && n-- == 0)
                {
                    return bit;
                }
            }

            return -1;
        }

        private static TextureOperation TraceToTextureOp(Operand start)
        {
            Stack<Operand> stack = new();
            HashSet<INode> visited = new();
            stack.Push(start);

            int steps = 0;

            while (stack.Count > 0 && steps++ < MaxTraceSteps)
            {
                Operand oper = stack.Pop();

                if (oper == null || oper.Type != OperandType.LocalVariable || oper.AsgOp == null || !visited.Add(oper.AsgOp))
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

                if (oper.AsgOp is not Operation def)
                {
                    continue;
                }

                if (def is TextureOperation texOp && def.Inst == Instruction.TextureSample)
                {
                    return texOp;
                }

                for (int i = 0; i < def.SourcesCount; i++)
                {
                    stack.Push(def.GetSource(i));
                }
            }

            return null;
        }
    }
}
