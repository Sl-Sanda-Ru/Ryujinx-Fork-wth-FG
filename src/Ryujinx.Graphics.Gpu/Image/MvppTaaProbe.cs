
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using System;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppTaaProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_TAAPROBE") == "1";

        private const int Slots = 24;

        private static readonly ulong[] _va = new ulong[Slots];
        private static readonly long[] _lastSeen = new long[Slots];
        private static int _count;

        private static long _frame;
        private static int _mainW;
        private static int _mainH;
        private static long _mainArea;

        private static readonly ulong[] _reported = new ulong[16];
        private static int _reportedN;
        private static int _announced;

        public static void AnnounceOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref _announced, 1) != 0)
            {
                return;
            }

            string v = Environment.GetEnvironmentVariable("RYUJINX_MVPP_TAAPROBE");
            Logger.Info?.Print(LogClass.Gpu,
                $"TAAPROBE gate check: RYUJINX_MVPP_TAAPROBE={(v ?? "<unset>")} -> enabled={Enabled}.");
        }

        public static void OnPresent()
        {
            if (Enabled)
            {
                _frame++;
            }
        }

        private static ulong Va(Texture t) => t.Range.GetSubRange(0).Address;

        private static int Find(ulong va)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_va[i] == va)
                {
                    return i;
                }
            }

            return -1;
        }

        public static void OnInput(Texture input, Texture target, ShaderStage stage)
        {
            if (!Enabled || input == null || target == null)
            {
                return;
            }

            long area = (long)target.Info.Width * target.Info.Height;

            if (area > _mainArea)
            {
                _mainArea = area;
                _mainW = target.Info.Width;
                _mainH = target.Info.Height;
            }

            if (target.Info.Width != _mainW || target.Info.Height != _mainH)
            {
                return;
            }

            ulong tva = Va(target);
            int ti = Find(tva);

            if (ti >= 0)
            {
                _lastSeen[ti] = _frame;
            }
            else if (_count < Slots)
            {
                _va[_count] = tva;
                _lastSeen[_count] = _frame;
                _count++;
            }

            if (input.Info.Width != _mainW || input.Info.Height != _mainH)
            {
                return;
            }

            int ii = Find(Va(input));

            if (ii < 0 || _lastSeen[ii] >= _frame)
            {
                return;
            }

            ulong key = Va(input) ^ (tva << 1);

            for (int i = 0; i < _reportedN; i++)
            {
                if (_reported[i] == key)
                {
                    return;
                }
            }

            if (_reportedN < _reported.Length)
            {
                _reported[_reportedN++] = key;
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"TAAPROBE PASSE TEMPORELLE #{_reportedN} (image {_frame}) : lit l'historique " +
                $"{input.Info.FormatInfo.Format}/{input.Info.Width}x{input.Info.Height} " +
                $"va=0x{Va(input):X} (ecrit a l'image {_lastSeen[ii]}) " +
                $"-> ecrit {target.Info.FormatInfo.Format}/{target.Info.Width}x{target.Info.Height} " +
                $"va=0x{tva:X} | etage {stage} | resolution principale {_mainW}x{_mainH}");
        }
    }
}
