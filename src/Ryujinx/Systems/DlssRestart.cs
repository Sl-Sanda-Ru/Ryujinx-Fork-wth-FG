
using Ryujinx.Ava.Utilities;
using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Ryujinx.Ava.Systems
{
    internal static class DlssRestart
    {

        public static void RestartCold(bool applyUiMode)
        {
            string exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exe))
            {
                Logger.Warning?.Print(LogClass.Application,
                    "DLSS restart: could not resolve the executable path; restart skipped.");

                return;
            }

            ProcessStartInfo startInfo = new(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            };

            foreach (string arg in CommandLineState.Arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.Environment["RYUJINX_RESTART_DELAY_MS"] = "3000";

            if (applyUiMode)
            {
                foreach (string name in startInfo.Environment.Keys
                    .Where(k => k.StartsWith("RYUJINX_DLSS") || k.StartsWith("RYUJINX_MVPP_") || k == "RYUJINX_HDR_PQ" ||
                                k.StartsWith("RYUJINX_FSR_FG"))
                    .ToArray())
                {
                    startInfo.Environment.Remove(name);
                }
            }

            try
            {
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application,
                    $"DLSS restart: failed to relaunch ({ex.Message}); staying open.");

                return;
            }

            Logger.Info?.Print(LogClass.Application, "DLSS restart: relaunching cold (one slInit per process).");

            Thread watchdog = new(() =>
            {
                Thread.Sleep(5000);
                Logger.Warning?.Print(LogClass.Application,
                    "DLSS restart: clean exit hung >5s -- force-terminating the old process to complete the relaunch.");
                Process.GetCurrentProcess().Kill();
            })
            {
                IsBackground = true,
                Name = "DlssRestartWatchdog",
            };
            watchdog.Start();

            Environment.Exit(0);
        }
    }
}
