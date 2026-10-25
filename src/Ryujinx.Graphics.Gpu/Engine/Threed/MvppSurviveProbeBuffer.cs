
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppSurviveProbeBuffer
    {
        private static readonly int MaxDumps =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_SURVIVE_DUMPS"), out int maxDumps) && maxDumps > 0
                ? maxDumps
                : 3;
        private const int DumpIntervalMs = 15000;

        private static BufferHandle _handle = BufferHandle.Null;
        private static BufferHandle _dummyHandle = BufferHandle.Null;
        private static long _nextDumpMs = -1;
        private static int _dumpCount;

        public static BufferRange GetRange(GpuContext context)
        {
            if (_handle == BufferHandle.Null)
            {
                _handle = context.Renderer.CreateBuffer(MvppSurviveProbe.BufferSize);
                context.Renderer.Pipeline.ClearBuffer(_handle, 0, MvppSurviveProbe.BufferSize, 0);

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-SURVIVE: probe buffer created ({MvppSurviveProbe.BufferSize} bytes, " +
                    $"map {MvppSurviveProbe.MapStride}x{MvppSurviveProbe.MapHeight}, " +
                    $"binding {MvppSurviveProbe.HostBinding}) and cleared to zero.");
            }

            return new BufferRange(_handle, 0, MvppSurviveProbe.BufferSize, true);
        }

        public static BufferRange GetDummyRange(GpuContext context)
        {
            if (_dummyHandle == BufferHandle.Null)
            {
                _dummyHandle = context.Renderer.CreateBuffer(MvppSurviveProbe.BufferSize);

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-SURVIVE: dummy sink buffer created ({MvppSurviveProbe.BufferSize} bytes).");
            }

            return new BufferRange(_dummyHandle, 0, MvppSurviveProbe.BufferSize, true);
        }

        public static void MaybeDump(GpuContext context)
        {
            if (_handle == BufferHandle.Null || _dumpCount >= MaxDumps)
            {
                return;
            }

            long now = Environment.TickCount64;

            if (_nextDumpMs < 0)
            {
                _nextDumpMs = now + DumpIntervalMs;
                return;
            }

            if (now < _nextDumpMs)
            {
                return;
            }

            _nextDumpMs = now + DumpIntervalMs;
            _dumpCount++;

            context.Renderer.Pipeline.Barrier();

            long nonZero = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            byte[] pgmPixels = new byte[MvppSurviveProbe.MapStride * MvppSurviveProbe.MapHeight];
            byte[] rawCopy = new byte[MvppSurviveProbe.BufferSize];

            using (PinnedSpan<byte> data = context.Renderer.GetBufferData(_handle, 0, MvppSurviveProbe.BufferSize))
            {
                ReadOnlySpan<byte> bytes = data.Get();
                bytes.CopyTo(rawCopy);

                ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(bytes);

                for (int i = 0; i < words.Length; i++)
                {
                    if (words[i] != 0)
                    {
                        nonZero++;
                        pgmPixels[i] = 255;

                        int x = i & (MvppSurviveProbe.MapStride - 1);
                        int y = i >> 12;

                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            string bbox = maxX >= 0 ? $"x[{minX}..{maxX}] y[{minY}..{maxY}]" : "(empty)";

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"mvpp_survive_dump_{_dumpCount}.pgm");
            string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"mvpp_survive_dump_{_dumpCount}.bin");

            try
            {
                using (FileStream fs = File.Create(path))
                {
                    byte[] header = Encoding.ASCII.GetBytes(
                        $"P5\n{MvppSurviveProbe.MapStride} {MvppSurviveProbe.MapHeight}\n255\n");
                    fs.Write(header, 0, header.Length);
                    fs.Write(pgmPixels, 0, pgmPixels.Length);
                }

                File.WriteAllBytes(binPath, rawCopy);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP-SURVIVE: dump write failed: {ex.Message}");
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP-SURVIVE DUMP {_dumpCount}/{MaxDumps}: nonZero={nonZero}/{(long)MvppSurviveProbe.MapStride * MvppSurviveProbe.MapHeight} " +
                $"({100.0 * nonZero / (MvppSurviveProbe.MapStride * MvppSurviveProbe.MapHeight):F1}%) bbox={bbox} " +
                $"sceneDepthPin={MvppCameraCapture.SceneDepthHostWidth}x{MvppCameraCapture.SceneDepthHostHeight} " +
                $"-> {path} + raw .bin");

            context.Renderer.Pipeline.ClearBuffer(_handle, 0, MvppSurviveProbe.BufferSize, 0);
        }
    }
}
