
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppCamTrace
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAMTRACE") == "1";

        private const int MaxLines = 600;

        private const float JumpThreshold = 50f;

        private static int _lines;
        private static int _jumps;
        private static ulong _prevAddress;
        private static float _prevX, _prevY, _prevZ;
        private static bool _hasPrev;

        public static void Note(ulong address, float px, float py, float pz, float jx, float jy, bool deJittered)
        {
            if (!Enabled)
            {
                return;
            }

            bool opening = _lines < MaxLines;

            if (!opening)
            {
                float ddx = px - _prevX;
                float ddy = py - _prevY;
                float ddz = pz - _prevZ;
                float jump = MathF.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);

                _prevAddress = address;
                _prevX = px;
                _prevY = py;
                _prevZ = pz;

                if (jump > JumpThreshold)
                {
                    _jumps++;

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"MVPP camtrace JUMP #{_jumps}: addr={address:X10} " +
                        $"pos=[{px:0.####} {py:0.####} {pz:0.####}] step={jump:0.##}.");
                }

                return;
            }

            _lines++;

            float step = 0f;
            bool addrChanged = false;

            if (_hasPrev)
            {
                float dx = px - _prevX;
                float dy = py - _prevY;
                float dz = pz - _prevZ;
                step = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                addrChanged = address != _prevAddress;
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP camtrace {_lines,4}: addr={address:X10}{(addrChanged ? " *NEW*" : "      ")} " +
                $"pos=[{px:0.####} {py:0.####} {pz:0.####}] step={step:0.#####} " +
                $"jitter=({jx:0.######} {jy:0.######}){(deJittered ? " REMOVED" : " kept")}" +
                (_lines == MaxLines ? " (trace complete, no further lines)" : ""));

            _prevAddress = address;
            _prevX = px;
            _prevY = py;
            _prevZ = pz;
            _hasPrev = true;
        }
    }
}
