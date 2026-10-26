
using Ryujinx.Common.Logging;
using System;
using System.IO;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppDrawStepProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_DRAWSTEP") == "1";

        private static readonly int _startSeconds =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DRAWSTEP_START"), out int st) && st > 0 ? st : 45;

        private static readonly int _everyDraws =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DRAWSTEP_EVERY"), out int ev) && ev > 0 ? ev : 50;

        private static readonly int _fromDraw =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DRAWSTEP_FROM"), out int fdr) && fdr > 0 ? fdr : 0;

        private static readonly int _maxSteps =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DRAWSTEP_MAX"), out int mx) && mx > 0 ? mx : 25;

        private static readonly int _frames =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DRAWSTEP_FRAMES"), out int fr) && fr > 0 ? fr : 5;

        private static readonly int _gapMs =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DRAWSTEP_GAP"), out int gp) && gp > 0 ? gp : 3000;

        private static readonly int _vkey =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DRAWSTEP_VKEY"), out int vk) && vk > 0 ? vk : 0x79;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool TriggerHeld()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                return (GetAsyncKeyState(_vkey) & 0x8000) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static long _armMs;
        private static long _nextMs;
        private static bool _announced;
        private static bool _capturing;
        private static bool _done;
        private static int _frameNo;
        private static int _csDispatches, _csBindings, _csImages, _csStores;
        private static int _drawNo;
        private static int _stepNo;
        private static string _dir;

        public static void OnPresent(Image.Texture presented)
        {
            if (!_enabled || _done)
            {
                return;
            }

            try
            {
                long now = Environment.TickCount64;

                if (_armMs == 0)
                {
                    _armMs = now;
                }

                if (!_announced)
                {
                    _announced = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP DRAWSTEP: armed -- PRESS F10 when you SEE the artifact. Each press captures a " +
                        $"whole frame ({_maxSteps} snapshots, one every {_everyDraws} draws). {_frames} presses at " +
                        "most. The game will freeze briefly each time: that is the capture.");
                }

                if (_capturing)
                {
                    if (presented?.HostTexture != null)
                    {
                        try
                        {
                            using GAL.PinnedSpan<byte> pdata = presented.HostTexture.GetData();
                            ReadOnlySpan<byte> pbytes = pdata.Get();
                            string pname = $"ZFINAL_presente_{presented.Info.Width}x{presented.Info.Height}_{presented.Info.FormatInfo.Format}.bin";
                            File.WriteAllBytes(Path.Combine(_dir, pname), pbytes.ToArray());

                            Logger.Info?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: {pname} ({pbytes.Length} bytes) -- frame as presented.");
                        }
                        catch (Exception pe)
                        {
                            Logger.Warning?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: presented frame unreadable: {pe.Message}");
                        }
                    }

                    _capturing = false;
                    _nextMs = now + _gapMs;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP DRAWSTEP: frame {_frameNo}/{_frames} done -- {_stepNo} snapshots over {_drawNo} draws, in {_dir}. " +
                        $"COMPUTE: {_csDispatches} dispatches, {_csBindings} bindings, {_csImages} images, {_csStores} written.");

                    if (_frameNo >= _frames)
                    {
                        _done = true;
                        Logger.Info?.Print(LogClass.Gpu, "MVPP DRAWSTEP: all frames captured.");
                    }

                    return;
                }

                if (now < _nextMs || !TriggerHeld())
                {
                    return;
                }

                _frameNo++;
                _dir = Path.Combine("drawstep", $"image{_frameNo:D2}");
                Directory.CreateDirectory(_dir);
                _drawNo = 0;
                _stepNo = 0;
                _csDispatches = _csBindings = _csImages = _csStores = 0;
                _capturing = true;

                Logger.Info?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: capturing frame {_frameNo}/{_frames} -> {_dir}");
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: disabled after error: {e.Message}");
            }
        }

        public static void OnDispatch(Image.TextureManager tm, int gridX, int gridY, int gridZ, ulong shaderVa)
        {
            if (!_enabled || !_capturing || _stepNo >= _maxSteps || tm == null)
            {
                return;
            }

            try
            {
                int idx = 0;

                _csDispatches++;

                tm.MvppEnumerateComputeBindings((isImage, isStore, tex) =>
                {
                    _csBindings++;

                    if (isImage)
                    {
                        _csImages++;
                    }

                    if (isImage && isStore)
                    {
                        _csStores++;
                    }
                });

                tm.MvppEnumerateComputeBindings((isImage, isStore, tex) =>
                {
                    if (!isImage || !isStore || tex?.HostTexture == null || _stepNo >= _maxSteps)
                    {
                        return;
                    }

                    using GAL.PinnedSpan<byte> data = tex.HostTexture.GetData();
                    ReadOnlySpan<byte> bytes = data.Get();

                    string name = $"step{_stepNo:D3}_CS{idx}_apresDraw{_drawNo:D5}_grid{gridX}x{gridY}x{gridZ}_" +
                                  $"{tex.Info.Width}x{tex.Info.Height}_{tex.Info.FormatInfo.Format}.bin";
                    File.WriteAllBytes(Path.Combine(_dir, name), bytes.ToArray());
                    _stepNo++;
                    idx++;

                    Logger.Info?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: {name} (shader 0x{shaderVa:X}).");
                });
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: disabled after compute error: {e.Message}");
            }
        }

        public static void OnDraw(GpuChannel channel)
        {
            if (!_enabled || !_capturing || _stepNo >= _maxSteps)
            {
                return;
            }

            try
            {
                ++_drawNo;

                if (_drawNo < _fromDraw || (_drawNo % _everyDraws) != 0)
                {
                    return;
                }

                int localDraw = _drawNo;

                channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
                {
                    if (rt?.HostTexture == null || _stepNo >= _maxSteps)
                    {
                        return;
                    }

                    using GAL.PinnedSpan<byte> data = rt.HostTexture.GetData();
                    ReadOnlySpan<byte> bytes = data.Get();

                    string name = $"step{_stepNo:D3}_draw{localDraw:D5}_slot{slot}_{rt.Info.Width}x{rt.Info.Height}_{rt.Info.FormatInfo.Format}.bin";
                    File.WriteAllBytes(Path.Combine(_dir, name), bytes.ToArray());
                    _stepNo++;

                    Logger.Info?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: {name} ({bytes.Length} bytes).");
                });

                channel.TextureManager.MvppEnumerateGraphicsInputsStage((stage, tex) =>
                {
                    if (tex?.HostTexture == null || _stepNo >= _maxSteps)
                    {
                        return;
                    }

                    using GAL.PinnedSpan<byte> idata = tex.HostTexture.GetData();
                    ReadOnlySpan<byte> ibytes = idata.Get();

                    string iname = $"step{_stepNo:D3}_draw{localDraw:D5}_INPUT_s{stage}_{tex.Info.Width}x{tex.Info.Height}_{tex.Info.FormatInfo.Format}.bin";
                    File.WriteAllBytes(Path.Combine(_dir, iname), ibytes.ToArray());
                    _stepNo++;

                    Logger.Info?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: {iname} (sampled input).");
                });
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: disabled after read error: {e.Message}");
            }
        }

        public static void OnDrawPost(GpuChannel channel)
        {
            if (!_enabled || !_capturing || _stepNo >= _maxSteps)
            {
                return;
            }

            if (_drawNo < _fromDraw || (_drawNo % _everyDraws) != 0)
            {
                return;
            }

            try
            {
                int localDraw = _drawNo;

                channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
                {
                    if (rt?.HostTexture == null || _stepNo >= _maxSteps)
                    {
                        return;
                    }

                    using GAL.PinnedSpan<byte> data = rt.HostTexture.GetData();
                    ReadOnlySpan<byte> bytes = data.Get();

                    string name = $"step{_stepNo:D3}_draw{localDraw:D5}_slot{slot}_POST_{rt.Info.Width}x{rt.Info.Height}_{rt.Info.FormatInfo.Format}.bin";
                    File.WriteAllBytes(Path.Combine(_dir, name), bytes.ToArray());
                    _stepNo++;

                    Logger.Info?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: {name} (after write).");
                });
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP DRAWSTEP: disabled after POST error: {e.Message}");
            }
        }
    }
}
