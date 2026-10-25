
using Ryujinx.Common.Logging;
using System;
using System.Numerics;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppProjAudit
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_PROJAUDIT") == "1";

        private const long IntervalMs = 1000;

        private static long _lastMs;

        public static void Audit(in Matrix4x4 vp, float camX, float camY, float camZ)
        {
            if (!Enabled)
            {
                return;
            }

            long now = Environment.TickCount64;

            if (now - _lastMs < IntervalMs)
            {
                return;
            }

            _lastMs = now;

            float fx = MathF.Sqrt(vp.M11 * vp.M11 + vp.M12 * vp.M12 + vp.M13 * vp.M13);
            float fy = MathF.Sqrt(vp.M21 * vp.M21 + vp.M22 * vp.M22 + vp.M23 * vp.M23);

            float n3 = MathF.Sqrt(vp.M41 * vp.M41 + vp.M42 * vp.M42 + vp.M43 * vp.M43);
            float a = MathF.Sqrt(vp.M31 * vp.M31 + vp.M32 * vp.M32 + vp.M33 * vp.M33);

            float dot23 = vp.M31 * vp.M41 + vp.M32 * vp.M42 + vp.M33 * vp.M43;
            float s = dot23 >= 0f ? 1f : -1f;

            float tz = s != 0f ? vp.M44 / s : 0f;
            float b = vp.M34 - a * tz;

            float aSigned = a * s;
            float near = MathF.Abs(aSigned - 1f) > 1e-6f ? b / (aSigned - 1f) : float.NaN;
            float far = MathF.Abs(aSigned + 1f) > 1e-6f ? b / (aSigned + 1f) : float.NaN;

            float wAtCam = vp.M41 * camX + vp.M42 * camY + vp.M43 * camZ + vp.M44;
            float camDist = MathF.Sqrt(camX * camX + camY * camY + camZ * camZ);

            float rt = float.NaN;

            if (Matrix4x4.Invert(vp, out Matrix4x4 inv))
            {
                Vector4 clip = new Vector4(0f, 0f, 0f, 1f);
                Vector4 world = Vector4.Transform(clip, inv);

                if (MathF.Abs(world.W) > 1e-20f)
                {
                    world /= world.W;
                    world.W = 1f;

                    Vector4 back = Vector4.Transform(world, vp);

                    if (MathF.Abs(back.W) > 1e-20f)
                    {
                        rt = MathF.Sqrt(back.X / back.W * (back.X / back.W) +
                                        back.Y / back.W * (back.Y / back.W)) * 1920f;
                    }
                }
            }
            else
            {
                rt = -1f;
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP proj audit: fx={fx:0.####} fy={fy:0.####} ratio={(fy > 1e-9f ? fx / fy : 0f):0.####} " +
                $"n3={n3:0.#####} A={a:0.######} s={s:+0;-0} B={b:0.####} " +
                $"near={near:0.#####} far={far:0.##} tz={tz:0.####} " +
                $"campos=[{camX:0.##} {camY:0.##} {camZ:0.##}] camDist={camDist:0.##} wAtCam={wAtCam:0.######} " +
                $"rt={rt:0.####} px.");
        }
    }
}
