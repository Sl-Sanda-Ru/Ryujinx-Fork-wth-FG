
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppScenePassProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_SCENE_PROBE") == "1";

        private struct In
        {
            public ulong Va;
            public string Fmt;
            public int W;
            public int H;
            public ShaderStage Stage;
            public int Handle;
            public int Binding;
            public AddressMode WrapU;
            public AddressMode WrapV;
            public MinFilter MinF;
            public MagFilter MagF;
        }

        private static readonly List<In> _inputs = new();
        private static readonly HashSet<ulong> _seenShaders = new();
        private static int _announced;

        public static void AnnounceOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref _announced, 1) != 0)
            {
                return;
            }

            string v = Environment.GetEnvironmentVariable("RYUJINX_SCENE_PROBE");
            Logger.Info?.Print(LogClass.Gpu,
                $"SCENEPROBE gate check: RYUJINX_SCENE_PROBE={(v ?? "<unset>")} -> enabled={Enabled}. " +
                (Enabled ? "Probe ACTIVE." : "Probe OFF."));
        }

        private static ulong Va(Texture t)
        {
            try
            {
                return t.Range.GetSubRange(0).Address;
            }
            catch
            {
                return 0;
            }
        }

        public static void OnInput(Texture texture, ShaderStage stage, int handle, int binding, Sampler sampler)
        {
            if (texture == null)
            {
                return;
            }

            lock (_inputs)
            {
                if (_inputs.Count >= 256)
                {
                    return;
                }

                TextureInfo info = texture.Info;
                _inputs.Add(new In
                {
                    Va = Va(texture),
                    Fmt = info.FormatInfo.Format.ToString(),
                    W = info.Width,
                    H = info.Height,
                    Stage = stage,
                    Handle = handle,
                    Binding = binding,
                    WrapU = sampler != null ? sampler.ProbeAddressU : default,
                    WrapV = sampler != null ? sampler.ProbeAddressV : default,
                    MinF = sampler != null ? sampler.ProbeMinFilter : default,
                    MagF = sampler != null ? sampler.ProbeMagFilter : default,
                });
            }
        }

        public static void OnDraw(TextureManager texMgr, ulong fsAddr, ulong vsAddr, long frame)
        {
            lock (_inputs)
            {
                try
                {
                    Texture colorRt = texMgr?.GetAnyRenderTarget();
                    if (colorRt == null)
                    {
                        return;
                    }

                    TextureInfo rt = colorRt.Info;
                    bool isScene = rt.Width == 1280 && rt.Height == 720 && rt.FormatInfo.Format == Format.R11G11B10Float;
                    bool isDof = rt.Width == 512 && rt.Height == 288 && rt.FormatInfo.Format == Format.R16G16B16A16Float;
                    if (!isScene && !isDof)
                    {
                        return;
                    }

                    if (!_seenShaders.Add(fsAddr))
                    {
                        return;
                    }

                    ulong fbVa = Va(colorRt);
                    string kind = isDof ? "512x288 R16G16B16A16 (DoF/bokeh)" : "720p R11G11B10 (scene)";

                    Logger.Info?.Print(LogClass.Gpu,
                        $"SCENEPROBE pass writes {kind}: fs=0x{fsAddr:X} vs=0x{vsAddr:X} fb=0x{fbVa:X} frame={frame} inputs={_inputs.Count} =====");

                    for (int slot = 0; slot < texMgr.ColorTargetsLength; slot++)
                    {
                        Texture ct = texMgr.GetColorTarget(slot);
                        if (ct != null)
                        {
                            TextureInfo cti = ct.Info;
                            Logger.Info?.Print(LogClass.Gpu,
                                $"SCENEPROBE   -> out_attr{slot} RT 0x{Va(ct):X} {cti.FormatInfo.Format}/{cti.Width}x{cti.Height}");
                        }
                    }

                    foreach (In i in _inputs)
                    {
                        (long caseA, long caseB) = MvppFeedbackProbe.Classify(i.Va);
                        bool history = caseA > 0 && caseA >= caseB;

                        var sb = new StringBuilder();
                        sb.Append($"SCENEPROBE   <- fp_t_tcb_{i.Handle:X} (bind={i.Binding}) 0x{i.Va:X} {i.Fmt}/{i.W}x{i.H} {i.Stage} wrap=[{i.WrapU}/{i.WrapV}] filter=[{i.MinF}/{i.MagF}] [A={caseA},B={caseB}]");
                        if (history)
                        {
                            sb.Append(" [H HISTORY]");
                        }

                        Logger.Info?.Print(LogClass.Gpu, sb.ToString());
                    }
                }
                finally
                {
                    _inputs.Clear();
                }
            }
        }
    }
}
