

using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Ryujinx.Ava.Systems
{
    public static class DlssUiSettings
    {
        private static readonly string[] _modeEnv = { "", "dlaa", "quality", "balanced", "performance" };

        public static readonly int[] PresetValues = { 11, 13, 12, 10, 6 };

        /// <summary>The environment name of a saved mode index, e.g. 2 -> "quality".</summary>
        public static string ModeName(int mode) => mode > 0 && mode < _modeEnv.Length ? _modeEnv[mode] : string.Empty;

        private static string FilePath => Path.Combine(AppDataManager.BaseDirPath, "dlss_mode.cfg");

        public static int Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] parts = File.ReadAllText(FilePath).Trim()
                        .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > 0 &&
                        int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode) &&
                        mode >= 0 && mode < _modeEnv.Length)
                    {
                        return mode;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to read DLSS UI setting: {ex.Message}");
            }

            return 0;
        }

        public static int LoadPresetValue()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] parts = File.ReadAllText(FilePath).Trim()
                        .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > 1 &&
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int preset) &&
                        preset > 0 && preset < 32)
                    {
                        return preset;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to read DLSS preset setting: {ex.Message}");
            }

            return 0;
        }

        public static int LoadFgMultiplier()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] parts = File.ReadAllText(FilePath).Trim()
                        .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > 2 &&
                        int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fg) &&
                        fg >= 1 && fg <= 3)
                    {
                        return fg;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to read DLSS FG setting: {ex.Message}");
            }

            return 1;
        }

        public static bool LoadForceMips()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] parts = File.ReadAllText(FilePath).Trim()
                        .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    return parts.Length > 3 && parts[3] == "1";
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to read mip-generation setting: {ex.Message}");
            }

            return false;
        }

        public static int LoadSharpenLevel()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] parts = File.ReadAllText(FilePath).Trim()
                        .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > 4 &&
                        int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sharpen))
                    {
                        return Math.Clamp(sharpen, 0, 100);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to read DLSS sharpening setting: {ex.Message}");
            }

            return 0;
        }

        private static bool LoadFlag(int index)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] parts = File.ReadAllText(FilePath).Trim()
                        .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    return parts.Length > index && parts[index] == "1";
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to read DLSS UI flag {index}: {ex.Message}");
            }

            return false;
        }

        public static bool LoadDofSkip() => LoadFlag(5);

        /// <summary>
        /// AMD FSR frame generation. Unlike the DLSS settings this one does not depend on the DLSS
        /// scaling filter being picked: FidelityFX supplies the swapchain and interpolates whatever
        /// is presented into it, upscaled or not.
        /// </summary>
        public static bool LoadFsrFg() => LoadFlag(6);

        /// <summary>
        /// FPS overlay drawn into the presented image. Unlike the settings above it needs no
        /// restart: the renderer reads it every frame.
        /// </summary>
        public static bool LoadFpsOverlay() => LoadFlag(7);

        /// <summary>
        /// Which upscaler the saved mode drives: false for DLSS, true for AMD FSR 3.1. FSR 3.1 runs
        /// through the same pipeline as DLSS, so it shares the mode field and only this flag differs.
        /// </summary>
        public static bool LoadFsr3Upscaler() => LoadFlag(8);

        /// <summary>
        /// Rewrites only the FPS overlay field, for the View menu toggle, which has none of the other
        /// settings at hand. Missing earlier fields are filled with what their loaders fall back to.
        /// </summary>
        public static void SaveFpsOverlay(bool enabled)
        {
            try
            {
                string[] defaults = ["0", "0", "1", "0", "0", "0", "0", "0", "0"];
                string[] parts = File.Exists(FilePath)
                    ? File.ReadAllText(FilePath).Trim().Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    : [];

                string[] fields = new string[Math.Max(defaults.Length, parts.Length)];

                for (int i = 0; i < fields.Length; i++)
                {
                    fields[i] = i < parts.Length ? parts[i] : defaults[i];
                }

                fields[7] = enabled ? "1" : "0";

                File.WriteAllText(FilePath, string.Join(' ', fields));

                Logger.Info?.Print(LogClass.Application, $"FPS overlay {(enabled ? "enabled" : "disabled")} (View menu) -> {FilePath}");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to write the FPS overlay setting: {ex.Message}");
            }
        }

        public static void Save(int mode, int presetValue, int fgMultiplier, bool forceMips, int sharpenLevel, bool dofSkipFlag, bool fsrFgFlag, bool fpsOverlayFlag, bool fsr3UpscalerFlag)
        {
            try
            {
                File.WriteAllText(FilePath, string.Create(CultureInfo.InvariantCulture, $"{mode} {presetValue} {fgMultiplier} {(forceMips ? 1 : 0)} {Math.Clamp(sharpenLevel, 0, 100)} {(dofSkipFlag ? 1 : 0)} {(fsrFgFlag ? 1 : 0)} {(fpsOverlayFlag ? 1 : 0)} {(fsr3UpscalerFlag ? 1 : 0)}"));

                Logger.Info?.Print(LogClass.Application,
                    $"DLSS UI saved: mode={mode} preset={presetValue} fg={fgMultiplier}x mips={(forceMips ? 1 : 0)} sharpen={sharpenLevel} xc2Stack={(dofSkipFlag ? 1 : 0)} fsrFg={(fsrFgFlag ? 1 : 0)} fpsOverlay={(fpsOverlayFlag ? 1 : 0)} upscaler={(fsr3UpscalerFlag ? "fsr3" : "dlss")} -> {FilePath}");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to write DLSS UI setting: {ex.Message}");
            }
        }

        private static readonly bool _envDump =
            Environment.GetEnvironmentVariable("RYUJINX_ENVDUMP") == "1";

        public static void DumpEnvironment(string moment)
        {
            if (!_envDump)
            {
                return;
            }

            try
            {
                var vars = new System.Collections.Generic.SortedDictionary<string, string>(StringComparer.Ordinal);

                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                {
                    string k = (string)e.Key;
                    if (k.StartsWith("RYUJINX_", StringComparison.Ordinal))
                    {
                        vars[k] = (string)e.Value;
                    }
                }

                Logger.Info?.Print(LogClass.Application, $"ENVDUMP ({moment}) : {vars.Count} variables RYUJINX_*");

                foreach (var kv in vars)
                {
                    Logger.Info?.Print(LogClass.Application, $"ENVDUMP   {kv.Key}={kv.Value}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"ENVDUMP failed: {ex.Message}");
            }
        }

        /// <summary>Every variable this profile sets, so a re-apply can clear the previous run's.</summary>
        private static readonly string[] _profileVariables =
        {
            "RYUJINX_DLSS",
            "RYUJINX_DLSS_AUTO_RES",
            "RYUJINX_DLSS_FG",
            "RYUJINX_DLSS_FG_FRAMES",
            "RYUJINX_DLSS_MODE",
            "RYUJINX_DLSS_PRESET",
            "RYUJINX_DLSS_SCENECUT_MAX_MOTION",
            "RYUJINX_DLSS_SHARPEN",
            "RYUJINX_DLSS_SHARPEN_LEVEL",
            "RYUJINX_DLSS_SR",
            "RYUJINX_FSR_FG",
            "RYUJINX_FSR_FG_CONTEXT",
            "RYUJINX_FSR_UPSCALE",
            "RYUJINX_HDR_PQ",
            "RYUJINX_MVPP_ASPECTLOCK",
            "RYUJINX_MVPP_BORDERFIX",
            "RYUJINX_MVPP_CAP",
            "RYUJINX_MVPP_DEJITTER",
            "RYUJINX_MVPP_EDGE",
            "RYUJINX_MVPP_ELECT_HYST",
            "RYUJINX_MVPP_ELECT_MAXSTEP",
            "RYUJINX_MVPP_ELECT_WARP",
            "RYUJINX_MVPP_FLIPY",
            "RYUJINX_MVPP_FORCEMIPS",
            "RYUJINX_MVPP_FORCEMIPS_LEVELS",
            "RYUJINX_MVPP_FORCEMIPS_REFRESH_MS",
            "RYUJINX_MVPP_FORCEMIPS_TRILINEAR",
            "RYUJINX_MVPP_MULTIADDR",
            "RYUJINX_MVPP_MULTIADDR_MAXROT",
            "RYUJINX_MVPP_NOLDR",
            "RYUJINX_MVPP_REPROJ",
            "RYUJINX_MVPP_SKYCALM",
            "RYUJINX_MVPP_SKYDRIFT",
            "RYUJINX_MVPP_SKYGRID",
            "RYUJINX_MVPP_SKYREJECT",
            "RYUJINX_MVPP_SKYROT",
            "RYUJINX_MVPP_SNAPFLOOR",
            "RYUJINX_MVPP_SNAPGUARD",
            "RYUJINX_MVPP_SNAPHOLD",
            "RYUJINX_MVPP_STATS_STRIDE",
            "RYUJINX_MVPP_VPFIFO",
            "RYUJINX_MVPP_VPSOLO",
        };

        // The profile variables that were already set before the profile first ran: a launcher's.
        // A re-apply leaves those alone, and a launcher that set RYUJINX_DLSS owns the whole profile.
        private static HashSet<string> _launcherVariables;
        private static bool _launcherOwnsProfile;

        /// <summary>
        /// Rebuilds the profile from the saved settings, replacing what the previous run set. Called at
        /// each game start, before the renderer reads it, so upscaler changes made in the settings
        /// apply to the next game without restarting the app.
        /// </summary>
        public static void Reapply()
        {
            if (_launcherVariables == null)
            {
                ApplyAtStartup();

                return;
            }

            if (_launcherOwnsProfile)
            {
                return;
            }

            foreach (string name in _profileVariables)
            {
                if (!_launcherVariables.Contains(name))
                {
                    Environment.SetEnvironmentVariable(name, null);
                }
            }

            ApplyAtStartup();
        }

        public static void ApplyAtStartup()
        {
            if (_launcherVariables == null)
            {
                _launcherVariables = new HashSet<string>();

                foreach (string name in _profileVariables)
                {
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                    {
                        _launcherVariables.Add(name);
                    }
                }

                _launcherOwnsProfile = _launcherVariables.Contains("RYUJINX_DLSS");
            }

            Ryujinx.Graphics.Vulkan.PerformanceOverlay.Enabled = LoadFpsOverlay();

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RYUJINX_MVPP_FORCEMIPS")) && LoadForceMips())
            {
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_FORCEMIPS", "1");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_FORCEMIPS_TRILINEAR", "1");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_FORCEMIPS_LEVELS", "8");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_FORCEMIPS_REFRESH_MS", "500");

                Logger.Info?.Print(LogClass.Application, "Runtime mipmap generation enabled (UI setting).");
            }

            // Frame generation is independent of upscaling, so this is applied before the checks
            // that gate the DLSS profile: the setting must hold with the scaling filter on anything,
            // DLSS included or not. An environment variable already set wins, so a launcher script
            // can still override the UI.
            bool fsrFg = LoadFsrFg();

            if (fsrFg && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RYUJINX_FSR_FG")))
            {
                Environment.SetEnvironmentVariable("RYUJINX_FSR_FG", "1");
                Environment.SetEnvironmentVariable("RYUJINX_FSR_FG_CONTEXT", "1");

                Logger.Info?.Print(LogClass.Application, "FSR frame generation enabled (UI setting).");
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RYUJINX_DLSS")))
            {
                DumpEnvironment("lancement par bat, profil UI ignore");

                return;
            }

            int mode = Load();

            Logger.Info?.Print(LogClass.Application,
                $"DLSS UI startup: read mode={mode} fg={LoadFgMultiplier()}x preset={LoadPresetValue()} mips={(LoadForceMips() ? 1 : 0)} from {FilePath}");

            if (mode <= 0 || mode >= _modeEnv.Length)
            {
                return;
            }

            Environment.SetEnvironmentVariable("RYUJINX_DLSS", "1");
            Environment.SetEnvironmentVariable("RYUJINX_DLSS_MODE", _modeEnv[mode]);
            Environment.SetEnvironmentVariable("RYUJINX_DLSS_SR", "1");

            // FSR 3.1 rides the DLSS pipeline with its own upscaler in place of Streamline's, and
            // keeps the render size fixed from startup: the mode scales the player's resolution
            // scale once rather than auto-res retuning it mid-game, which faults the GPU on Intel in
            // some games (Pokemon Shield) when render targets are rebuilt at a fractional scale.
            bool fsr3 = LoadFsr3Upscaler();

            if (fsr3)
            {
                Environment.SetEnvironmentVariable("RYUJINX_FSR_UPSCALE", "1");
                Environment.SetEnvironmentVariable("RYUJINX_DLSS_AUTO_RES", "0");

                Logger.Info?.Print(LogClass.Application, $"AMD FSR 3.1 upscaling enabled (UI setting), mode={_modeEnv[mode]}, render size fixed at startup.");
            }
            else
            {
                Environment.SetEnvironmentVariable("RYUJINX_DLSS_AUTO_RES", "1");
            }

            Environment.SetEnvironmentVariable("RYUJINX_MVPP_CAP", "1");
            Environment.SetEnvironmentVariable("RYUJINX_MVPP_REPROJ", "2");
            Environment.SetEnvironmentVariable("RYUJINX_MVPP_FLIPY", "1");
            Environment.SetEnvironmentVariable("RYUJINX_MVPP_VPFIFO", "1");
            Environment.SetEnvironmentVariable("RYUJINX_MVPP_SKYROT", "1");
            Environment.SetEnvironmentVariable("RYUJINX_MVPP_EDGE", "6");

            Environment.SetEnvironmentVariable("RYUJINX_MVPP_BORDERFIX", "1");

            Environment.SetEnvironmentVariable("RYUJINX_MVPP_SKYDRIFT", "1");
            Environment.SetEnvironmentVariable("RYUJINX_MVPP_SKYGRID", "1");

            Environment.SetEnvironmentVariable("RYUJINX_MVPP_STATS_STRIDE", "4");

            Environment.SetEnvironmentVariable("RYUJINX_MVPP_ELECT_HYST", "500");
            Environment.SetEnvironmentVariable("RYUJINX_MVPP_ELECT_WARP", "1");
            Environment.SetEnvironmentVariable("RYUJINX_DLSS_SCENECUT_MAX_MOTION", "0.01");

            bool dofSkip = LoadDofSkip();
            if (dofSkip)
            {
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_VPSOLO", "1");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_DEJITTER", "1");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_ASPECTLOCK", "1");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_SKYREJECT", "1");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_SNAPGUARD", "1");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_SKYCALM", "1");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_MULTIADDR", "1");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_MULTIADDR_MAXROT", "0.5");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_SNAPHOLD", "20");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_SNAPFLOOR", "0.5");
                Environment.SetEnvironmentVariable("RYUJINX_MVPP_NOLDR", "1");

                Environment.SetEnvironmentVariable("RYUJINX_MVPP_ELECT_MAXSTEP", "1000");

            }

            int fgMultiplier = LoadFgMultiplier();

            // The two frame generators cannot share a swapchain. FidelityFX creates its own, so
            // asking for both leaves DLSS-FG to decline at probe time; not arming it here keeps the
            // choice in one place instead of a race between two subsystems.
            // DLSS-FG takes its frame token from DLSS's own evaluate, which FSR 3.1 replaces.
            if (fgMultiplier > 1 && !fsrFg && !fsr3)
            {
                Environment.SetEnvironmentVariable("RYUJINX_DLSS_FG", "1");
                Environment.SetEnvironmentVariable("RYUJINX_DLSS_FG_FRAMES", (fgMultiplier - 1).ToString(CultureInfo.InvariantCulture));

                Environment.SetEnvironmentVariable("RYUJINX_HDR_PQ", "1");
            }

            int preset = LoadPresetValue();
            if (preset > 0)
            {
                Environment.SetEnvironmentVariable("RYUJINX_DLSS_PRESET", preset.ToString(CultureInfo.InvariantCulture));
            }

            int sharpen = LoadSharpenLevel();
            if (sharpen > 0)
            {
                Environment.SetEnvironmentVariable("RYUJINX_DLSS_SHARPEN", "1");
                Environment.SetEnvironmentVariable("RYUJINX_DLSS_SHARPEN_LEVEL", sharpen.ToString(CultureInfo.InvariantCulture));
            }

            DumpEnvironment("apres profil UI");

            Logger.Info?.Print(LogClass.Application,
                $"DLSS UI profile applied: mode={_modeEnv[mode]}, fg={fgMultiplier}x, " +
                $"preset={(preset > 0 ? preset.ToString() : "engine default (K)")}, sharpen={(sharpen > 0 ? sharpen.ToString() : "off")}, " +
                $"dofSkip={(dofSkip ? "on" : "off")} (no jitter).");
        }
    }
}
