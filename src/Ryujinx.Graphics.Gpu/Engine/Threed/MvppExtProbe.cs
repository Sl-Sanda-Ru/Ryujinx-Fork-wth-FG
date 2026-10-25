
using Ryujinx.Common.Logging;
using System;
using System.Numerics;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppExtProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_EXTPROBE") == "1";

        private const float FloorPos = 1f;
        private const float FloorRot = 0.01f;
        private const float CeilPos = 10f;
        private const float CeilRot = 0.5f;
        private const int MaxLinesPerSecond = 12;

        private static Matrix4x4 _m1, _m2;
        private static bool _has1, _has2Consec, _lastWasFresh;
        private static int _chain;
        private static int _lastTpSeq = int.MinValue;
        private static int _tpAge = 99;

        private static bool _pending;
        private static Matrix4x4 _pred, _held;
        private static float _pStepPos, _pStepRot;
        private static bool _pTpNear;

        private static long _wLogMs;
        private static int _wHolds, _wQualifies, _wResolus, _wSousPlancher, _wSurPlafond,
                           _wSansPaire, _wEnchaines, _wTp, _wInvRatees, _wChaines2;

        private static long _lineWindowMs;
        private static int _lineCount;

        public static void OnPresent(bool fresh, in Matrix4x4 m, int teleportSeq)
        {
            if (teleportSeq != _lastTpSeq)
            {
                _lastTpSeq = teleportSeq;
                _tpAge = 0;
            }
            else if (_tpAge < 99)
            {
                _tpAge++;
            }

            if (fresh)
            {
                if (_pending)
                {
                    Resolve(in m, _chain);
                    _pending = false;
                }

                _has2Consec = _has1 && _lastWasFresh;
                _m2 = _m1;
                _m1 = m;
                _has1 = true;
                _lastWasFresh = true;
                _chain = 0;
            }
            else
            {
                _chain++;
                _lastWasFresh = false;
                _wHolds++;

                if (_chain == 2)
                {
                    _wChaines2++;
                }

                if (_chain == 1)
                {
                    Qualify();
                }
                else
                {
                    _wEnchaines++;
                }
            }

            long now = Environment.TickCount64;

            if (now - _wLogMs >= 5000)
            {
                if (_wLogMs != 0 && _wHolds > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP extpred fenetre: holds={_wHolds} qualifies={_wQualifies} " +
                        $"resolus={_wResolus} sous-plancher={_wSousPlancher} " +
                        $"sur-plafond={_wSurPlafond} sans-paire={_wSansPaire} " +
                        $"enchaines={_wEnchaines} tp={_wTp} inv-ratees={_wInvRatees} " +
                        $"chaines2+={_wChaines2}");
                }

                _wLogMs = now;
                _wHolds = _wQualifies = _wResolus = _wSousPlancher = _wSurPlafond = 0;
                _wSansPaire = _wEnchaines = _wTp = _wInvRatees = _wChaines2 = 0;
            }
        }

        private static void Qualify()
        {
            if (!_has1 || !_has2Consec)
            {
                _wSansPaire++;

                return;
            }

            if (_tpAge <= 2)
            {
                _wTp++;

                return;
            }

            float stepRot = RotDelta(in _m1, in _m2);
            float stepPos = -1f;

            if (MvppFamHold.PosFromVp(in _m1, out Vector3 p1) &&
                MvppFamHold.PosFromVp(in _m2, out Vector3 p2))
            {
                stepPos = (p1 - p2).Length();
            }

            if ((stepPos < 0f || stepPos <= FloorPos) && stepRot <= FloorRot)
            {
                _wSousPlancher++;

                return;
            }

            if (stepPos > CeilPos || stepRot > CeilRot)
            {
                _wSurPlafond++;

                return;
            }

            if (!Matrix4x4.Invert(_m2, out Matrix4x4 inv2))
            {
                _wInvRatees++;

                return;
            }

            _pred = Matrix4x4.Multiply(Matrix4x4.Multiply(_m1, inv2), _m1);
            _held = _m1;
            _pStepPos = stepPos;
            _pStepRot = stepRot;
            _pTpNear = false;
            _pending = true;
            _wQualifies++;
        }

        private static void Resolve(in Matrix4x4 real, int chainLen)
        {
            _wResolus++;

            float errErot = RotDelta(in _pred, in real);
            float errArot = RotDelta(in _held, in real);

            string errEpos = "?", errApos = "?";

            if (MvppFamHold.PosFromVp(in real, out Vector3 pr))
            {
                if (MvppFamHold.PosFromVp(in _pred, out Vector3 pe))
                {
                    errEpos = (pe - pr).Length().ToString("0.###");
                }

                if (MvppFamHold.PosFromVp(in _held, out Vector3 pa))
                {
                    errApos = (pa - pr).Length().ToString("0.###");
                }
            }

            long now = Environment.TickCount64;

            if (now - _lineWindowMs >= 1000)
            {
                _lineWindowMs = now;
                _lineCount = 0;
            }

            if (++_lineCount <= MaxLinesPerSecond)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP extpred: errE-pos={errEpos} errE-rot={errErot:0.####} " +
                    $"errA-pos={errApos} errA-rot={errArot:0.####} " +
                    $"step-pos={(_pStepPos >= 0f ? _pStepPos.ToString("0.###") : "?")} " +
                    $"step-rot={_pStepRot:0.####} chaine={chainLen} tp={(_pTpNear ? "oui" : "non")}");
            }
        }

        private static float RotDelta(in Matrix4x4 a, in Matrix4x4 b)
        {
            float d = 0f;
            d = MathF.Max(d, MathF.Abs(a.M11 - b.M11));
            d = MathF.Max(d, MathF.Abs(a.M12 - b.M12));
            d = MathF.Max(d, MathF.Abs(a.M13 - b.M13));
            d = MathF.Max(d, MathF.Abs(a.M21 - b.M21));
            d = MathF.Max(d, MathF.Abs(a.M22 - b.M22));
            d = MathF.Max(d, MathF.Abs(a.M23 - b.M23));
            d = MathF.Max(d, MathF.Abs(a.M31 - b.M31));
            d = MathF.Max(d, MathF.Abs(a.M32 - b.M32));
            d = MathF.Max(d, MathF.Abs(a.M33 - b.M33));

            return d;
        }
    }
}
