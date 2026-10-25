using System;
using System.Globalization;
using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.Instructions.InstEmitAluHelper;
using static Ryujinx.Graphics.Shader.Instructions.InstEmitHelper;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Instructions
{
    static partial class InstEmit
    {
        public static void DaddR(EmitterContext context)
        {
            InstDaddR op = context.GetOp<InstDaddR>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcReg(context, op.SrcB, isFP64: true);

            EmitFadd(context, Instruction.FP64, srcA, srcB, op.Dest, op.NegA, op.NegB, op.AbsA, op.AbsB, false, op.WriteCC);
        }

        public static void DaddI(EmitterContext context)
        {
            InstDaddI op = context.GetOp<InstDaddI>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcImm(context, Imm20ToFloat(op.Imm20), isFP64: true);

            EmitFadd(context, Instruction.FP64, srcA, srcB, op.Dest, op.NegA, op.NegB, op.AbsA, op.AbsB, false, op.WriteCC);
        }

        public static void DaddC(EmitterContext context)
        {
            InstDaddC op = context.GetOp<InstDaddC>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcCbuf(context, op.CbufSlot, op.CbufOffset, isFP64: true);

            EmitFadd(context, Instruction.FP64, srcA, srcB, op.Dest, op.NegA, op.NegB, op.AbsA, op.AbsB, false, op.WriteCC);
        }

        public static void DfmaR(EmitterContext context)
        {
            InstDfmaR op = context.GetOp<InstDfmaR>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcReg(context, op.SrcB, isFP64: true);
            Operand srcC = GetSrcReg(context, op.SrcC, isFP64: true);

            EmitFfma(context, Instruction.FP64, srcA, srcB, srcC, op.Dest, op.NegA, op.NegC, false, op.WriteCC);
        }

        public static void DfmaI(EmitterContext context)
        {
            InstDfmaI op = context.GetOp<InstDfmaI>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcImm(context, Imm20ToFloat(op.Imm20), isFP64: true);
            Operand srcC = GetSrcReg(context, op.SrcC, isFP64: true);

            EmitFfma(context, Instruction.FP64, srcA, srcB, srcC, op.Dest, op.NegA, op.NegC, false, op.WriteCC);
        }

        public static void DfmaC(EmitterContext context)
        {
            InstDfmaC op = context.GetOp<InstDfmaC>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcCbuf(context, op.CbufSlot, op.CbufOffset, isFP64: true);
            Operand srcC = GetSrcReg(context, op.SrcC, isFP64: true);

            EmitFfma(context, Instruction.FP64, srcA, srcB, srcC, op.Dest, op.NegA, op.NegC, false, op.WriteCC);
        }

        public static void DfmaRc(EmitterContext context)
        {
            InstDfmaRc op = context.GetOp<InstDfmaRc>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcReg(context, op.SrcC, isFP64: true);
            Operand srcC = GetSrcCbuf(context, op.CbufSlot, op.CbufOffset, isFP64: true);

            EmitFfma(context, Instruction.FP64, srcA, srcB, srcC, op.Dest, op.NegA, op.NegC, false, op.WriteCC);
        }

        public static void DmulR(EmitterContext context)
        {
            InstDmulR op = context.GetOp<InstDmulR>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcReg(context, op.SrcB, isFP64: true);

            EmitFmul(context, Instruction.FP64, MultiplyScale.NoScale, srcA, srcB, op.Dest, op.NegA, false, op.WriteCC);
        }

        public static void DmulI(EmitterContext context)
        {
            InstDmulI op = context.GetOp<InstDmulI>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcImm(context, Imm20ToFloat(op.Imm20), isFP64: true);

            EmitFmul(context, Instruction.FP64, MultiplyScale.NoScale, srcA, srcB, op.Dest, op.NegA, false, op.WriteCC);
        }

        public static void DmulC(EmitterContext context)
        {
            InstDmulC op = context.GetOp<InstDmulC>();

            Operand srcA = GetSrcReg(context, op.SrcA, isFP64: true);
            Operand srcB = GetSrcCbuf(context, op.CbufSlot, op.CbufOffset, isFP64: true);

            EmitFmul(context, Instruction.FP64, MultiplyScale.NoScale, srcA, srcB, op.Dest, op.NegA, false, op.WriteCC);
        }

        public static void FaddR(EmitterContext context)
        {
            InstFaddR op = context.GetOp<InstFaddR>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcReg(context, op.SrcB);

            EmitFadd(context, Instruction.FP32, srcA, srcB, op.Dest, op.NegA, op.NegB, op.AbsA, op.AbsB, op.Sat, op.WriteCC);
        }

        public static void FaddI(EmitterContext context)
        {
            InstFaddI op = context.GetOp<InstFaddI>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcImm(context, Imm20ToFloat(op.Imm20));

            EmitFadd(context, Instruction.FP32, srcA, srcB, op.Dest, op.NegA, op.NegB, op.AbsA, op.AbsB, op.Sat, op.WriteCC);
        }

        public static void FaddC(EmitterContext context)
        {
            InstFaddC op = context.GetOp<InstFaddC>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcCbuf(context, op.CbufSlot, op.CbufOffset);

            EmitFadd(context, Instruction.FP32, srcA, srcB, op.Dest, op.NegA, op.NegB, op.AbsA, op.AbsB, op.Sat, op.WriteCC);
        }

        private const float NanScrubMagicImmediate = 0.00999999978f;

        public static void Fadd32i(EmitterContext context)
        {
            InstFadd32i op = context.GetOp<InstFadd32i>();

            if (NanScrubProbe.Enabled &&
                !context.TranslatorContext.MvppNanScrubArmed &&
                context.TranslatorContext.Definitions.Stage == ShaderStage.Fragment &&
                Math.Abs(BitConverter.Int32BitsToSingle(op.Imm32) - NanScrubMagicImmediate) < 1e-6f)
            {
                context.TranslatorContext.MvppNanScrubArmed = true;
                NanScrubProbe.OnArmed(context.TranslatorContext.Address, context.TranslatorContext.GpuAccessor);
            }

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcImm(context, op.Imm32);

            EmitFadd(context, Instruction.FP32, srcA, srcB, op.Dest, op.NegA, op.NegB, op.AbsA, op.AbsB, false, op.WriteCC);
        }

        public static void FfmaR(EmitterContext context)
        {
            InstFfmaR op = context.GetOp<InstFfmaR>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcReg(context, op.SrcB);
            Operand srcC = GetSrcReg(context, op.SrcC);

            EmitFfma(context, Instruction.FP32, srcA, srcB, srcC, op.Dest, op.NegA, op.NegC, op.Sat, op.WriteCC);
        }

        public static void FfmaI(EmitterContext context)
        {
            InstFfmaI op = context.GetOp<InstFfmaI>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcImm(context, Imm20ToFloat(op.Imm20));
            Operand srcC = GetSrcReg(context, op.SrcC);

            EmitFfma(context, Instruction.FP32, srcA, srcB, srcC, op.Dest, op.NegA, op.NegC, op.Sat, op.WriteCC);
        }

        public static void FfmaC(EmitterContext context)
        {
            InstFfmaC op = context.GetOp<InstFfmaC>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcCbuf(context, op.CbufSlot, op.CbufOffset);
            Operand srcC = GetSrcReg(context, op.SrcC);

            EmitFfma(context, Instruction.FP32, srcA, srcB, srcC, op.Dest, op.NegA, op.NegC, op.Sat, op.WriteCC);
        }

        public static void FfmaRc(EmitterContext context)
        {
            InstFfmaRc op = context.GetOp<InstFfmaRc>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcReg(context, op.SrcC);
            Operand srcC = GetSrcCbuf(context, op.CbufSlot, op.CbufOffset);

            EmitFfma(context, Instruction.FP32, srcA, srcB, srcC, op.Dest, op.NegA, op.NegC, op.Sat, op.WriteCC);
        }

        public static void Ffma32i(EmitterContext context)
        {
            InstFfma32i op = context.GetOp<InstFfma32i>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcImm(context, op.Imm32);
            Operand srcC = GetSrcReg(context, op.Dest);

            EmitFfma(context, Instruction.FP32, srcA, srcB, srcC, op.Dest, op.NegA, op.NegC, op.Sat, op.WriteCC);
        }

        public static void FmulR(EmitterContext context)
        {
            InstFmulR op = context.GetOp<InstFmulR>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcReg(context, op.SrcB);

            EmitFmul(context, Instruction.FP32, op.Scale, srcA, srcB, op.Dest, op.NegA, op.Sat, op.WriteCC);
        }

        public static void FmulI(EmitterContext context)
        {
            InstFmulI op = context.GetOp<InstFmulI>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcImm(context, Imm20ToFloat(op.Imm20));

            EmitFmul(context, Instruction.FP32, op.Scale, srcA, srcB, op.Dest, op.NegA, op.Sat, op.WriteCC);
        }

        public static void FmulC(EmitterContext context)
        {
            InstFmulC op = context.GetOp<InstFmulC>();

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcCbuf(context, op.CbufSlot, op.CbufOffset);

            EmitFmul(context, Instruction.FP32, op.Scale, srcA, srcB, op.Dest, op.NegA, op.Sat, op.WriteCC);
        }

        private const ulong HashTestFsAddress = 0x1000AE430UL;
        private const float HashMagicImmediate = 43758.5469f;
        private const float HashMagicTolerance = 0.1f;

        private static readonly float? _hashConstValue = HashTestProbe.ConstValue;
        private static bool _hashTestLogged;

        private static bool IsHashMagicImmediate(int imm32)
        {
            return MathF.Abs(BitConverter.Int32BitsToSingle(imm32) - HashMagicImmediate) < HashMagicTolerance;
        }

        private const float SatScrubDecodeImmediate = 3.00999999f;

        public static void Fmul32i(EmitterContext context)
        {
            InstFmul32i op = context.GetOp<InstFmul32i>();

            if (NanScrubProbe.SatScrub &&
                !context.TranslatorContext.MvppSatScrubAllRts &&
                context.TranslatorContext.Definitions.Stage == ShaderStage.Fragment &&
                Math.Abs(BitConverter.Int32BitsToSingle(op.Imm32) - SatScrubDecodeImmediate) < 1e-5f)
            {
                context.TranslatorContext.MvppSatScrubAllRts = true;
                context.TranslatorContext.MvppNanScrubArmed = true;
                context.TranslatorContext.GpuAccessor.Log(
                    $"[SATSCRUB2] armed MV-chain shader (guest 0x{context.TranslatorContext.Address:X}) -- ALL rts scrubbed");
            }

            if (_hashConstValue.HasValue && context.TranslatorContext.Address == HashTestFsAddress)
            {
                bool isHashMultiply = IsHashMagicImmediate(op.Imm32);

                if (!_hashTestLogged)
                {
                    context.TranslatorContext.GpuAccessor.Log(
                        $"[HASH_TEST] Guest FS: 0x{HashTestFsAddress:X} | " +
                        $"Imm32: {BitConverter.Int32BitsToSingle(op.Imm32).ToString(CultureInfo.InvariantCulture)} | " +
                        $"SrcA: R{op.SrcA} | Dest: R{op.Dest} | " +
                        $"Sat: {op.Sat} | WriteCC: {op.WriteCC} | " +
                        $"Override: {(isHashMultiply ? "YES" : "NO")}" +
                        (isHashMultiply
                            ? $" | Replacement value: {_hashConstValue.Value.ToString(CultureInfo.InvariantCulture)}"
                            : string.Empty));

                    if (isHashMultiply)
                    {
                        _hashTestLogged = true;
                    }
                }

                if (isHashMultiply)
                {
                    context.Copy(GetDest(op.Dest), ConstF(_hashConstValue.Value));

                    return;
                }
            }

            Operand srcA = GetSrcReg(context, op.SrcA);
            Operand srcB = GetSrcImm(context, op.Imm32);

            EmitFmul(context, Instruction.FP32, MultiplyScale.NoScale, srcA, srcB, op.Dest, false, op.Sat, op.WriteCC);
        }

        public static void Hadd2R(EmitterContext context)
        {
            InstHadd2R op = context.GetOp<InstHadd2R>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, op.NegA, op.AbsA);
            Operand[] srcB = GetHalfSrc(context, op.BSwizzle, op.SrcB, op.NegB, op.AbsB);

            EmitHadd2Hmul2(context, op.OFmt, srcA, srcB, isAdd: true, op.Dest, op.Sat);
        }

        public static void Hadd2I(EmitterContext context)
        {
            InstHadd2I op = context.GetOp<InstHadd2I>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, op.NegA, op.AbsA);
            Operand[] srcB = GetHalfSrc(context, op.BimmH0, op.BimmH1);

            EmitHadd2Hmul2(context, op.OFmt, srcA, srcB, isAdd: true, op.Dest, op.Sat);
        }

        public static void Hadd2C(EmitterContext context)
        {
            InstHadd2C op = context.GetOp<InstHadd2C>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, op.NegA, op.AbsA);
            Operand[] srcB = GetHalfSrc(context, HalfSwizzle.F32, op.CbufSlot, op.CbufOffset, op.NegB, op.AbsB);

            EmitHadd2Hmul2(context, op.OFmt, srcA, srcB, isAdd: true, op.Dest, op.Sat);
        }

        public static void Hadd232i(EmitterContext context)
        {
            InstHadd232i op = context.GetOp<InstHadd232i>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, op.NegA, false);
            Operand[] srcB = GetHalfSrc(context, op.Imm);

            EmitHadd2Hmul2(context, OFmt.F16, srcA, srcB, isAdd: true, op.Dest, op.Sat);
        }

        public static void Hfma2R(EmitterContext context)
        {
            InstHfma2R op = context.GetOp<InstHfma2R>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, false, false);
            Operand[] srcB = GetHalfSrc(context, op.BSwizzle, op.SrcB, op.NegA, false);
            Operand[] srcC = GetHalfSrc(context, op.CSwizzle, op.SrcC, op.NegC, false);

            EmitHfma2(context, op.OFmt, srcA, srcB, srcC, op.Dest, op.Sat);
        }

        public static void Hfma2I(EmitterContext context)
        {
            InstHfma2I op = context.GetOp<InstHfma2I>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, false, false);
            Operand[] srcB = GetHalfSrc(context, op.BimmH0, op.BimmH1);
            Operand[] srcC = GetHalfSrc(context, op.CSwizzle, op.SrcC, op.NegC, false);

            EmitHfma2(context, op.OFmt, srcA, srcB, srcC, op.Dest, op.Sat);
        }

        public static void Hfma2C(EmitterContext context)
        {
            InstHfma2C op = context.GetOp<InstHfma2C>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, false, false);
            Operand[] srcB = GetHalfSrc(context, HalfSwizzle.F32, op.CbufSlot, op.CbufOffset, op.NegA, false);
            Operand[] srcC = GetHalfSrc(context, op.CSwizzle, op.SrcC, op.NegC, false);

            EmitHfma2(context, op.OFmt, srcA, srcB, srcC, op.Dest, op.Sat);
        }

        public static void Hfma2Rc(EmitterContext context)
        {
            InstHfma2Rc op = context.GetOp<InstHfma2Rc>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, false, false);
            Operand[] srcB = GetHalfSrc(context, op.CSwizzle, op.SrcC, op.NegA, false);
            Operand[] srcC = GetHalfSrc(context, HalfSwizzle.F32, op.CbufSlot, op.CbufOffset, op.NegC, false);

            EmitHfma2(context, op.OFmt, srcA, srcB, srcC, op.Dest, op.Sat);
        }

        public static void Hfma232i(EmitterContext context)
        {
            InstHfma232i op = context.GetOp<InstHfma232i>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, false, false);
            Operand[] srcB = GetHalfSrc(context, op.Imm);
            Operand[] srcC = GetHalfSrc(context, HalfSwizzle.F16, op.Dest, op.NegC, false);

            EmitHfma2(context, OFmt.F16, srcA, srcB, srcC, op.Dest, saturate: false);
        }

        public static void Hmul2R(EmitterContext context)
        {
            InstHmul2R op = context.GetOp<InstHmul2R>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, false, op.AbsA);
            Operand[] srcB = GetHalfSrc(context, op.BSwizzle, op.SrcB, op.NegA, op.AbsB);

            EmitHadd2Hmul2(context, op.OFmt, srcA, srcB, isAdd: false, op.Dest, op.Sat);
        }

        public static void Hmul2I(EmitterContext context)
        {
            InstHmul2I op = context.GetOp<InstHmul2I>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, op.NegA, op.AbsA);
            Operand[] srcB = GetHalfSrc(context, op.BimmH0, op.BimmH1);

            EmitHadd2Hmul2(context, op.OFmt, srcA, srcB, isAdd: false, op.Dest, op.Sat);
        }

        public static void Hmul2C(EmitterContext context)
        {
            InstHmul2C op = context.GetOp<InstHmul2C>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, false, op.AbsA);
            Operand[] srcB = GetHalfSrc(context, HalfSwizzle.F32, op.CbufSlot, op.CbufOffset, op.NegA, op.AbsB);

            EmitHadd2Hmul2(context, op.OFmt, srcA, srcB, isAdd: false, op.Dest, op.Sat);
        }

        public static void Hmul232i(EmitterContext context)
        {
            InstHmul232i op = context.GetOp<InstHmul232i>();

            Operand[] srcA = GetHalfSrc(context, op.ASwizzle, op.SrcA, false, false);
            Operand[] srcB = GetHalfSrc(context, op.Imm32);

            EmitHadd2Hmul2(context, OFmt.F16, srcA, srcB, isAdd: false, op.Dest, op.Sat);
        }

        private static void EmitFadd(
            EmitterContext context,
            Instruction fpType,
            Operand srcA,
            Operand srcB,
            int rd,
            bool negateA,
            bool negateB,
            bool absoluteA,
            bool absoluteB,
            bool saturate,
            bool writeCC)
        {
            bool isFP64 = fpType == Instruction.FP64;

            srcA = context.FPAbsNeg(srcA, absoluteA, negateA, fpType);
            srcB = context.FPAbsNeg(srcB, absoluteB, negateB, fpType);

            Operand res = context.FPSaturate(context.FPAdd(srcA, srcB, fpType), saturate, fpType);

            SetDest(context, res, rd, isFP64);

            SetFPZnFlags(context, res, writeCC, fpType);
        }

        private static void EmitFfma(
            EmitterContext context,
            Instruction fpType,
            Operand srcA,
            Operand srcB,
            Operand srcC,
            int rd,
            bool negateB,
            bool negateC,
            bool saturate,
            bool writeCC)
        {
            bool isFP64 = fpType == Instruction.FP64;

            srcB = context.FPNegate(srcB, negateB, fpType);
            srcC = context.FPNegate(srcC, negateC, fpType);

            Operand res = context.FPSaturate(context.FPFusedMultiplyAdd(srcA, srcB, srcC, fpType), saturate, fpType);

            SetDest(context, res, rd, isFP64);

            SetFPZnFlags(context, res, writeCC, fpType);
        }

        private static void EmitFmul(
            EmitterContext context,
            Instruction fpType,
            MultiplyScale scale,
            Operand srcA,
            Operand srcB,
            int rd,
            bool negateB,
            bool saturate,
            bool writeCC)
        {
            bool isFP64 = fpType == Instruction.FP64;

            srcB = context.FPNegate(srcB, negateB, fpType);

            if (scale != MultiplyScale.NoScale)
            {
                Operand scaleConst = scale switch
                {
                    MultiplyScale.D2 => ConstF(0.5f),
                    MultiplyScale.D4 => ConstF(0.25f),
                    MultiplyScale.D8 => ConstF(0.125f),
                    MultiplyScale.M2 => ConstF(2f),
                    MultiplyScale.M4 => ConstF(4f),
                    MultiplyScale.M8 => ConstF(8f),
                    _ => ConstF(1f), // Invalid, behave as if it had no scale.
                };

                if (scaleConst.AsFloat() == 1f)
                {
                    context.TranslatorContext.GpuAccessor.Log($"Invalid FP multiply scale \"{scale}\".");
                }

                if (isFP64)
                {
                    scaleConst = context.FP32ConvertToFP64(scaleConst);
                }

                srcA = context.FPMultiply(srcA, scaleConst, fpType);
            }

            Operand res = context.FPSaturate(context.FPMultiply(srcA, srcB, fpType), saturate, fpType);

            SetDest(context, res, rd, isFP64);

            SetFPZnFlags(context, res, writeCC, fpType);
        }

        private static void EmitHadd2Hmul2(
            EmitterContext context,
            OFmt swizzle,
            Operand[] srcA,
            Operand[] srcB,
            bool isAdd,
            int rd,
            bool saturate)
        {
            Operand[] res = new Operand[2];

            for (int index = 0; index < res.Length; index++)
            {
                if (isAdd)
                {
                    res[index] = context.FPAdd(srcA[index], srcB[index]);
                }
                else
                {
                    res[index] = context.FPMultiply(srcA[index], srcB[index]);
                }

                res[index] = context.FPSaturate(res[index], saturate);
            }

            context.Copy(GetDest(rd), GetHalfPacked(context, swizzle, res, rd));
        }

        public static void EmitHfma2(
            EmitterContext context,
            OFmt swizzle,
            Operand[] srcA,
            Operand[] srcB,
            Operand[] srcC,
            int rd,
            bool saturate)
        {
            Operand[] res = new Operand[2];

            for (int index = 0; index < res.Length; index++)
            {
                res[index] = context.FPFusedMultiplyAdd(srcA[index], srcB[index], srcC[index]);
                res[index] = context.FPSaturate(res[index], saturate);
            }

            context.Copy(GetDest(rd), GetHalfPacked(context, swizzle, res, rd));
        }
    }
}
