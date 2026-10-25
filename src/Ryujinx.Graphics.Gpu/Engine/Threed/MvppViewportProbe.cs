
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppViewportProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_VP_PROBE") == "1";

        private static bool _announced;
        private static long _windowMs;

        private sealed class Stats
        {
            public int Draws;
            public int Narrow;
            public int Short;
            public int TransformOff;
            public float MinX, MaxX, MinW, MaxW, MinY, MaxY, MinH, MaxH;
            public bool Seeded;

            public int ClipZero;
            public int ClipFull;
            public int ClipCuts;
            public int ClipMinX, ClipMaxX, ClipMinW, ClipMaxW;
            public bool ClipSeeded;
            public int A2c;
            public int A2cDither;
            public int NegScaleX;
            public int SwizzleXOdd;

            public void AddClip(int x, int w)
            {
                if (!ClipSeeded)
                {
                    ClipSeeded = true;
                    ClipMinX = ClipMaxX = x;
                    ClipMinW = ClipMaxW = w;
                    return;
                }

                if (x < ClipMinX) { ClipMinX = x; }
                if (x > ClipMaxX) { ClipMaxX = x; }
                if (w < ClipMinW) { ClipMinW = w; }
                if (w > ClipMaxW) { ClipMaxW = w; }
            }

            public int ScissorOn;
            public int ScissorCuts;
            public int ScissorSeen;
            public int SciMinX1, SciMaxX1, SciMinX2, SciMaxX2;
            public int ScrMinW, ScrMaxW, ScrMinH, ScrMaxH;
            public bool ScrSeeded;

            public void AddScissor(int x1, int x2)
            {
                if (ScissorSeen++ == 0)
                {
                    SciMinX1 = SciMaxX1 = x1;
                    SciMinX2 = SciMaxX2 = x2;
                    return;
                }

                if (x1 < SciMinX1) { SciMinX1 = x1; }
                if (x1 > SciMaxX1) { SciMaxX1 = x1; }
                if (x2 < SciMinX2) { SciMinX2 = x2; }
                if (x2 > SciMaxX2) { SciMaxX2 = x2; }
            }

            public void AddScreen(int w, int h)
            {
                if (!ScrSeeded)
                {
                    ScrSeeded = true;
                    ScrMinW = ScrMaxW = w;
                    ScrMinH = ScrMaxH = h;
                    return;
                }

                if (w < ScrMinW) { ScrMinW = w; }
                if (w > ScrMaxW) { ScrMaxW = w; }
                if (h < ScrMinH) { ScrMinH = h; }
                if (h > ScrMaxH) { ScrMaxH = h; }
            }

            public void Add(float x, float y, float w, float h)
            {
                if (!Seeded)
                {
                    Seeded = true;
                    MinX = MaxX = x;
                    MinY = MaxY = y;
                    MinW = MaxW = w;
                    MinH = MaxH = h;
                    return;
                }

                if (x < MinX) { MinX = x; }
                if (x > MaxX) { MaxX = x; }
                if (y < MinY) { MinY = y; }
                if (y > MaxY) { MaxY = y; }
                if (w < MinW) { MinW = w; }
                if (w > MaxW) { MaxW = w; }
                if (h < MinH) { MinH = h; }
                if (h > MaxH) { MaxH = h; }
            }
        }

        private static readonly Dictionary<(int W, int H), Stats> _byShape = new();

        private static readonly int[] _addrU = new int[16];
        private static readonly int[] _addrV = new int[16];
        private static int _addrBindings;
        private static int _addrSampled;

        public static void OnDraw(GpuChannel channel, ref ThreedClassState state)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                OnDrawImpl(channel, ref state);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP VPPROBE: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel, ref ThreedClassState state)
        {
            if (!_announced)
            {
                _announced = true;
                Logger.Info?.Print(LogClass.Gpu,
                    "MVPP VPPROBE: ON -- viewport vs render target, read-only, one line per shape per second.");
            }

            int rtW = 0, rtH = 0;

            channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
            {
                if (rtW == 0 && slot == 0 && rt != null)
                {
                    rtW = rt.Info.Width;
                    rtH = rt.Info.Height;
                }
            });

            if (rtW <= 0 || rtH <= 0)
            {
                return;
            }

            ref ViewportTransform transform = ref state.ViewportTransform[0];

            float scaleX = MathF.Abs(transform.ScaleX);
            float scaleY = MathF.Abs(transform.ScaleY);
            float vpX = transform.TranslateX - scaleX;
            float vpY = transform.TranslateY - scaleY;
            float vpW = scaleX * 2f;
            float vpH = scaleY * 2f;

            (int, int) key = (rtW, rtH);

            if (!_byShape.TryGetValue(key, out Stats st))
            {
                st = new Stats();
                _byShape[key] = st;
            }

            st.Draws++;
            st.Add(vpX, vpY, vpW, vpH);

            if (state.ViewportTransformEnable == 0)
            {
                st.TransformOff++;
            }

            if (vpX > 1f || vpX + vpW < rtW - 1f)
            {
                st.Narrow++;
            }

            if (vpY > 1f || vpY + vpH < rtH - 1f)
            {
                st.Short++;
            }

            ScissorState sc = state.ScissorState[0];
            bool scEnabled = sc.Enable;

            if (scEnabled)
            {
                st.ScissorOn++;
                st.AddScissor(sc.X1, sc.X2);

                if (sc.X1 > 0 || sc.X2 < rtW)
                {
                    st.ScissorCuts++;
                }
            }

            ScreenScissorState scr = state.ScreenScissorState;
            st.AddScreen(scr.Width, scr.Height);

            ViewportExtents ext = state.ViewportExtents[0];
            int clipX = ext.X;
            int clipW = ext.Width;

            if (clipX == 0 && clipW == 0)
            {
                st.ClipZero++;
            }
            else
            {
                st.AddClip(clipX, clipW);

                if (clipX > 0 || clipX + clipW < rtW)
                {
                    st.ClipCuts++;
                }
                else
                {
                    st.ClipFull++;
                }
            }

            if (transform.ScaleX < 0f)
            {
                st.NegScaleX++;
            }

            if (transform.UnpackSwizzleX() != ViewportSwizzle.PositiveX)
            {
                st.SwizzleXOdd++;
            }

            if ((state.MultisampleControl & 1) != 0)
            {
                st.A2c++;

                if (state.AlphaToCoverageDitherEnable)
                {
                    st.A2cDither++;
                }
            }

            if ((++_addrSampled & 15) == 0)
            {
                channel.TextureManager.MvppEnumerateGraphicsInputsWithSampler((stage, tex, smp) =>
                {
                    if (smp == null)
                    {
                        return;
                    }

                    int u = (int)smp.ProbeAddressU;
                    int v = (int)smp.ProbeAddressV;

                    if ((uint)u < 16) { _addrU[u]++; }
                    if ((uint)v < 16) { _addrV[v]++; }

                    _addrBindings++;
                });
            }

            long nowMs = Environment.TickCount64;

            if (nowMs < _windowMs)
            {
                return;
            }

            _windowMs = nowMs + 1000;

            foreach (KeyValuePair<(int W, int H), Stats> entry in _byShape)
            {
                Stats s = entry.Value;

                if (s.Draws == 0 || !s.Seeded)
                {
                    continue;
                }

                float leftMin = s.MinX;
                float rightMin = entry.Key.W - (s.MinX + s.MinW);

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP VPPROBE: rt {entry.Key.W}x{entry.Key.H} draws={s.Draws} narrow={s.Narrow} short={s.Short} " +
                    $"tfOff={s.TransformOff} vpW=[{s.MinW:F1}..{s.MaxW:F1}] vpH=[{s.MinH:F1}..{s.MaxH:F1}] " +
                    $"vpX=[{s.MinX:F1}..{s.MaxX:F1}] vpY=[{s.MinY:F1}..{s.MaxY:F1}] " +
                    $"margeG={leftMin:F1} margeD={rightMin:F1} " +
                    $"| sciOn={s.ScissorOn} sciCuts={s.ScissorCuts} sciX=[{s.SciMinX1}..{s.SciMaxX1}][{s.SciMinX2}..{s.SciMaxX2}] " +
                    $"scrW=[{s.ScrMinW}..{s.ScrMaxW}] scrH=[{s.ScrMinH}..{s.ScrMaxH}] " +
                    $"|| CLIP zero={s.ClipZero} full={s.ClipFull} CUTS={s.ClipCuts} " +
                    $"clipX=[{s.ClipMinX}..{s.ClipMaxX}] clipW=[{s.ClipMinW}..{s.ClipMaxW}] " +
                    $"MIRROR negScaleX={s.NegScaleX} swizzleXodd={s.SwizzleXOdd} || A2C={s.A2c} A2C_DITHER={s.A2cDither}");
                s.Draws = 0;
                s.Narrow = 0;
                s.Short = 0;
                s.TransformOff = 0;
                s.Seeded = false;
                s.ScissorOn = 0;
                s.ScissorCuts = 0;
                s.ScissorSeen = 0;
                s.ScrSeeded = false;
                s.ClipZero = 0;
                s.ClipFull = 0;
                s.ClipCuts = 0;
                s.ClipSeeded = false;
                s.NegScaleX = 0;
                s.SwizzleXOdd = 0;
                s.A2c = 0;
                s.A2cDither = 0;
            }

            if (_addrBindings > 0)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP ADDRPROBE: {_addrBindings} bindings echantillonnes | U: {DescribeModes(_addrU)} | V: {DescribeModes(_addrV)}");

                Array.Clear(_addrU);
                Array.Clear(_addrV);
                _addrBindings = 0;
            }
        }

        private static string DescribeModes(int[] counts)
        {
            System.Text.StringBuilder sb = new();

            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0)
                {
                    continue;
                }

                AddressMode m = (AddressMode)i;
                bool substitue = m is AddressMode.Clamp or AddressMode.MirrorClamp or AddressMode.MirrorClampToBorder;

                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(m).Append('=').Append(counts[i]);

                if (substitue)
                {
                    sb.Append("(SUBSTITUE)");
                }
            }

            return sb.Length > 0 ? sb.ToString() : "aucun";
        }

    }
}
