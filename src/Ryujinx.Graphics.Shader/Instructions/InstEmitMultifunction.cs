using System.Collections.Generic;
using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;

using static Ryujinx.Graphics.Shader.Instructions.InstEmitHelper;

namespace Ryujinx.Graphics.Shader.Instructions
{
    static partial class InstEmit
    {
        public static void RroR(EmitterContext context)
        {
            InstRroR op = context.GetOp<InstRroR>();

            EmitRro(context, GetSrcReg(context, op.SrcB), op.Dest, op.AbsB, op.NegB);
        }

        public static void RroI(EmitterContext context)
        {
            InstRroI op = context.GetOp<InstRroI>();

            EmitRro(context, GetSrcImm(context, Imm20ToFloat(op.Imm20)), op.Dest, op.AbsB, op.NegB);
        }

        public static void RroC(EmitterContext context)
        {
            InstRroC op = context.GetOp<InstRroC>();

            EmitRro(context, GetSrcCbuf(context, op.CbufSlot, op.CbufOffset), op.Dest, op.AbsB, op.NegB);
        }

        public static void Mufu(EmitterContext context)
        {
            InstMufu op = context.GetOp<InstMufu>();

            Operand res = context.FPAbsNeg(GetSrcReg(context, op.SrcA), op.AbsA, op.NegA);

            switch (op.MufuOp)
            {
                case MufuOp.Cos:
                    res = context.FPCosine(ReduceSinCosArg(context, res));
                    break;

                case MufuOp.Sin:
                    res = context.FPSine(ReduceSinCosArg(context, res));
                    break;

                case MufuOp.Ex2:
                    res = context.FPExponentB2(res);
                    break;

                case MufuOp.Lg2:
                    res = context.FPLogarithmB2(res);
                    break;

                case MufuOp.Rcp:
                    res = ReduceMufuPrecision(context, context.FPReciprocal(res), MufuOp.Rcp);
                    break;

                case MufuOp.Rsq:
                    res = ReduceMufuPrecision(context, context.FPReciprocalSquareRoot(res), MufuOp.Rsq);
                    break;

                case MufuOp.Rcp64h:
                    res = context.PackDouble2x32(OperandHelper.Const(0), res);
                    res = context.UnpackDouble2x32High(context.FPReciprocal(res, Instruction.FP64));
                    break;

                case MufuOp.Rsq64h:
                    res = context.PackDouble2x32(OperandHelper.Const(0), res);
                    res = context.UnpackDouble2x32High(context.FPReciprocalSquareRoot(res, Instruction.FP64));
                    break;

                case MufuOp.Sqrt:
                    res = ReduceMufuPrecision(context, context.FPSquareRoot(res), MufuOp.Sqrt);
                    break;

                default:
                    context.TranslatorContext.GpuAccessor.Log($"Invalid MUFU operation \"{op.MufuOp}\".");
                    break;
            }

            context.Copy(GetDest(op.Dest), context.FPSaturate(res, op.Sat));
        }

        private static void EmitRro(EmitterContext context, Operand srcB, int rd, bool absB, bool negB)
        {
            // This is the range reduction operator,
            // we translate it as a simple move, as it
            // should be always followed by a matching
            // MUFU instruction.
            srcB = context.FPAbsNeg(srcB, absB, negB);

            context.Copy(GetDest(rd), srcB);
        }

        private static bool _rroReduceLogged;

        private static Operand ReduceSinCosArg(EmitterContext context, Operand x)
        {
            if (!RroReduceProbe.Enabled)
            {
                return x;
            }

            if (!_rroReduceLogged)
            {
                _rroReduceLogged = true;
                context.TranslatorContext.GpuAccessor.Log("RROREDUCE: sin/cos argument range reduction ACTIVE (RYUJINX_RRO_REDUCE=1).");
            }

            Operand k = context.FPFloor(context.FPMultiply(x, OperandHelper.ConstF(0.15915494309f)));
            return context.FPSubtract(x, context.FPMultiply(k, OperandHelper.ConstF(6.28318530718f)));
        }

        private const ulong MufuPrecFsAddress = 0x1000AE430UL;

        private static readonly HashSet<MufuOp> _mufuPrecLoggedOps = new();

        private static Operand ReduceMufuPrecision(EmitterContext context, Operand res, MufuOp mufuOp)
        {
            ulong target = MufuPrecProbe.AddressOverride ?? MufuPrecFsAddress;

            if (!MufuPrecProbe.Enabled || context.TranslatorContext.Address != target)
            {
                return res;
            }

            int bits = MufuPrecProbe.BitsKept.Value;
            int dropped = MufuPrecProbe.MantissaBits - bits;

            if (dropped <= 0)
            {
                return res;
            }

            lock (_mufuPrecLoggedOps)
            {
                if (_mufuPrecLoggedOps.Add(mufuOp))
                {
                    context.TranslatorContext.GpuAccessor.Log(
                        $"[MUFU_PREC] Guest FS: 0x{MufuPrecFsAddress:X} | Op: {mufuOp} | " +
                        $"Mantissa bits kept: {bits}/{MufuPrecProbe.MantissaBits} | Dropped: {dropped}");
                }
            }

            return context.BitwiseAnd(res, OperandHelper.Const(unchecked((int)(0xFFFFFFFFu << dropped))));
        }
    }
}
