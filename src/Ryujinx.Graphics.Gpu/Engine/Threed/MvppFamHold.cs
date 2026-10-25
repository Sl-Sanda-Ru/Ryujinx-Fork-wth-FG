
using Ryujinx.Common.Logging;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppFamHold
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_FAMHOLD") == "1";

        private const int MaxBytesPerBuffer = 4096;
        private const int SampleStride = 8;
        private const int MaxLinesPerSecond = 12;

        private static readonly (string Nom, int Stage, int Slot, int Offset, bool T)[] _familles =
        {
            ("s4c3+000",  4, 3, 0x000, false),
            ("s0c6+000",  0, 6, 0x000, false),
            ("s0c6+040",  0, 6, 0x040, false),
            ("s0c4+000T", 0, 4, 0x000, true),
            ("s0c3+040",  0, 3, 0x040, false),
        };

        private static readonly object _lock = new();
        private static readonly Matrix4x4[] _cur = new Matrix4x4[5];
        private static readonly bool[] _curSampled = new bool[5];
        private static readonly Matrix4x4[] _prev = new Matrix4x4[5];
        private static readonly int[] _prevAge = new int[5];

        private static int _drawTick;
        private static int _pushed;

        private static long _wLogMs;
        private static int _wHolds, _wFrais, _wRien, _wSansVerif, _wSupprimees;
        private static readonly int[] _wParFamille = new int[5];

        private static long _lineWindowMs;
        private static int _lineCount;

        public static void NotePush()
        {
            System.Threading.Interlocked.Exchange(ref _pushed, 1);
        }

        public static void Sample(GpuChannel channel)
        {
            if (_drawTick++ % SampleStride != 0)
            {
                return;
            }

            for (int i = 0; i < _familles.Length; i++)
            {
                if (TryReadFamille(channel, i, out Matrix4x4 m))
                {
                    lock (_lock)
                    {
                        _cur[i] = m;
                        _curSampled[i] = true;
                    }
                }
            }
        }

        public static void OnPresent(bool fresh, in Matrix4x4 publiee)
        {
            bool pushed = System.Threading.Interlocked.Exchange(ref _pushed, 0) == 1;

            lock (_lock)
            {
                if (!fresh && !pushed)
                {
                    Evaluate(in publiee);
                }

                for (int i = 0; i < _familles.Length; i++)
                {
                    if (_curSampled[i])
                    {
                        _prev[i] = _cur[i];
                        _prevAge[i] = 1;
                        _curSampled[i] = false;
                    }
                    else if (_prevAge[i] > 0)
                    {
                        _prevAge[i]++;
                    }
                }

                long now = Environment.TickCount64;

                if (now - _wLogMs >= 5000)
                {
                    if (_wLogMs != 0 && (_wHolds > 0 || _wSupprimees > 0))
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP famhold fenetre: holds={_wHolds} frais-ailleurs={_wFrais} " +
                            $"rien={_wRien} sans-verif={_wSansVerif} supprimees={_wSupprimees} | " +
                            $"{_familles[0].Nom}={_wParFamille[0]} {_familles[1].Nom}={_wParFamille[1]} " +
                            $"{_familles[2].Nom}={_wParFamille[2]} {_familles[3].Nom}={_wParFamille[3]} " +
                            $"{_familles[4].Nom}={_wParFamille[4]}");
                    }

                    _wLogMs = now;
                    _wHolds = _wFrais = _wRien = _wSansVerif = _wSupprimees = 0;
                    Array.Clear(_wParFamille, 0, _wParFamille.Length);
                }
            }
        }

        private static void Evaluate(in Matrix4x4 publiee)
        {
            _wHolds++;

            int verifies = 0;
            int fraisIdx = -1;

            for (int i = 0; i < _familles.Length; i++)
            {
                if (!_curSampled[i] || _prevAge[i] != 1)
                {
                    continue;
                }

                verifies++;

                if (!_cur[i].Equals(_prev[i]) && LooksLikeVp(in _cur[i]))
                {
                    _wParFamille[i]++;

                    if (fraisIdx < 0)
                    {
                        fraisIdx = i;
                    }
                }
            }

            string ligne;

            if (verifies == 0)
            {
                _wSansVerif++;
                ligne = $"MVPP famhold: HOLD verifies=0/{_familles.Length} (aucune famille lisible cette image)";
            }
            else if (fraisIdx >= 0)
            {
                _wFrais++;

                string dist = "?";

                if (PosFromVp(in _cur[fraisIdx], out Vector3 pf) && PosFromVp(in publiee, out Vector3 pp))
                {
                    dist = (pf - pp).Length().ToString("0.##");
                }

                ligne = $"MVPP famhold: HOLD frais=oui famille={_familles[fraisIdx].Nom} " +
                        $"dist={dist} verifies={verifies}/{_familles.Length}";
            }
            else
            {
                _wRien++;
                ligne = $"MVPP famhold: HOLD frais=non verifies={verifies}/{_familles.Length}";
            }

            long now = Environment.TickCount64;

            if (now - _lineWindowMs >= 1000)
            {
                _lineWindowMs = now;
                _lineCount = 0;
            }

            if (++_lineCount <= MaxLinesPerSecond)
            {
                Logger.Info?.Print(LogClass.Gpu, ligne);
            }
            else
            {
                _wSupprimees++;
            }
        }

        private static bool TryReadFamille(GpuChannel channel, int i, out Matrix4x4 m)
        {
            m = default;

            (_, int stage, int slot, int offset, bool t) = _familles[i];

            ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(stage, slot);
            int size = Math.Min(channel.BufferManager.GetGraphicsUniformBufferSize(stage, slot), MaxBytesPerBuffer);

            if (address == 0 || address == ulong.MaxValue || size < offset + 64)
            {
                return false;
            }

            ReadOnlySpan<float> data;

            try
            {
                data = MemoryMarshal.Cast<byte, float>(channel.MemoryManager.Physical.GetSpan(address, size));
            }
            catch
            {
                return false;
            }

            int idx = offset / 4;

            if (idx + 16 > data.Length)
            {
                return false;
            }

            ReadOnlySpan<float> r = data.Slice(idx, 16);

            for (int k = 0; k < 16; k++)
            {
                if (!float.IsFinite(r[k]))
                {
                    return false;
                }
            }

            m = t
                ? new Matrix4x4(r[0], r[4], r[8], r[12],
                                r[1], r[5], r[9], r[13],
                                r[2], r[6], r[10], r[14],
                                r[3], r[7], r[11], r[15])
                : new Matrix4x4(r[0], r[1], r[2], r[3],
                                r[4], r[5], r[6], r[7],
                                r[8], r[9], r[10], r[11],
                                r[12], r[13], r[14], r[15]);

            return true;
        }

        private static bool LooksLikeVp(in Matrix4x4 m)
        {
            Vector3 r0 = new(m.M11, m.M12, m.M13);
            Vector3 r1 = new(m.M21, m.M22, m.M23);
            Vector3 r2 = new(m.M31, m.M32, m.M33);
            Vector3 r3 = new(m.M41, m.M42, m.M43);

            float n3 = r3.Length();

            if (n3 < 0.9f || n3 > 1.1f)
            {
                return false;
            }

            float n0 = r0.Length(), n1 = r1.Length(), n2 = r2.Length();

            if (n0 < 1e-6f || n1 < 1e-6f || n2 < 1e-6f)
            {
                return false;
            }

            if (MathF.Abs(Vector3.Dot(r0, r1)) / (n0 * n1) > 0.05f ||
                MathF.Abs(Vector3.Dot(r0, r3)) / n0 > 0.05f ||
                MathF.Abs(Vector3.Dot(r1, r3)) / n1 > 0.05f)
            {
                return false;
            }

            if (Vector3.Cross(r2, r3).Length() / n2 > 0.05f)
            {
                return false;
            }

            return true;
        }

        internal static bool PosFromVp(in Matrix4x4 m, out Vector3 p)
        {
            p = default;

            float a0 = m.M11, b0 = m.M12, c0 = m.M13, d0 = m.M14;
            float a1 = m.M21, b1 = m.M22, c1 = m.M23, d1 = m.M24;
            float a3 = m.M41, b3 = m.M42, c3 = m.M43, d3 = m.M44;

            float det = a0 * (b1 * c3 - c1 * b3) - b0 * (a1 * c3 - c1 * a3) + c0 * (a1 * b3 - b1 * a3);

            if (MathF.Abs(det) < 1e-12f)
            {
                return false;
            }

            float dx = -d0 * (b1 * c3 - c1 * b3) - b0 * (-d1 * c3 + c1 * d3) + c0 * (-d1 * b3 + b1 * d3);
            float dy = a0 * (-d1 * c3 + c1 * d3) + d0 * (a1 * c3 - c1 * a3) + c0 * (-a1 * d3 + d1 * a3);
            float dz = a0 * (-b1 * d3 + d1 * b3) - b0 * (-a1 * d3 + d1 * a3) - d0 * (a1 * b3 - b1 * a3);

            p = new Vector3(dx / det, dy / det, dz / det);

            float scale = 1f + p.Length();
            float rw = MathF.Abs(a3 * p.X + b3 * p.Y + c3 * p.Z + d3);
            float rx = MathF.Abs(a0 * p.X + b0 * p.Y + c0 * p.Z + d0);

            if (rw > 1e-2f * scale || rx > 1e-1f * scale || !float.IsFinite(p.X + p.Y + p.Z))
            {
                p = default;

                return false;
            }

            return true;
        }
    }
}
