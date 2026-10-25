
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppTwinMapProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_TWINMAP") == "1";

        private static bool _armedLogged;
        private static long _summaryMs;

        private class TwinStats
        {
            public readonly HashSet<ulong> Writers = new();
            public long WriteDraws;
            public int WritersLogged;

            public ulong GpuVa;
            public ulong Backing;
            public ulong LastTranslation;

            public WeakReference<Texture> LastTexture;

            public long Clears;
            public int ClearsLogged;
            public long MaskedBinds;

            public long BlackClears;
            public long NeutralClears;
        }

        private static readonly Dictionary<ulong, TwinStats> _twins = new();
        private static readonly HashSet<(ulong, ShaderStage, int)> _reads = new();
        private static WeakReference<Memory.MemoryManager> _mm;

        private static long _poolMatches;
        private static long _poolMismatches;
        private static readonly HashSet<(ulong, ulong, int)> _mismatchTuples = new();

        private static ulong GuestVa(Texture t)
        {
            return t.Range.GetSubRange(0).Address;
        }

        public static bool TryClassify(out ulong skyVa, out Texture materialTwin)
        {
            skyVa = 0;
            materialTwin = null;

            lock (_twins)
            {
                if (_twins.Count != 2)
                {
                    return false;
                }

                TwinStats sky = null, mat = null;
                ulong skyKey = 0;

                foreach (var kv in _twins)
                {
                    if (kv.Value.Writers.Count <= 4)
                    {
                        sky = kv.Value;
                        skyKey = kv.Key;
                    }
                    else if (kv.Value.Writers.Count >= 50)
                    {
                        mat = kv.Value;
                    }
                }

                if (sky == null || mat == null)
                {
                    TwinStats black = null, neutral = null;
                    ulong blackKey = 0;

                    foreach (var kv in _twins)
                    {
                        if (kv.Value.BlackClears >= 10 && kv.Value.NeutralClears == 0)
                        {
                            black = kv.Value;
                            blackKey = kv.Key;
                        }
                        else if (kv.Value.NeutralClears >= 10 && kv.Value.BlackClears == 0)
                        {
                            neutral = kv.Value;
                        }
                    }

                    if (black != null && neutral != null)
                    {
                        sky = black;
                        skyKey = blackKey;
                        mat = neutral;
                    }
                }

                if (sky == null || mat == null || mat.LastTexture == null)
                {
                    return false;
                }

                if (!mat.LastTexture.TryGetTarget(out Texture matTex))
                {
                    return false;
                }

                skyVa = skyKey;
                materialTwin = matTex;
                return true;
            }
        }

        public static ulong[] TwinVas()
        {
            lock (_twins)
            {
                ulong[] vas = new ulong[_twins.Count];
                _twins.Keys.CopyTo(vas, 0);
                return vas;
            }
        }

        public static void OnDraw(TextureManager texMgr, ulong fsAddr, Memory.MemoryManager memoryManager, ReadOnlySpan<uint> writeMasks)
        {
            if (!Enabled || texMgr == null || fsAddr == 0)
            {
                return;
            }

            if (memoryManager != null)
            {
                _mm = new WeakReference<Memory.MemoryManager>(memoryManager);
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[TWINMAP] ARMED (RYUJINX_TWINMAP=1). Mapping writers and readers of each MV twin by guest VA.");
            }

            bool any = false;

            for (int slot = 0; slot < texMgr.ColorTargetsLength; slot++)
            {
                Texture ct = texMgr.GetColorTarget(slot);
                if (!MvppMvSyncProbe.IsMvBuffer(ct))
                {
                    continue;
                }

                any = true;
                ulong va = GuestVa(ct);
                uint mask = (writeMasks.Length > slot) ? writeMasks[slot] : 0xFu;

                lock (_twins)
                {
                    TwinStats stats = GetOrAdd(va, ct, memoryManager);

                    stats.LastTexture = new WeakReference<Texture>(ct);

                    if (mask == 0)
                    {
                        stats.MaskedBinds++;
                        continue;
                    }

                    stats.WriteDraws++;

                    if (stats.Writers.Add(fsAddr) && stats.WritersLogged < 8)
                    {
                        stats.WritersLogged++;
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[TWINMAP] WRITER of twin@0x{va:X}: fs=0x{fsAddr:X} slot={slot} mask=0x{mask:X} (writer #{stats.Writers.Count}; first 8 logged)");
                    }
                }
            }

            if (any)
            {
                Heartbeat();
            }
        }

        private static TwinStats GetOrAdd(ulong va, Texture ct, Memory.MemoryManager memoryManager)
        {
            if (!_twins.TryGetValue(va, out TwinStats stats))
            {
                stats = new TwinStats
                {
                    GpuVa = ct.Info.GpuAddress,
                    Backing = va,
                };
                _twins[va] = stats;

                ulong translated = memoryManager?.Translate(ct.Info.GpuAddress) ?? 0;
                stats.LastTranslation = translated;

                Logger.Warning?.Print(LogClass.Gpu,
                    $"[TWINMAP] NEW TWIN target: twin@0x{va:X} ({_twins.Count} twin(s)) | GpuVa=0x{ct.Info.GpuAddress:X} backing=0x{va:X} translatesTo=0x{translated:X}" +
                    (translated != va ? " *** TRANSLATION != BACKING (aliasing/remap suspect) ***" : ""));
            }

            return stats;
        }

        public static void OnClear(Texture target, uint componentMask, float r, float g, float b, float a)
        {
            if (!Enabled || target == null || !MvppMvSyncProbe.IsMvBuffer(target))
            {
                return;
            }

            ulong va = GuestVa(target);

            lock (_twins)
            {
                TwinStats stats = GetOrAdd(va, target, null);
                stats.Clears++;

                if (r < 0.1f && g < 0.1f && b < 0.1f)
                {
                    stats.BlackClears++;
                }
                else if (r > 0.4f && r < 0.6f && g > 0.4f && g < 0.6f)
                {
                    stats.NeutralClears++;
                }

                if (stats.ClearsLogged < 4)
                {
                    stats.ClearsLogged++;
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[TWINMAP] CLEAR of twin@0x{va:X}: rgba=({r:0.###},{g:0.###},{b:0.###},{a:0.###}) mask=0x{componentMask:X} (clear #{stats.Clears})");
                }
            }
        }

        public static void OnRead(Texture texture, ShaderStage stage, int handle, TexturePool pool, int textureId, Memory.MemoryManager memoryManager)
        {
            if (!Enabled || texture == null || !MvppMvSyncProbe.IsMvBuffer(texture))
            {
                return;
            }

            ulong va = GuestVa(texture);
            bool isNew;

            lock (_twins)
            {
                isNew = _reads.Add((va, stage, handle));
            }

            if (isNew)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[TWINMAP] READER of twin@0x{va:X}: stage={stage} handle=0x{handle:X} (tcb_{handle:X}) | distinct readers: {_reads.Count}");
            }

            if (pool != null && memoryManager != null && textureId >= 0)
            {
                try
                {
                    ulong descAddr = pool.Address + (ulong)textureId * 0x20;
                    TextureDescriptor raw = System.Runtime.InteropServices.MemoryMarshal.Read<TextureDescriptor>(
                        memoryManager.Physical.GetSpan(descAddr, 0x20));
                    ulong rawVa = raw.UnpackAddress();
                    ulong boundVa = texture.Info.GpuAddress;

                    if (rawVa != boundVa)
                    {
                        _poolMismatches++;

                        bool newMismatch;
                        lock (_twins)
                        {
                            newMismatch = _mismatchTuples.Add((rawVa, boundVa, handle));
                        }

                        if (newMismatch)
                        {
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"[TWINMAP] *** POOL DESCRIPTOR MISMATCH ***: raw guest VA=0x{rawVa:X} vs BOUND VA=0x{boundVa:X} (poolId={textureId} handle=0x{handle:X} stage={stage}) | the emulator is reading a STALE binding");
                        }
                    }
                    else
                    {
                        _poolMatches++;
                    }
                }
                catch
                {
                }
            }

            Heartbeat();
        }

        private static void Heartbeat()
        {
            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000)
            {
                _summaryMs = now;

                Memory.MemoryManager mm = null;
                _mm?.TryGetTarget(out mm);

                lock (_twins)
                {
                    string summary = "";
                    foreach (var kv in _twins)
                    {
                        TwinStats stats = kv.Value;
                        summary += $" twin@0x{kv.Key:X}: writers={stats.Writers.Count} draws={stats.WriteDraws} maskedBinds={stats.MaskedBinds} CLEARS={stats.Clears} |";

                        if (mm != null && stats.GpuVa != 0)
                        {
                            ulong translated = mm.Translate(stats.GpuVa);

                            if (translated != stats.LastTranslation)
                            {
                                Logger.Warning?.Print(LogClass.Gpu,
                                    $"[TWINMAP] *** TRANSLATION CHANGED for twin@0x{kv.Key:X}: GpuVa=0x{stats.GpuVa:X} was->0x{stats.LastTranslation:X} now->0x{translated:X} (backing=0x{stats.Backing:X}) ***");
                                stats.LastTranslation = translated;
                            }
                            else if (translated != stats.Backing)
                            {
                                Logger.Warning?.Print(LogClass.Gpu,
                                    $"[TWINMAP] *** TRANSLATION != BACKING for twin@0x{kv.Key:X}: GpuVa=0x{stats.GpuVa:X} ->0x{translated:X} vs backing 0x{stats.Backing:X} ***");
                            }
                        }
                    }

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[TWINMAP/HEARTBEAT ~3s]{summary} readers(distinct va,stage,handle)={_reads.Count} | poolTruth: match={_poolMatches} MISMATCH={_poolMismatches}");
                }
            }
        }
    }
}
