
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppBuilderInProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_BUILDERIN") == "1";

        private static readonly (ulong Addr, string Name)[] Passes =
        {
            (0x1000AD730UL, "builder"),
            (0x1000B4530UL, "down64"),
            (0x1000C2F30UL, "coc64"),
            (0x1000AE430UL, "bokeh_fs"),
            (0x100071F30UL, "pingpong"),
        };

        private static readonly HashSet<ulong> _unknown = new();
        private static readonly HashSet<string> _census = new();
        private static int _censusLines;

        private static int TargetKey(TextureManager texMgr)
        {
            if (texMgr == null)
            {
                return 0;
            }

            try
            {
                Texture t = texMgr.GetColorTarget(0);

                return t == null ? 0 : (t.Info.Width * 8191) ^ t.Info.Height;
            }
            catch
            {
                return 0;
            }
        }

        private static void CensusTick(TextureManager texMgr, ulong fsAddr)
        {
            if (texMgr == null || _censusLines >= 400)
            {
                return;
            }

            for (int i = 0; i < 8; i++)
            {
                Texture t;

                try
                {
                    t = texMgr.GetColorTarget(i);
                }
                catch
                {
                    break;
                }

                if (t == null)
                {
                    continue;
                }

                string key = $"{fsAddr:X}|rt{i}|{t.Info.Width}x{t.Info.Height}|{t.Info.FormatInfo.Format}";

                if (!_census.Add(key))
                {
                    continue;
                }

                _censusLines++;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[CENSUS] fs=0x{fsAddr:X} rt{i} {t.Info.Width}x{t.Info.Height} {t.Info.FormatInfo.Format}");
            }
        }
        private static string _shape = "?";

        public static Texture LastTwin { get; private set; }

        private static bool MatchesUnknownTarget(TextureManager texMgr, out string shape)
        {
            shape = null;

            if (texMgr == null)
            {
                return false;
            }

            for (int i = 0; i < 8; i++)
            {
                Texture t;

                try
                {
                    t = texMgr.GetColorTarget(i);
                }
                catch
                {
                    break;
                }

                if (t == null)
                {
                    continue;
                }

                int w = t.Info.Width, h = t.Info.Height;

                if ((w == 64 && (h == 36 || h == 180)) || (w == 320 && h == 36))
                {
                    shape = $"{w}x{h} {t.Info.FormatInfo.Format}";
                    return true;
                }
            }

            return false;
        }

        private static string PassName(ulong addr)
        {
            foreach ((ulong a, string n) in Passes)
            {
                if (a == addr)
                {
                    return n;
                }
            }

            return null;
        }
        private const int FragmentStageIndex = 4;
        private const int CbufSlot = 3;

        private static readonly int[] WantedC3 = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13 };

        private struct Bound
        {
            public ulong Va;
            public string Fmt;
            public int W;
            public int H;
            public int Handle;
            public int Binding;
            public string Filter;
            public int Levels;
            public Texture Tex;
        }

        private static readonly List<Bound> _inputs = new();
        private static bool _armed;
        private static bool _failLogged;

        private const int MaxReports = 40;
        private static int _reports;
        private static readonly Dictionary<ulong, long> _lastBeat = new();
        private static readonly Dictionary<string, string> _lastSig = new();
        private static readonly Dictionary<string, int> _perConfig = new();
        private static long _builderDraws;
        private static TextureManager _texMgr;

        public static void OnInput(Texture texture, ShaderStage stage, int handle, int binding, Sampler sampler)
        {
            if (!Enabled || texture == null || stage != ShaderStage.Fragment)
            {
                return;
            }

            lock (_inputs)
            {
                if (_inputs.Count >= 64)
                {
                    return;
                }

                TextureInfo info = texture.Info;
                ulong va;

                try
                {
                    va = texture.Range.GetSubRange(0).Address;
                }
                catch
                {
                    va = 0;
                }

                _inputs.Add(new Bound
                {
                    Va = va,
                    Fmt = info.FormatInfo.Format.ToString(),
                    W = info.Width,
                    H = info.Height,
                    Handle = handle,
                    Binding = binding,
                    Levels = texture.Info.Levels,
                    Tex = texture,
                    Filter = sampler == null
                        ? "sampler=null"
                        : $"min={sampler.ProbeMinFilter} mag={sampler.ProbeMagFilter} " +
                          $"wrap={sampler.ProbeAddressU}/{sampler.ProbeAddressV}",
                });
            }
        }

        public static void OnDraw(GpuChannel channel, TextureManager texMgr, ulong fsAddr)
        {
            if (!Enabled)
            {
                return;
            }

            lock (_inputs)
            {
                try
                {
                    CensusTick(texMgr, fsAddr);

                    string pass = PassName(fsAddr);

                    if (pass == null)
                    {
                        if (!MatchesUnknownTarget(texMgr, out string shape))
                        {
                            return;
                        }

                        if (_unknown.Add(fsAddr))
                        {
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"[BUILDERIN] *** UNLISTED PASS writing {shape} : guest fs = 0x{fsAddr:X} ***");
                        }

                        pass = $"unlisted_{fsAddr:X}";
                    }

                    _builderDraws++;

                    if (fsAddr == Passes[0].Addr)
                    {
                        foreach (Bound bb in _inputs)
                        {
                            if (bb.W == 1280 && bb.H == 720)
                            {
                                LastTwin = bb.Tex;
                            }
                        }
                    }

                    if (!_armed)
                    {
                        _armed = true;
                        Logger.Warning?.Print(LogClass.Gpu,
                            "[BUILDERIN] armed: tracking the DoF/motion chain (builder, down64, coc64, bokeh_fs, pingpong)");
                    }

                    long now = Environment.TickCount64;
                    ulong beatKey = fsAddr ^ ((ulong)TargetKey(texMgr) << 40);
                    _lastBeat.TryGetValue(beatKey, out long beat);

                    if (beat != 0 && now - beat < 250)
                    {
                        return;
                    }

                    _lastBeat[beatKey] = now;

                    _texMgr = texMgr;
                    string signature = BuildSignature(channel, out string[] lines);

                    string config = pass + "|" + _shape;

                    _lastSig.TryGetValue(config, out string prev);
                    _perConfig.TryGetValue(config, out int seen);

                    if (signature == prev || seen >= 4 || _reports >= MaxReports)
                    {
                        return;
                    }

                    bool first = prev == null;
                    _lastSig[config] = signature;
                    _perConfig[config] = seen + 1;
                    _reports++;

                    Logger.Warning?.Print(LogClass.Gpu,
                        first
                            ? $"[BUILDERIN] --- {pass} [{_shape}] state #1 ---"
                            : $"[BUILDERIN] --- {pass} [{_shape}] state #{seen + 1} CHANGED ---");

                    foreach (string line in lines)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, line);
                    }

                    if (_reports == MaxReports)
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            "[BUILDERIN] report cap reached - further changes are no longer printed");
                    }
                }
                catch (Exception ex)
                {
                    if (!_failLogged)
                    {
                        _failLogged = true;
                        Logger.Warning?.Print(LogClass.Gpu, $"[BUILDERIN] read FAILED: {ex.Message}");
                    }
                }
                finally
                {
                    _inputs.Clear();
                }
            }
        }

        private static string BuildSignature(GpuChannel channel, out string[] lines)
        {
            List<string> outLines = new();
            StringBuilder sig = new();
            StringBuilder shape = new();

            foreach (Bound b in _inputs)
            {
                outLines.Add(
                    $"[BUILDERIN] sampler fp_t_tcb_{b.Handle:X} (binding {b.Binding}) " +
                    $"<- VA 0x{b.Va:X} {b.W}x{b.H} {b.Fmt}  mips={b.Levels}  [{b.Filter}]");
                sig.Append($"{b.Handle:X}:{b.Va:X}:{b.W}x{b.H}:{b.Levels}:{b.Filter};");
                shape.Append($"{b.W}x{b.H},");
            }

            if (_inputs.Count == 0)
            {
                outLines.Add("[BUILDERIN] no sampled inputs recorded for this draw");
                sig.Append("noinputs;");
            }

            AppendTargets(_texMgr, outLines, sig, shape);
            AppendCbuf(channel, CbufSlot, "fp_c3", WantedC3, outLines, sig);
            AppendCbuf(channel, 1, "fp_c1", new[] { 0 }, outLines, sig);

            lines = outLines.ToArray();
            _shape = shape.ToString();

            return sig.ToString();
        }

        private static void AppendTargets(TextureManager texMgr, List<string> lines, StringBuilder sig, StringBuilder shape)
        {
            if (texMgr == null)
            {
                lines.Add("[BUILDERIN] no texture manager for this draw");
                sig.Append("notexmgr;");
                return;
            }

            int found = 0;

            for (int i = 0; i < 8; i++)
            {
                Texture t;

                try
                {
                    t = texMgr.GetColorTarget(i);
                }
                catch
                {
                    break;
                }

                if (t == null)
                {
                    continue;
                }

                found++;
                TextureInfo info = t.Info;
                ulong va;

                try
                {
                    va = t.Range.GetSubRange(0).Address;
                }
                catch
                {
                    va = 0;
                }

                lines.Add($"[BUILDERIN] OUTPUT rt{i} -> VA 0x{va:X} {info.Width}x{info.Height} {info.FormatInfo.Format}");
                sig.Append($"rt{i}:{va:X}:{info.Width}x{info.Height}:{info.FormatInfo.Format};");
                shape.Append($"{info.Width}x{info.Height}>");
            }

            if (found == 0)
            {
                lines.Add("[BUILDERIN] OUTPUT: no colour target reported");
                sig.Append("nort;");
            }
        }

        private static void AppendCbuf(
            GpuChannel channel,
            int slot,
            string name,
            int[] indices,
            List<string> lines,
            StringBuilder sig)
        {
            uint useMask = channel.BufferManager.GetGraphicsUniformBufferUseMask(FragmentStageIndex);

            if ((useMask & (1u << slot)) == 0)
            {
                lines.Add($"[BUILDERIN] {name} (slot {slot}) not bound for this draw");
                sig.Append($"{name}:unbound;");
                return;
            }

            ulong addr = channel.BufferManager.GetGraphicsUniformBufferAddress(FragmentStageIndex, slot);

            if (addr == 0 || addr == ulong.MaxValue)
            {
                lines.Add($"[BUILDERIN] {name} (slot {slot}) has no address");
                sig.Append($"{name}:noaddr;");
                return;
            }

            foreach (int i in indices)
            {
                StringBuilder sb = new();
                sb.Append($"[BUILDERIN] {name}.data[{i}] =");

                for (int c = 0; c < 4; c++)
                {
                    float f = BitConverter.Int32BitsToSingle(
                        channel.MemoryManager.Physical.Read<int>(addr + (ulong)(i * 16 + c * 4)));

                    sb.Append($" {f:G9}");
                    sig.Append($"{f:G9},");
                }

                lines.Add(sb.ToString());
            }
        }
    }
}
