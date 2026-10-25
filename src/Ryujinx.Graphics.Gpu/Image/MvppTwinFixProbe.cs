
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppTwinFixProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_TWINFIX") == "1";

        private static bool _classifiedLogged;
        private static long _redirects;

        private static readonly HashSet<(ShaderStage, int)> _redirectTuples = new();

        public static bool NeedsRebind(Texture cached)
        {
            if (!Enabled || cached == null)
            {
                return false;
            }

            if (!MvppMvSyncProbe.IsMvBuffer(cached))
            {
                return false;
            }

            if (!MvppTwinMapProbe.TryClassify(out ulong skyVa, out _))
            {
                return false;
            }

            return cached.Range.GetSubRange(0).Address == skyVa;
        }

        public static Texture MaybeRedirect(Texture texture, ShaderStage stage, int handle)
        {
            if (!Enabled || texture == null || !MvppMvSyncProbe.IsMvBuffer(texture))
            {
                return texture;
            }

            if (!MvppTwinMapProbe.TryClassify(out ulong skyVa, out Texture materialTwin) || materialTwin == null)
            {
                return texture;
            }

            if (texture.Range.GetSubRange(0).Address != skyVa)
            {
                return texture;
            }

            if (!_classifiedLogged)
            {
                _classifiedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[TWINFIX] twins classified; redirect ARMED: reads of sky-twin@0x{skyVa:X} will be served by material-twin@0x{materialTwin.Range.GetSubRange(0).Address:X}");
            }

            _redirects++;

            bool isNew;
            lock (_redirectTuples)
            {
                isNew = _redirectTuples.Add((stage, handle));
            }

            if (isNew)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[TWINFIX] REDIRECT: stage={stage} handle=0x{handle:X} (tcb_{handle:X}) now reads the material twin | total redirected binds: {_redirects}");
            }

            return materialTwin;
        }
    }
}
