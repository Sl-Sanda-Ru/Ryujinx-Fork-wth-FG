
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Graphics.GAL
{
    public static class DlssCameraState
    {
        public static bool Enabled;

        private static readonly object _lock = new();
        private static Matrix4x4 _currentVp;
        private static bool _currentValid;

        public static Matrix4x4 PresentVp;
        public static bool PresentVpValid;

        public static float PresentJitterX;
        public static float PresentJitterY;

        private static readonly ConcurrentQueue<(Matrix4x4 Vp, bool Valid, float Jx, float Jy)> _presentRing = new();

        public static void PublishPresent(in Matrix4x4 vp, bool valid) => PublishPresent(in vp, valid, 0f, 0f);

        public static void PublishPresent(in Matrix4x4 vp, bool valid, float jx, float jy)
        {
            _presentRing.Enqueue((vp, valid, jx, jy));

            while (_presentRing.Count > 4)
            {
                _presentRing.TryDequeue(out _);
            }
        }

        public static void ConsumePresent()
        {
            if (_presentRing.TryDequeue(out (Matrix4x4 Vp, bool Valid, float Jx, float Jy) entry))
            {
                PresentVp = entry.Vp;
                PresentVpValid = entry.Valid;
                PresentJitterX = entry.Jx;
                PresentJitterY = entry.Jy;
            }
        }

        public static void PublishCurrent(in Matrix4x4 vp)
        {
            lock (_lock)
            {
                _currentVp = vp;
                _currentValid = true;
            }
        }

        public static bool SnapshotCurrent(out Matrix4x4 vp)
        {
            lock (_lock)
            {
                vp = _currentVp;

                return _currentValid;
            }
        }

        public static bool FifoEnabled;

        public static bool GuestDepthMinusOneToOne;

        public static int TeleportSeq;

        public static long RenderedFrameSeq;

        private static readonly Queue<Matrix4x4> _fifo = new();
        private static Matrix4x4 _fifoLast;
        private static bool _fifoHasLast;

        public static int StatPushes;
        public static int StatFresh;
        public static int StatHolds;
        public static int StatDrops;
        public static int StatMaxDepth;

        public static bool LastConsumeFresh;

        public static void PushOrdered(in Matrix4x4 vp, float jx, float jy) => PushOrdered(in vp);

        public static void PushOrdered(in Matrix4x4 vp)
        {
            _fifo.Enqueue(vp);
            StatPushes++;

            if (_fifo.Count > StatMaxDepth)
            {
                StatMaxDepth = _fifo.Count;
            }

            while (_fifo.Count > 4)
            {
                _fifo.Dequeue();
                StatDrops++;
            }
        }

        public static bool TryConsumeOrdered(out Matrix4x4 vp)
        {
            if (_fifo.TryDequeue(out vp))
            {
                _fifoLast = vp;
                _fifoHasLast = true;
                StatFresh++;
                LastConsumeFresh = true;

                return true;
            }

            vp = _fifoLast;
            StatHolds++;
            LastConsumeFresh = false;

            return _fifoHasLast;
        }
    }
}
