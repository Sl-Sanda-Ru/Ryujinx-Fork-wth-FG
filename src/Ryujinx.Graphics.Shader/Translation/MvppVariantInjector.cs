
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using System;
using System.Collections.Generic;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppVariantInjector
    {
        public const int MvppN1HostBinding = 91;

        private const int Cb5Slot = 5;

        private static readonly bool _n1Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_N1") == "1";

        private static readonly bool _canaryEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_CANARY") == "1";
        private static bool _canaryLogged;

        internal static void Inject(IReadOnlyList<Function> funcs, AttributeUsage attributeUsage, ResourceManager resourceManager, ShaderStage stage, ulong address)
        {
            if (stage == ShaderStage.Vertex)
            {
                InjectVertex(funcs, attributeUsage, resourceManager, address);
            }
            else if (stage == ShaderStage.Fragment)
            {
                InjectFragment(funcs, attributeUsage, address);
            }
        }

        private static void InjectVertex(IReadOnlyList<Function> funcs, AttributeUsage attributeUsage, ResourceManager resourceManager, ulong address)
        {
            int locN = attributeUsage.MvppClipLocN;
            int locPrev = attributeUsage.MvppClipLocPrev;

            if (locN < 0 || locPrev < 0)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP inject: vs @0x{address:X} SKIPPED (need 2 free user locations in [0,15], or not a VS->FS variant).");
                return;
            }

            int cb5Binding = -1;
            int n1Binding = -1;

            if (_n1Enabled && resourceManager != null &&
                resourceManager.IsConstantBufferSlotUsed(Cb5Slot))
            {
                cb5Binding = resourceManager.GetConstantBufferBinding(Cb5Slot);
                n1Binding = MvppN1HostBinding;
                resourceManager.DeclareMvppN1ConstantBuffer(n1Binding);
            }

            List<(LinkedListNode<INode> StoreNode, List<Operation> NewOps)> pending = new();
            int shared = 0;
            bool bail = false;

            foreach (Function func in funcs)
            {
                if (bail)
                {
                    break;
                }

                foreach (BasicBlock block in func.Blocks)
                {
                    if (bail)
                    {
                        break;
                    }

                    List<LinkedListNode<INode>> posStores = null;

                    for (LinkedListNode<INode> n = block.Operations.First; n != null; n = n.Next)
                    {
                        if (n.Value is Operation op &&
                            op.Inst == Instruction.Store &&
                            op.StorageKind == StorageKind.Output &&
                            op.SourcesCount >= 3 &&
                            op.GetSource(0).Type == OperandType.Constant &&
                            op.GetSource(0).Value == (int)IoVariable.Position)
                        {
                            (posStores ??= new List<LinkedListNode<INode>>()).Add(n);
                        }
                    }

                    if (posStores == null)
                    {
                        continue;
                    }

                    foreach (LinkedListNode<INode> storeNode in posStores)
                    {
                        Operation store = (Operation)storeNode.Value;
                        Operand component = store.GetSource(store.SourcesCount - 2);
                        Operand value = store.GetSource(store.SourcesCount - 1);

                        Dictionary<Operand, Operand> map = new();
                        List<Operation> newOps = new();

                        Operand clone = CloneCone(value, map, newOps, cb5Binding, n1Binding, ref shared, ref bail);

                        if (bail)
                        {
                            break;
                        }

                        newOps.Add(new Operation(
                            Instruction.Store,
                            StorageKind.Output,
                            null,
                            Const((int)IoVariable.UserDefined),
                            Const(locN),
                            component,
                            value));

                        newOps.Add(new Operation(
                            Instruction.Store,
                            StorageKind.Output,
                            null,
                            Const((int)IoVariable.UserDefined),
                            Const(locPrev),
                            component,
                            clone));

                        pending.Add((storeNode, newOps));
                    }
                }
            }

            if (bail)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP inject: vs @0x{address:X} SKIPPED (unclonable op -- e.g. texture -- in position slice); variant stays byte-identical.");
                return;
            }

            if (pending.Count == 0)
            {
                return;
            }

            foreach ((LinkedListNode<INode> storeNode, List<Operation> newOps) in pending)
            {
                foreach (Operation nop in newOps)
                {
                    storeNode.List.AddBefore(storeNode, nop);
                }
            }

            attributeUsage.SetOutputUserAttribute(locN);
            attributeUsage.SetOutputUserAttribute(locPrev);

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP inject VS @0x{address:X}: clip_N loc {locN} + clip_(N-1) loc {locPrev} REGISTERED " +
                $"(both in [0,15]; {pending.Count} position stores, {shared} shared leaves).");

            if (n1Binding >= 0)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP N-1: vs @0x{address:X} clip_(N-1) cb5 reads REDIRECTED to RESERVED host binding {n1Binding} (cb5 binding {cb5Binding}).");
            }
        }

        private static void InjectFragment(IReadOnlyList<Function> funcs, AttributeUsage attributeUsage, ulong address)
        {
            int locN = attributeUsage.MvppClipLocN;
            int locPrev = attributeUsage.MvppClipLocPrev;

            if (locN < 0 || locPrev < 0)
            {
                return;
            }

            if (_canaryEnabled)
            {
                foreach (BasicBlock block in funcs[0].Blocks)
                {
                    Operation c0 = new(Instruction.Store, StorageKind.Output, null,
                        Const((int)IoVariable.FragmentOutputColor), Const(0), Const(0), ConstF(-0.5f));
                    Operation c1 = new(Instruction.Store, StorageKind.Output, null,
                        Const((int)IoVariable.FragmentOutputColor), Const(0), Const(1), ConstF(0.866f));

                    LinkedListNode<INode> first = block.Operations.First;
                    if (first != null)
                    {
                        block.Operations.AddBefore(first, c0);
                        block.Operations.AddBefore(first, c1);
                    }
                    else
                    {
                        block.Operations.AddFirst(c0);
                        block.Operations.AddLast(c1);
                    }

                    break;
                }

                if (!_canaryLogged)
                {
                    _canaryLogged = true;
                    Logger.Warning?.Print(LogClass.Gpu,
                        "MVPP CANARY: ACTIVE -- unconditional FS-entry RT0.xy write (magenta) on variant FS.");
                }
            }

            LinkedListNode<INode> anchor = null;

            foreach (Function func in funcs)
            {
                foreach (BasicBlock block in func.Blocks)
                {
                    for (LinkedListNode<INode> n = block.Operations.First; n != null; n = n.Next)
                    {
                        if (n.Value is Operation op &&
                            op.Inst == Instruction.Store &&
                            op.StorageKind == StorageKind.Output &&
                            op.SourcesCount >= 1 &&
                            op.GetSource(0).Type == OperandType.Constant &&
                            op.GetSource(0).Value == (int)IoVariable.FragmentOutputColor)
                        {
                            anchor = n;
                        }
                    }
                }
            }

            if (anchor == null)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP inject FS @0x{address:X}: no fragment color store -- MV write skipped (loc {locN}/{locPrev}).");
                return;
            }

            foreach (int c in new[] { 0, 1, 3 })
            {
                attributeUsage.SetInputUserAttribute(locN, c);
                attributeUsage.SetInputUserAttribute(locPrev, c);
            }

            Operand LoadIn(int loc, int comp)
            {
                Operand dst = Local();
                anchor = anchor.List.AddAfter(anchor, new Operation(
                    Instruction.Load,
                    StorageKind.Input,
                    dst,
                    Const((int)IoVariable.UserDefined),
                    Const(loc),
                    Const(comp)));
                return dst;
            }

            Operand Fp(Instruction inst, Operand a, Operand b)
            {
                Operand dst = Local();
                anchor = anchor.List.AddAfter(anchor, new Operation(inst, dst, a, b));
                return dst;
            }

            void StoreMv(int comp, Operand val)
            {
                anchor = anchor.List.AddAfter(anchor, new Operation(
                    Instruction.Store,
                    StorageKind.Output,
                    null,
                    Const((int)IoVariable.FragmentOutputColor),
                    Const(0),
                    Const(comp),
                    val));
            }

            Operand nx = LoadIn(locN, 0);
            Operand ny = LoadIn(locN, 1);
            Operand nw = LoadIn(locN, 3);
            Operand px = LoadIn(locPrev, 0);
            Operand py = LoadIn(locPrev, 1);
            Operand pw = LoadIn(locPrev, 3);

            Operand ndcNx = Fp(Instruction.FP32 | Instruction.Divide, nx, nw);
            Operand ndcNy = Fp(Instruction.FP32 | Instruction.Divide, ny, nw);
            Operand ndcPx = Fp(Instruction.FP32 | Instruction.Divide, px, pw);
            Operand ndcPy = Fp(Instruction.FP32 | Instruction.Divide, py, pw);

            Operand mvx = Fp(Instruction.FP32 | Instruction.Subtract, ndcNx, ndcPx);
            Operand mvy = Fp(Instruction.FP32 | Instruction.Subtract, ndcNy, ndcPy);

            Operand Guard(Operand v)
            {
                Operand nan = Fp(Instruction.FP32 | Instruction.CompareNotEqual, v, v);
                Operand hi = Fp(Instruction.FP32 | Instruction.CompareGreater, v, ConstF(1e30f));
                Operand lo = Fp(Instruction.FP32 | Instruction.CompareLess, v, ConstF(-1e30f));
                Operand bad = Fp(Instruction.LogicalOr, nan, Fp(Instruction.LogicalOr, hi, lo));

                Operand safe = Local();
                anchor = anchor.List.AddAfter(anchor, new Operation(
                    Instruction.ConditionalSelect, safe, bad, ConstF(0f), v));
                return safe;
            }

            StoreMv(0, Guard(mvx));
            StoreMv(1, Guard(mvy));

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP inject FS @0x{address:X}: reads clip_N loc {locN} + clip_(N-1) loc {locPrev} " +
                $"(== VS output locs -> LOC MATCH), writes MV=ndc(N)-ndc(N-1) to RT0 -- varyings SURVIVE.");
        }

        private static Operand CloneCone(Operand o, Dictionary<Operand, Operand> map, List<Operation> newOps, int cb5Binding, int n1Binding, ref int shared, ref bool bail)
        {
            if (bail || o == null || o.Type != OperandType.LocalVariable || o.AsgOp == null)
            {
                return o;
            }

            if (map.TryGetValue(o, out Operand existing))
            {
                return existing;
            }

            if (o.AsgOp is not Operation defOp)
            {
                shared++;
                return o;
            }

            if (defOp.GetType() != typeof(Operation))
            {
                bail = true;
                return o;
            }

            Operand[] newSources = new Operand[defOp.SourcesCount];
            for (int i = 0; i < defOp.SourcesCount; i++)
            {
                newSources[i] = CloneCone(defOp.GetSource(i), map, newOps, cb5Binding, n1Binding, ref shared, ref bail);
            }

            if (bail)
            {
                return o;
            }

            if (n1Binding >= 0 &&
                defOp.Inst == Instruction.Load &&
                defOp.StorageKind == StorageKind.ConstantBuffer &&
                newSources.Length >= 1 &&
                newSources[0].Type == OperandType.Constant &&
                newSources[0].Value == cb5Binding)
            {
                newSources[0] = Const(n1Binding);
            }

            Operand newDest = Local();
            Operation newOp = new(defOp.Inst, defOp.StorageKind, newDest, newSources);

            map[o] = newDest;
            newOps.Add(newOp);

            return newDest;
        }
    }
}
