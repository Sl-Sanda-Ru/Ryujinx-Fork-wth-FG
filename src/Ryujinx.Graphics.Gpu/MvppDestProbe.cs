
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu
{
    static class MvppDestProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DESTPROBE") == "1";

        private const int MaxWritesLogged = 400;
        private const int StackSampleWrites = 60;

        private static readonly HashSet<ulong> _presentedAddrs = new();
        private static int _frame;
        private static int _order;
        private static int _writesLogged;
        private static int _stackSamples;

        private static readonly HashSet<ulong> _trackedAddrs = new();
        private static int _trackedLogged;
        private const int MaxTrackedLogged = 200;

        public static bool Enabled => _enabled;

        public static bool IsPresentedAddress(ulong addr) => _presentedAddrs.Count != 0 && _presentedAddrs.Contains(addr);

        public static void AddTracked(ulong addr)
        {
            if (_enabled && addr != 0 && _trackedAddrs.Count < 32)
            {
                _trackedAddrs.Add(addr);
            }
        }

        public static int Frame => _frame;

        public static void RegisterPresented(Image.Texture tex)
        {
            if (!_enabled || tex == null)
            {
                return;
            }

            try
            {
                ulong addr = tex.Range.GetSubRange(0).Address;
                Format fmt = tex.Info.FormatInfo.Format;
                int w = tex.Info.Width, h = tex.Info.Height;

                bool isNew = _presentedAddrs.Add(addr);
                _frame++;
                _order = 0;

                if (isNew)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-DEST PRESENT f{_frame} addr@0x{addr:X} {fmt} {w}x{h} (backbuffers known={_presentedAddrs.Count})");
                }
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP dest probe: disabled after unexpected error: {e}");
            }
        }

        public static void OnModify(Image.Texture tex, bool viaModifying, bool bound)
        {
            if (!_enabled || tex == null || (_presentedAddrs.Count == 0 && _trackedAddrs.Count == 0))
            {
                return;
            }

            if (viaModifying && !bound)
            {
                return;
            }

            try
            {
                ulong addr = tex.Range.GetSubRange(0).Address;
                bool isPres = _presentedAddrs.Contains(addr);
                bool isTracked = _trackedAddrs.Contains(addr);
                if (!isPres && !isTracked)
                {
                    return;
                }

                if (isTracked && !isPres)
                {
                    if (_trackedLogged >= MaxTrackedLogged)
                    {
                        return;
                    }
                    _trackedLogged++;

                    string tcaller = _stackSamples < StackSampleWrites ? (" caller=" + Caller()) : "";
                    if (tcaller.Length != 0)
                    {
                        _stackSamples++;
                    }

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-DEST TRACKED-WRITE via={(viaModifying ? "Modifying(RT/3D)" : "Modified(copy/store)")} " +
                        $"{tex.Info.FormatInfo.Format} {tex.Info.Width}x{tex.Info.Height} @0x{addr:X}{tcaller}");
                    return;
                }

                _order++;
                if (_writesLogged >= MaxWritesLogged)
                {
                    return;
                }
                _writesLogged++;

                string caller = "";
                if (_stackSamples < StackSampleWrites)
                {
                    _stackSamples++;
                    caller = " caller=" + Caller();
                }

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-DEST WRITE f{_frame} #{_order} via={(viaModifying ? "Modifying(RT/3D)" : "Modified(copy/store)")} " +
                    $"{tex.Info.FormatInfo.Format} {tex.Info.Width}x{tex.Info.Height} @0x{addr:X}{caller}");
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP dest probe (modify): disabled after unexpected error: {e}");
            }
        }

        private static string Caller()
        {
            try
            {
                System.Diagnostics.StackTrace st = new(2, false);
                for (int i = 0; i < st.FrameCount && i < 8; i++)
                {
                    System.Reflection.MethodBase m = st.GetFrame(i)?.GetMethod();
                    string type = m?.DeclaringType?.Name;
                    if (type == null)
                    {
                        continue;
                    }

                    if (type != "Texture" && type != "MvppDestProbe" && !type.StartsWith("TextureGroup"))
                    {
                        return type + "." + m.Name;
                    }
                }

                return "?";
            }
            catch
            {
                return "?";
            }
        }
    }
}
