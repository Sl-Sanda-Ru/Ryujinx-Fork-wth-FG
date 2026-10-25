
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    public static class DlssIntegration
    {
        private static bool _enabled = IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_DLSS"));

        private static bool _srFractional = IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_DLSS_SR"));

        public static bool AutoResScale =
            _srFractional && IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_DLSS_AUTO_RES"));

        public static bool IsEnabled => _enabled;

        public static bool SrFractional => _srFractional;

        public static readonly bool BirthClearDisabled =
            IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_DLSS_NOBIRTHCLEAR"));

        public static readonly bool BirthClearAll =
            IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_BIRTHCLEAR_ALL"));

        public static bool FrameGenRequested =
            _enabled && IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_DLSS_FG"));

        /// <summary>
        /// Re-reads the fields above that come from the settings profile, so a changed upscaler
        /// setting applies at the next game start without restarting the app.
        /// </summary>
        internal static void ReloadProfileSettings()
        {
            {
                _enabled = IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_DLSS"));
            }
            {
                _srFractional = IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_DLSS_SR"));
            }
            {
                AutoResScale = _srFractional && IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_DLSS_AUTO_RES"));
            }
            {
                FrameGenRequested = _enabled && IsTruthy(Environment.GetEnvironmentVariable("RYUJINX_DLSS_FG"));
            }

            Mode = ParseQualityMode(Environment.GetEnvironmentVariable("RYUJINX_DLSS_MODE")) ?? DlssQualityMode.Native;
            _modeLogged = false;
        }

        public static readonly bool MvFilterEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_MV_FILTER") is not ("0" or "false" or "off");

        public static readonly bool MetricsEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_METRICS") is "1" or "true" or "on";

        public static readonly bool ForceDlss =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_FORCE_DLSS") is "1" or "true" or "on";

        public static readonly bool GpuTimingEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_GPUTIME") is "1" or "true" or "on";

        public static readonly bool OutputBarrierEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_OUTPUT_BARRIER") is not ("0" or "false" or "off");

        public static readonly string[] DeviceExtensions =
        {
            "VK_NVX_binary_import",
            "VK_NVX_image_view_handle",
            "VK_KHR_buffer_device_address",
            "VK_NV_optical_flow",
            "VK_KHR_synchronization2",
            "VK_KHR_format_feature_flags2",
        };

        public static Func<float> ResolutionScaleGetter;
        public static Action<float> ResolutionScaleSetter;

        public static float UserBaseResScale = 1f;

        private static bool _modeLogged;

        public static DlssQualityMode Mode { get; private set; } =
            ParseQualityMode(Environment.GetEnvironmentVariable("RYUJINX_DLSS_MODE")) ?? DlssQualityMode.Native;

        /// <summary>
        /// Changes the quality mode without restarting. The mode only sets the render scale, which
        /// <see cref="ApplyModeResolutionScale"/> re-derives from the player's own scale whenever the
        /// graphics config is refreshed, so the new mode takes effect when the next game starts.
        /// Turning the upscaler on or off is not covered: that still needs a fresh process.
        /// </summary>
        public static void SetMode(string modeName)
        {
            DlssQualityMode? mode = ParseQualityMode(modeName);

            if (mode.HasValue && mode.Value != Mode)
            {
                Mode = mode.Value;
                _modeLogged = false;
                Environment.SetEnvironmentVariable("RYUJINX_DLSS_MODE", modeName);
            }
        }

        /// <summary>Sharpening strength after the upscaler, 0 to 100; applies on the next frame.</summary>
        public static void SetSharpenLevel(int level) => DlssSharpenPass.Configure(level);

        public static float RenderScaleFactor(DlssQualityMode mode)
        {
            return mode switch
            {
                DlssQualityMode.Quality => 0.667f,
                DlssQualityMode.Balanced => 0.581f,
                DlssQualityMode.Performance => 0.5f,
                DlssQualityMode.UltraPerformance => 0.333f,
                _ => 1f,
            };
        }

        public static void ApplyModeResolutionScale()
        {
            if (!_enabled || ResolutionScaleGetter == null || ResolutionScaleSetter == null)
            {
                return;
            }

            float baseScale = ResolutionScaleGetter();
            if (baseScale <= 0f)
            {
                baseScale = 1f;
            }

            UserBaseResScale = baseScale;

            float factor = RenderScaleFactor(Mode);
            if (factor == 1f)
            {
                return;
            }

            float scaled = baseScale * factor;

            float aligned = _srFractional ? MathF.Max(0.1f, scaled) : MathF.Max(1f, MathF.Round(scaled));
            ResolutionScaleSetter(aligned);

            if (!_modeLogged)
            {
                _modeLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS: quality mode {Mode} -> resolution scale {baseScale:0.00} x {factor:0.00} = {scaled:0.00} -> {aligned:0.00} " +
                    (_srFractional ? "(fractional internal, mip-align padded)." : "(snapped to integer, mip-aligned)."));
            }
        }

        private static DlssQualityMode? ParseQualityMode(string value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "native" or "dlaa" => DlssQualityMode.Native,
                "quality" => DlssQualityMode.Quality,
                "balanced" => DlssQualityMode.Balanced,
                "performance" or "perf" => DlssQualityMode.Performance,
                "ultraperformance" or "ultraperf" or "ultra" => DlssQualityMode.UltraPerformance,
                _ => null,
            };
        }

        private static bool IsTruthy(string value)
        {
            return value is "1" or "true" or "TRUE" or "True";
        }
    }

    public enum DlssQualityMode
    {
        Native,
        Quality,
        Balanced,
        Performance,
        UltraPerformance,
    }
}
