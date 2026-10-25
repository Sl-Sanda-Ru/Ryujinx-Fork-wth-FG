
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppWriterCensusProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVWRITER_CENSUS") == "1";

        private static bool _armedLogged;

        private static long _armedDraws;
        private static long _unarmedDraws;
        private static long _maskedOffDraws;
        private static long _summaryMs;

        private static readonly HashSet<ulong> _armedFs = new();
        private static readonly HashSet<ulong> _unarmedFs = new();
        private static readonly HashSet<ulong> _maskedOffFs = new();

        public static void OnPresent()
        {
            if (!Enabled)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[CENSUS] ARMED (RYUJINX_MVWRITER_CENSUS=1). Cross-checking MV-buffer writers against the fingerprint-armed registry.");
            }

            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000)
            {
                _summaryMs = now;

                int registry = NanScrubProbe.ArmedAddressCount();
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[CENSUS/HEARTBEAT ~3s v3-program] registry={registry} | ARMED-program draws={_armedDraws} distinctVA={_armedFs.Count} | " +
                    $"UNARMED-program WRITING (mask!=0): draws={_unarmedDraws} distinctVA={_unarmedFs.Count} | " +
                    $"bound-but-masked-off: draws={_maskedOffDraws} distinctVA={_maskedOffFs.Count}" +
                    (registry == 0 ? " *** REGISTRY EMPTY: cache ON or NANSCRUB missing = VOID ***" : ""));
            }
        }

        public static void OnDraw(TextureManager texMgr, ulong fsAddr, ReadOnlySpan<uint> writeMasks, Ryujinx.Graphics.Shader.ShaderProgramInfo fragInfo)
        {
            if (texMgr == null || fsAddr == 0)
            {
                return;
            }

            for (int slot = 0; slot < texMgr.ColorTargetsLength; slot++)
            {
                if (!MvppMvSyncProbe.IsMvBuffer(texMgr.GetColorTarget(slot)))
                {
                    continue;
                }

                uint mask = slot < writeMasks.Length ? writeMasks[slot] : 0xFu;

                if (mask == 0)
                {
                    _maskedOffDraws++;

                    bool newMasked;
                    lock (_maskedOffFs)
                    {
                        newMasked = _maskedOffFs.Add(fsAddr);
                    }

                    if (newMasked)
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[CENSUS] bound-but-masked-off writer: fs=0x{fsAddr:X} slot={slot} | distinct: {_maskedOffFs.Count}");
                    }

                    return;
                }

                if (fragInfo != null && fragInfo.MvppMvEncodeArmed)
                {
                    _armedDraws++;
                    lock (_armedFs)
                    {
                        _armedFs.Add(fsAddr);
                    }
                }
                else
                {
                    _unarmedDraws++;

                    bool isNew;
                    lock (_unarmedFs)
                    {
                        isNew = _unarmedFs.Add(fsAddr);
                    }

                    if (isNew)
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[CENSUS] MV writer whose PROGRAM carries no fingerprint AND WRITING: fs=0x{fsAddr:X} slot={slot} mask=0x{mask:X} | escaped every value scrub | distinct unarmed VAs: {_unarmedFs.Count}");
                    }
                }

                return;
            }
        }
    }
}
