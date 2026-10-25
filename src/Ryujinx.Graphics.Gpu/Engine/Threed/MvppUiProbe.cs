
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppUiProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_UIPROBE") == "1";

        private const int MaxEpochsPerFrame = 64;
        private const int FullDetailFrames = 8;
        private const int VerdictThrottleMs = 2000;

        private struct Epoch
        {
            public ulong Addr;
            public Format Fmt;
            public int W;
            public int H;
            public float Scale;
            public bool AnyDepth;
            public bool AnyBlend;
            public int Draws;
            public int NoDepthDraws;
            public int FirstNoDepthAt;
        }

        private static readonly List<Epoch> _epochs = new();
        private static float _prevScale = 1f;
        private static int _frameNo;
        private static int _framesLoggedFull;
        private static long _lastVerdictMs;
        private static long _lastDetailMs;

        private static readonly int _dumpAfterSeconds =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_UIPROBE_DUMP"), out int ds) && ds > 0 ? ds : 0;

        private static long _dumpArmedAtMs;
        private static bool _dumpDone;
        private static int _dumpSeq;
        private static Image.Texture _epochTex;

        private static readonly bool _hudless =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_HUDLESS") == "1";

        private const int HudlessMinDraws = 20;

        private static readonly bool _hudlessFeed =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_HUDLESS_FEED") == "1";

        private static ITexture _hudlessCopy;
        private static int _hudlessCopyW;
        private static int _hudlessCopyH;
        private static Format _hudlessCopyFmt;
        private static long _hudlessFeedId;
        private static int _hudlessCopyFails;

        private static int _renderWidthSeen;
        private static int _hudlessHits;
        private static int _hudlessFrames;
        private static int _hudlessMultiple;
        private static long _hudlessLogMs;
        private static int _hudlessInFrame;
        private static int _hudlessBestDraws;
        private static ulong _hudlessBestAddr;
        private static int _hudlessBestRank;
        private static int _hudlessRetenus;
        private static int _hudlessEchecsLogues;
        private static string _lastVerdictSig = "";

        public static void OnDraw(GpuChannel channel, ref ThreedClassState state, GpuContext context)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                OnDrawImpl(channel, ref state, context);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP UI probe: disabled after unexpected error: {e}");
            }
        }

        public static void OnPresent()
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                if (_epochs.Count > 0)
                {
                    FlushFrame();
                }
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP UIPROBE: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel, ref ThreedClassState state, GpuContext context)
        {
            float scale = channel.TextureManager.RenderTargetScale;

            if (_prevScale == 1f && scale > 1f && _epochs.Count > 0)
            {
                FlushFrame();
            }
            _prevScale = scale;

            Image.Texture col0 = channel.TextureManager.RenderTargetColor0;

            ulong addr = 0;
            Format fmt = Format.R8G8B8A8Unorm;
            int w = 0, h = 0;

            if (col0 != null)
            {
                addr = col0.Range.GetSubRange(0).Address;
                fmt = col0.Info.FormatInfo.Format;
                w = col0.Info.Width;
                h = col0.Info.Height;
            }

            bool depth = state.DepthTestEnable;
            bool blend = state.BlendEnable[0];

            if (_epochs.Count > 0)
            {
                ref Epoch last = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_epochs)[_epochs.Count - 1];
                if (last.Addr == addr && last.Fmt == fmt && last.W == w && last.H == h)
                {
                    last.Draws++;
                    last.AnyDepth |= depth;
                    last.AnyBlend |= blend;

                    if (!depth)
                    {
                        if (last.NoDepthDraws == 0)
                        {
                            last.FirstNoDepthAt = last.Draws;
                        }

                        last.NoDepthDraws++;
                    }

                    return;
                }
            }

            if (_hudless && _epochs.Count > 0)
            {
                Epoch fini = _epochs[^1];

                if (fini.W > _renderWidthSeen)
                {
                    _renderWidthSeen = fini.W;
                }

                bool ldr8 = fini.Fmt == Format.R8G8B8A8Unorm || fini.Fmt == Format.B8G8R8A8Unorm;

                if (ldr8 && fini.W == _renderWidthSeen && fini.Draws >= HudlessMinDraws)
                {
                    _hudlessInFrame++;

                    if (fini.Draws >= _hudlessBestDraws)
                    {
                        _hudlessBestDraws = fini.Draws;
                        _hudlessBestAddr = fini.Addr;
                        _hudlessBestRank = _epochs.Count;

                        if (_hudlessFeed && _epochTex != null && context != null)
                        {
                            CopyHudless(context);
                        }
                    }
                }
            }

            TryDumpEpoch();
            _epochTex = col0;

            if (_epochs.Count < MaxEpochsPerFrame)
            {
                _epochs.Add(new Epoch
                {
                    Addr = addr,
                    Fmt = fmt,
                    W = w,
                    H = h,
                    Scale = scale,
                    AnyDepth = depth,
                    AnyBlend = blend,
                    Draws = 1,
                    NoDepthDraws = depth ? 0 : 1,
                    FirstNoDepthAt = depth ? 0 : 1,
                });
            }
        }

        private static void CopyHudless(GpuContext context)
        {
            try
            {
                Image.Texture src = _epochTex;
                ITexture host = src.HostTexture;

                if (host == null)
                {
                    return;
                }

                int w = src.Info.Width;
                int h = src.Info.Height;
                Format fmt = src.Info.FormatInfo.Format;

                if (_hudlessCopy == null || _hudlessCopyW != w || _hudlessCopyH != h || _hudlessCopyFmt != fmt)
                {
                    _hudlessCopy?.Release();
                    _hudlessCopy = context.Renderer.CreateTexture(new TextureCreateInfo(
                        w, h, 1, 1, 1, 1, 1, 1,
                        fmt,
                        DepthStencilMode.Depth,
                        Target.Texture2D,
                        SwizzleComponent.Red,
                        SwizzleComponent.Green,
                        SwizzleComponent.Blue,
                        SwizzleComponent.Alpha));
                    _hudlessCopyW = w;
                    _hudlessCopyH = h;
                    _hudlessCopyFmt = fmt;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-HUDLESSFEED: texture de destination creee {w}x{h} {fmt}.");
                }

                host.CopyTo(_hudlessCopy, 0, 0);

                GAL.MvppColorSnapshot.SceneColorHost = _hudlessCopy;
                GAL.MvppColorSnapshot.FrameId = ++_hudlessFeedId;
            }
            catch (Exception e)
            {
                if (++_hudlessCopyFails <= 3)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"MVPP-HUDLESSFEED: copie echouee: {e.Message}");
                }
            }
        }

        private static void TryDumpEpoch()
        {
            if (_dumpAfterSeconds == 0 || _dumpDone || _epochTex == null)
            {
                return;
            }

            long now = Environment.TickCount64;

            if (_dumpArmedAtMs == 0)
            {
                _dumpArmedAtMs = now;

                return;
            }

            if (now - _dumpArmedAtMs < _dumpAfterSeconds * 1000L)
            {
                return;
            }

            try
            {
                GAL.ITexture host = _epochTex.HostTexture;

                if (host == null)
                {
                    return;
                }

                ulong addr = _epochTex.Range.GetSubRange(0).Address;
                string dir = System.IO.Path.Combine("rtdump", "epochs");
                System.IO.Directory.CreateDirectory(dir);

                using GAL.PinnedSpan<byte> data = host.GetData();
                string nom = $"ep{_dumpSeq:D2}_{_epochTex.Info.Width}x{_epochTex.Info.Height}_" +
                             $"{_epochTex.Info.FormatInfo.Format}_at{addr:X}.bin";
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, nom), data.Get().ToArray());

                Logger.Info?.Print(LogClass.Gpu, $"MVPP-EPOCHDUMP: {nom} ecrit a la FERMETURE de son epoque.");

                if (++_dumpSeq >= 12)
                {
                    _dumpDone = true;
                    Logger.Info?.Print(LogClass.Gpu, "MVPP-EPOCHDUMP: termine (12 epoques capturees), sonde desarmee.");
                }
            }
            catch (Exception e)
            {
                _dumpDone = true;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP-EPOCHDUMP: abandonne apres erreur: {e.Message}");
            }
        }

        private static void FlushFrame()
        {
            _frameNo++;

            int sceneIdx = -1, compIdx = -1;
            long sceneArea = -1, compArea = -1;

            for (int i = 0; i < _epochs.Count; i++)
            {
                Epoch e = _epochs[i];
                long area = (long)e.W * e.H;
                bool hdr = e.Fmt == Format.R11G11B10Float || e.Fmt == Format.R16G16B16A16Float;

                if (hdr && area > sceneArea)
                {
                    sceneArea = area;
                    sceneIdx = i;
                }
                else if (!hdr && area > compArea)
                {
                    compArea = area;
                    compIdx = i;
                }
            }

            ulong compAddr = compIdx >= 0 ? _epochs[compIdx].Addr : 0;

            bool blendIntoComposite = false;
            ulong separateUiAddr = 0;
            int separateUiW = 0, separateUiH = 0;

            for (int i = 0; i < _epochs.Count; i++)
            {
                Epoch e = _epochs[i];
                bool hdr = e.Fmt == Format.R11G11B10Float || e.Fmt == Format.R16G16B16A16Float;
                if (hdr || !e.AnyBlend)
                {
                    continue;
                }

                if (e.Addr == compAddr)
                {
                    blendIntoComposite = true;
                }
                else
                {
                    separateUiAddr = e.Addr;
                    separateUiW = e.W;
                    separateUiH = e.H;
                }
            }

            string verdict =
                separateUiAddr != 0 ? "SEPARATE(GO)" :
                blendIntoComposite ? "INPLACE(NO-GO)" :
                "UNCLEAR";

            string sig = $"{verdict}|comp0x{compAddr:X}|ui0x{separateUiAddr:X}";
            long now = Environment.TickCount64;
            if (sig != _lastVerdictSig || now - _lastVerdictMs >= VerdictThrottleMs)
            {
                _lastVerdictSig = sig;
                _lastVerdictMs = now;

                string sceneStr = sceneIdx >= 0
                    ? $"0x{_epochs[sceneIdx].Addr:X}/{_epochs[sceneIdx].Fmt}/{_epochs[sceneIdx].W}x{_epochs[sceneIdx].H}"
                    : "none";
                string compStr = compIdx >= 0
                    ? $"0x{compAddr:X}/{_epochs[compIdx].Fmt}/{_epochs[compIdx].W}x{_epochs[compIdx].H}"
                    : "none";
                string uiStr = separateUiAddr != 0
                    ? $"0x{separateUiAddr:X}/{separateUiW}x{separateUiH}"
                    : "none";

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-UIPROBE f{_frameNo} DECISION={verdict} epochs={_epochs.Count} " +
                    $"scene={sceneStr} composite={compStr} blendIntoComposite={blendIntoComposite} separateUiRT={uiStr}");
            }

            bool periodic = now - _lastDetailMs >= 5000;

            if (_framesLoggedFull < FullDetailFrames || periodic)
            {
                if (periodic)
                {
                    _lastDetailMs = now;
                }

                _framesLoggedFull++;
                for (int i = 0; i < _epochs.Count; i++)
                {
                    Epoch e = _epochs[i];
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-UIPROBE f{_frameNo} epoch{i}: RT@0x{e.Addr:X} {e.Fmt} {e.W}x{e.H} " +
                        $"scale={e.Scale:0.##} depth={e.AnyDepth} blend={e.AnyBlend} draws={e.Draws}" +
                        (e.NoDepthDraws > 0 ? $"  >>> SANS-DEPTH {e.NoDepthDraws} (des le n°{e.FirstNoDepthAt}) = CANDIDAT HUD" : ""));
                }
            }

            if (_hudless)
            {
                _hudlessFrames++;

                if (_hudlessInFrame > 1)
                {
                    _hudlessMultiple++;
                }

                if (_hudlessBestDraws > 0)
                {
                    _hudlessRetenus++;
                    _hudlessHits++;

                    if (_hudlessRetenus <= 6)
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-HUDLESS elu: @0x{_hudlessBestAddr:X} {_hudlessBestDraws} dessins " +
                            $"(epoque n°{_hudlessBestRank}), {_hudlessInFrame} candidat(s) dans l'image.");
                    }
                }

                else if (_hudlessEchecsLogues < 8)
                {
                    _hudlessEchecsLogues++;

                    var vu = new System.Text.StringBuilder();

                    for (int i = 0; i < _epochs.Count; i++)
                    {
                        vu.Append($"[{_epochs[i].Fmt} {_epochs[i].W}x{_epochs[i].H} {_epochs[i].Draws}d] ");
                    }

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-HUDLESS AUCUN CANDIDAT sur cette image ({_epochs.Count} epoques) : " +
                        (vu.Length > 0 ? vu.ToString().TrimEnd() : "aucune epoque"));
                }

                _hudlessInFrame = 0;
                _hudlessBestDraws = 0;
                _hudlessBestAddr = 0;
                _hudlessBestRank = 0;

                if (now - _hudlessLogMs >= 5000)
                {
                    _hudlessLogMs = now;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-HUDLESS: {_hudlessHits} elus sur {_hudlessFrames} images " +
                        $"({(_hudlessFrames > 0 ? 100f * _hudlessHits / _hudlessFrames : 0f):0.0}% - doit valoir 100), " +
                        $"images ou plusieurs candidats se presentaient={_hudlessMultiple} " +
                        $"(l'election en garde UN, c'est desormais informatif), " +
                        $"largeur de rendu retenue={_renderWidthSeen}.");
                    _hudlessHits = 0;
                    _hudlessFrames = 0;
                    _hudlessMultiple = 0;
                    _hudlessRetenus = 0;
                }
            }

            _epochs.Clear();
        }
    }
}
