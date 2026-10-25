
using Ryujinx.Graphics.GAL;
using System;
using System.Globalization;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    public static class DlssJitter
    {
        private static readonly bool _flag =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTER") is "1" or "true" or "on";

        public static bool Enabled => _flag && DlssIntegration.IsEnabled;

        private static readonly float _scale = ParseScale();

        public static float Scale => _scale;

        private static float ParseScale()
        {
            string value = Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTER_SCALE");

            if (value != null &&
                float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float scale) &&
                scale > 0f)
            {
                return scale;
            }

            return 1.0f;
        }

        private static readonly float _lodBias = ParseLodBias();

        public static float LodBias => _lodBias;

        private static float ParseLodBias()
        {
            string value = Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTER_LOD_BIAS");

            if (value != null &&
                float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float bias))
            {
                return bias;
            }

            return -0.5f;
        }

        private const uint SequenceLength = 16;

        private static uint _index;

        public static void Advance()
        {
            if (!Enabled)
            {
                DlssJitterState.Enabled = false;
                DlssJitterState.OffsetX = 0f;
                DlssJitterState.OffsetY = 0f;

                return;
            }

            _index = _index % SequenceLength + 1;
            DlssJitterState.Enabled = true;
            DlssJitterState.Inject =
                Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTER_DECLONLY") != "1";
            DlssJitterState.JitterUi =
                Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTER_UI") == "1";
            DlssJitterState.NoShadowJitter =
                Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTER_NOSHADOW") == "1";
            DlssJitterState.CamPassOnly =
                Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTER_CAMPASS") != "0";
            DlssJitterState.OffsetX = (Halton(_index, 2) - 0.5f) * _scale;
            DlssJitterState.OffsetY = (Halton(_index, 3) - 0.5f) * _scale;
            DlssJitterState.FrameId++;
        }

        private static float Halton(uint index, uint radix)
        {
            float result = 0f;
            float fraction = 1f;
            uint i = index;

            while (i > 0)
            {
                fraction /= radix;
                result += fraction * (i % radix);
                i /= radix;
            }

            return result;
        }
    }
}
