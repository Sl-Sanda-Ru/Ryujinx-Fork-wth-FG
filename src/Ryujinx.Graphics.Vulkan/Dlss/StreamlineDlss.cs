
using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    public static unsafe class StreamlineDlss
    {
        private const string Interposer = "sl.interposer";
        private const uint FeatureDLSS = 0;

        private const uint BufferTypeDepth = 0;
        private const uint BufferTypeMotionVectors = 1;
        private const uint BufferTypeScalingInputColor = 3;
        private const uint BufferTypeScalingOutputColor = 4;

        private const uint BufferTypeBiasCurrentColorHint = 29;

        private const uint LifecycleValidUntilEvaluate = 2;

        private const byte ResourceTypeTex2d = 0;

        private const byte BoolFalse = 0;
        private const byte BoolTrue = 1;

        public struct DlssTexture
        {
            public IntPtr Image;
            public IntPtr View;
            public uint NativeFormat;
            public uint Layout;
            public uint Width;
            public uint Height;
        }

        public enum DlssMode : uint
        {
            Off = 0,
            MaxPerformance,
            Balanced,
            MaxQuality,
            UltraPerformance,
            UltraQuality,
            Dlaa,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StructType
        {
            public uint Data1;
            public ushort Data2;
            public ushort Data3;
            public byte B0, B1, B2, B3, B4, B5, B6, B7;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ViewportHandle
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public uint Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Resource
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public byte ResType;
            public IntPtr Native;
            public IntPtr Memory;
            public IntPtr View;
            public uint State;
            public uint Width;
            public uint Height;
            public uint NativeFormat;
            public uint MipLevels;
            public uint ArrayLayers;
            public ulong GpuVirtualAddress;
            public uint Flags;
            public uint Usage;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Extent
        {
            public uint Top, Left, Width, Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ResourceTag
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public IntPtr ResourcePtr;
            public uint BufferType;
            public uint Lifecycle;
            public Extent Extent;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Mat4
        {
            public float M00, M01, M02, M03;
            public float M10, M11, M12, M13;
            public float M20, M21, M22, M23;
            public float M30, M31, M32, M33;

            public static Mat4 Identity()
            {
                Mat4 m = default;
                m.M00 = m.M11 = m.M22 = m.M33 = 1f;

                return m;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Constants
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public Mat4 CameraViewToClip;
            public Mat4 ClipToCameraView;
            public Mat4 ClipToLensClip;
            public Mat4 ClipToPrevClip;
            public Mat4 PrevClipToClip;
            public float JitterOffsetX, JitterOffsetY;
            public float MvecScaleX, MvecScaleY;
            public float CameraPinholeOffsetX, CameraPinholeOffsetY;
            public float CameraPosX, CameraPosY, CameraPosZ;
            public float CameraUpX, CameraUpY, CameraUpZ;
            public float CameraRightX, CameraRightY, CameraRightZ;
            public float CameraFwdX, CameraFwdY, CameraFwdZ;
            public float CameraNear;
            public float CameraFar;
            public float CameraFOV;
            public float CameraAspectRatio;
            public float MotionVectorsInvalidValue;
            public byte DepthInverted;
            public byte CameraMotionIncluded;
            public byte MotionVectors3D;
            public byte Reset;
            public byte OrthographicProjection;
            public byte MotionVectorsDilated;
            public byte MotionVectorsJittered;
            public float MinRelativeLinearDepthObjectSeparation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DlssOptions
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public uint Mode;
            public uint OutputWidth;
            public uint OutputHeight;
            public float Sharpness;
            public float PreExposure;
            public float ExposureScale;
            public byte ColorBuffersHDR;
            public byte IndicatorInvertAxisX;
            public byte IndicatorInvertAxisY;
            public uint DlaaPreset;
            public uint QualityPreset;
            public uint BalancedPreset;
            public uint PerformancePreset;
            public uint UltraPerformancePreset;
            public uint UltraQualityPreset;
            public byte UseAutoExposure;
            public byte AlphaUpscalingEnabled;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DlssOptimalSettings
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public uint OptimalRenderWidth;
            public uint OptimalRenderHeight;
            public float OptimalSharpness;
            public uint RenderWidthMin;
            public uint RenderHeightMin;
            public uint RenderWidthMax;
            public uint RenderHeightMax;
        }

        [DllImport(Interposer, EntryPoint = "slGetNewFrameToken", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int slGetNewFrameToken(out IntPtr token, in uint frameIndex);

        [DllImport(Interposer, EntryPoint = "slSetConstants", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int slSetConstants(in Constants values, IntPtr frameToken, in ViewportHandle viewport);

        [DllImport(Interposer, EntryPoint = "slEvaluateFeature", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int slEvaluateFeature(uint feature, IntPtr frameToken, IntPtr* inputs, uint numInputs, IntPtr cmdBuffer);

        [DllImport(Interposer, EntryPoint = "slGetFeatureFunction", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int slGetFeatureFunction(uint feature, [MarshalAs(UnmanagedType.LPStr)] string functionName, out IntPtr function);

        private static delegate* unmanaged[Cdecl]<in ViewportHandle, in DlssOptions, int> _slDLSSSetOptions;
        private static delegate* unmanaged[Cdecl]<in DlssOptions, ref DlssOptimalSettings, int> _slDLSSGetOptimalSettings;
        private static bool _functionsBound;
        private static bool _loggedSizes;
        private static bool _loggedUndefinedFormat;

        public static IntPtr LastFrameToken { get; private set; }

        public static uint LastFrameIndex { get; private set; }

        private static StructType Guid(uint d1, ushort d2, ushort d3, byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7)
        {
            return new StructType
            {
                Data1 = d1, Data2 = d2, Data3 = d3,
                B0 = b0, B1 = b1, B2 = b2, B3 = b3, B4 = b4, B5 = b5, B6 = b6, B7 = b7,
            };
        }

        private static bool BindFunctions()
        {
            if (_functionsBound)
            {
                return true;
            }

            if (slGetFeatureFunction(FeatureDLSS, "slDLSSSetOptions", out IntPtr setOptions) != 0 ||
                slGetFeatureFunction(FeatureDLSS, "slDLSSGetOptimalSettings", out IntPtr getOptimal) != 0 ||
                setOptions == IntPtr.Zero || getOptimal == IntPtr.Zero)
            {
                Logger.Warning?.Print(LogClass.Gpu, "DLSS: could not bind DLSS feature functions.");

                return false;
            }

            _slDLSSSetOptions = (delegate* unmanaged[Cdecl]<in ViewportHandle, in DlssOptions, int>)setOptions;
            _slDLSSGetOptimalSettings = (delegate* unmanaged[Cdecl]<in DlssOptions, ref DlssOptimalSettings, int>)getOptimal;
            _functionsBound = true;

            if (!_loggedSizes)
            {
                _loggedSizes = true;
                Logger.Info?.Print(LogClass.Gpu, $"DLSS: struct sizes Resource={Marshal.SizeOf<Resource>()}(112) Constants={Marshal.SizeOf<Constants>()}(456) DLSSOptions={Marshal.SizeOf<DlssOptions>()}(88) Tag={Marshal.SizeOf<ResourceTag>()}(64)");
            }

            return true;
        }

        private static ViewportHandle MakeViewport(uint id)
        {
            ViewportHandle vp = default;
            vp.Type = Guid(0x171b6435, 0x9b3c, 0x4fc8, 0x99, 0x94, 0xfb, 0xe5, 0x25, 0x69, 0xaa, 0xa4);
            vp.Version = 1;
            vp.Value = id;

            return vp;
        }

        private static readonly uint? _presetEnv = ParsePresetEnv();

        private static readonly bool _presetEnvSet = _presetEnv.HasValue;

        private static readonly uint _upscalePreset = _presetEnv ?? 11u;

        private static uint? ParsePresetEnv()
        {
            string raw = System.Environment.GetEnvironmentVariable("RYUJINX_DLSS_PRESET");

            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            if (uint.TryParse(raw, out uint value) && value <= 13u)
            {
                return value;
            }

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                $"RYUJINX_DLSS_PRESET=\"{raw}\" is not a valid DLSS preset (0-13): using engine defaults.");

            return null;
        }

        private static readonly float _sharpness =
            float.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_DLSS_SHARPNESS"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float _s)
                ? System.Math.Clamp(_s, 0f, 1f) : 0f;

        private static readonly float _fixedExposure =
            float.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_DLSS_FIXED_EXPOSURE"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float _fe)
                ? _fe : 0f;

        private static bool _optionsLogged;

        public static bool SetOptions(uint viewportId, DlssMode mode, uint outputWidth, uint outputHeight, bool hdr)
        {
            if (!BindFunctions())
            {
                return false;
            }

            DlssOptions opt = default;
            opt.Type = Guid(0x6ac826e4, 0x4c61, 0x4101, 0xa9, 0x2d, 0x63, 0x8d, 0x42, 0x10, 0x57, 0xb8);
            opt.Version = 3;
            opt.Mode = (uint)mode;
            opt.OutputWidth = outputWidth;
            opt.OutputHeight = outputHeight;
            opt.PreExposure = 1.0f;
            opt.ExposureScale = _fixedExposure > 0f ? _fixedExposure : 1.0f;
            opt.Sharpness = _sharpness;
            opt.ColorBuffersHDR = hdr ? BoolTrue : BoolFalse;
            opt.UseAutoExposure = _fixedExposure > 0f ? BoolFalse : BoolTrue;

            const uint PresetK = 11;
            opt.DlaaPreset = _presetEnvSet ? _upscalePreset : PresetK;
            opt.QualityPreset = _upscalePreset;
            opt.BalancedPreset = _upscalePreset;
            opt.PerformancePreset = _upscalePreset;
            opt.UltraPerformancePreset = _upscalePreset;
            opt.UltraQualityPreset = _upscalePreset;

            if (!_optionsLogged)
            {
                _optionsLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS: upscale preset={_upscalePreset} (6=CNN/F, 11=transformer/K DLSS4), sharpness={_sharpness:F2}");
            }

            ViewportHandle vp = MakeViewport(viewportId);
            int r = _slDLSSSetOptions(in vp, in opt);
            if (r != 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS: slDLSSSetOptions failed ({r}).");

                return false;
            }

            return true;
        }

        public static bool GetOptimalRenderSize(DlssMode mode, uint outputWidth, uint outputHeight, out uint renderWidth, out uint renderHeight)
        {
            renderWidth = outputWidth;
            renderHeight = outputHeight;

            if (!BindFunctions())
            {
                return false;
            }

            DlssOptions opt = default;
            opt.Type = Guid(0x6ac826e4, 0x4c61, 0x4101, 0xa9, 0x2d, 0x63, 0x8d, 0x42, 0x10, 0x57, 0xb8);
            opt.Version = 3;
            opt.Mode = (uint)mode;
            opt.OutputWidth = outputWidth;
            opt.OutputHeight = outputHeight;

            DlssOptimalSettings settings = default;
            settings.Type = Guid(0xef1d0957, 0xfd58, 0x4df7, 0xb5, 0x04, 0x8b, 0x69, 0xd8, 0xaa, 0x6b, 0x76);
            settings.Version = 1;

            if (_slDLSSGetOptimalSettings(in opt, ref settings) != 0 || settings.OptimalRenderWidth == 0)
            {
                return false;
            }

            renderWidth = settings.OptimalRenderWidth;
            renderHeight = settings.OptimalRenderHeight;

            return true;
        }

        public static bool GetRenderRange(DlssMode mode, uint outputWidth, uint outputHeight,
            out uint minWidth, out uint minHeight, out uint maxWidth, out uint maxHeight)
        {
            minWidth = minHeight = maxWidth = maxHeight = 0;

            if (!BindFunctions())
            {
                return false;
            }

            DlssOptions opt = default;
            opt.Type = Guid(0x6ac826e4, 0x4c61, 0x4101, 0xa9, 0x2d, 0x63, 0x8d, 0x42, 0x10, 0x57, 0xb8);
            opt.Version = 3;
            opt.Mode = (uint)mode;
            opt.OutputWidth = outputWidth;
            opt.OutputHeight = outputHeight;

            DlssOptimalSettings settings = default;
            settings.Type = Guid(0xef1d0957, 0xfd58, 0x4df7, 0xb5, 0x04, 0x8b, 0x69, 0xd8, 0xaa, 0x6b, 0x76);
            settings.Version = 1;

            if (_slDLSSGetOptimalSettings(in opt, ref settings) != 0 || settings.OptimalRenderWidth == 0)
            {
                return false;
            }

            minWidth = settings.RenderWidthMin;
            minHeight = settings.RenderHeightMin;
            maxWidth = settings.RenderWidthMax;
            maxHeight = settings.RenderHeightMax;

            return true;
        }

        private static Resource MakeResource(in DlssTexture tex)
        {
            Resource r = default;
            r.Type = Guid(0x3a9d70cf, 0x2418, 0x4b72, 0x83, 0x91, 0x13, 0xf8, 0x72, 0x1c, 0x72, 0x61);
            r.Version = 1;
            r.ResType = ResourceTypeTex2d;
            r.Native = tex.Image;
            r.View = tex.View;
            r.State = tex.Layout;
            r.Width = tex.Width;
            r.Height = tex.Height;
            r.NativeFormat = tex.NativeFormat;
            r.MipLevels = 1;
            r.ArrayLayers = 1;

            return r;
        }

        private static ResourceTag MakeTag(Resource* resource, uint bufferType, uint width, uint height)
        {
            ResourceTag t = default;
            t.Type = Guid(0x4c6a5aad, 0xb445, 0x496c, 0x87, 0xff, 0x1a, 0xf3, 0x84, 0x5b, 0xe6, 0x53);
            t.Version = 1;
            t.ResourcePtr = (IntPtr)resource;
            t.BufferType = bufferType;
            t.Lifecycle = LifecycleValidUntilEvaluate;
            t.Extent = new Extent { Top = 0, Left = 0, Width = width, Height = height };

            return t;
        }

        private static Constants BuildConstants(uint outW, uint outH, uint renderW, uint renderH, bool reset, float jitterX, float jitterY)
        {
            Constants c = default;
            c.Type = Guid(0xdcd35ad7, 0x4e4a, 0x4bad, 0xa9, 0x0c, 0xe0, 0xc4, 0x9e, 0xb2, 0x3a, 0xfe);
            c.Version = 2;
            c.CameraViewToClip = Mat4.Identity();
            c.ClipToCameraView = Mat4.Identity();
            c.ClipToLensClip = Mat4.Identity();
            c.ClipToPrevClip = Mat4.Identity();
            c.PrevClipToClip = Mat4.Identity();
            c.JitterOffsetX = jitterX;
            c.JitterOffsetY = jitterY;
            c.MvecScaleX = renderW != 0 ? 1.0f / renderW : 1f;
            c.MvecScaleY = renderH != 0 ? 1.0f / renderH : 1f;
            c.CameraUpY = 1f;
            c.CameraRightX = 1f;
            c.CameraFwdZ = 1f;
            c.CameraNear = 0.1f;
            c.CameraFar = 10000f;
            c.CameraFOV = 1.0f;
            c.CameraAspectRatio = outH != 0 ? (float)outW / outH : 1.7777f;
            c.MotionVectorsInvalidValue = 0f;
            c.DepthInverted = BoolFalse;
            c.CameraMotionIncluded = BoolTrue;
            c.MotionVectors3D = BoolFalse;
            c.Reset = reset ? BoolTrue : BoolFalse;
            c.OrthographicProjection = BoolFalse;
            c.MotionVectorsDilated = BoolFalse;
            c.MotionVectorsJittered = BoolFalse;
            c.MinRelativeLinearDepthObjectSeparation = 40f;

            return c;
        }

        public static bool Evaluate(
            IntPtr cmdBuffer,
            uint viewportId,
            uint frameIndex,
            bool reset,
            float jitterX,
            float jitterY,
            in DlssTexture input,
            in DlssTexture output,
            in DlssTexture depth,
            in DlssTexture motion,
            in DlssTexture biasMask = default,
            bool hasBiasMask = false)
        {
            if (!_functionsBound)
            {
                return false;
            }

            bool useBias = hasBiasMask && biasMask.NativeFormat != 0;

            if (input.NativeFormat == 0 || output.NativeFormat == 0 || depth.NativeFormat == 0 || motion.NativeFormat == 0)
            {
                if (!_loggedUndefinedFormat)
                {
                    _loggedUndefinedFormat = true;
                    Logger.Warning?.Print(LogClass.Gpu,
                        "DLSS: skipping evaluate -- a tagged resource has an undefined Vulkan format. " +
                        $"input={input.NativeFormat} ({input.Width}x{input.Height}), " +
                        $"output={output.NativeFormat} ({output.Width}x{output.Height}), " +
                        $"depth={depth.NativeFormat} ({depth.Width}x{depth.Height}), " +
                        $"motion={motion.NativeFormat} ({motion.Width}x{motion.Height}).");
                }

                return false;
            }

            if (slGetNewFrameToken(out IntPtr token, in frameIndex) != 0 || token == IntPtr.Zero)
            {
                Logger.Warning?.Print(LogClass.Gpu, "DLSS: slGetNewFrameToken failed.");

                return false;
            }

            LastFrameToken = token;
            LastFrameIndex = frameIndex;

            ViewportHandle vp = MakeViewport(viewportId);

            Constants constants = BuildConstants(output.Width, output.Height, motion.Width, motion.Height, reset, jitterX, jitterY);
            int rc = slSetConstants(in constants, token, in vp);
            if (rc != 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS: slSetConstants failed ({rc}).");

                return false;
            }

            Resource rInput = MakeResource(input);
            Resource rOutput = MakeResource(output);
            Resource rDepth = MakeResource(depth);
            Resource rMotion = MakeResource(motion);
            Resource rBias = useBias ? MakeResource(biasMask) : default;

            ResourceTag tInput = MakeTag(&rInput, BufferTypeScalingInputColor, input.Width, input.Height);
            ResourceTag tOutput = MakeTag(&rOutput, BufferTypeScalingOutputColor, output.Width, output.Height);
            ResourceTag tDepth = MakeTag(&rDepth, BufferTypeDepth, depth.Width, depth.Height);
            ResourceTag tMotion = MakeTag(&rMotion, BufferTypeMotionVectors, motion.Width, motion.Height);
            ResourceTag tBias = useBias ? MakeTag(&rBias, BufferTypeBiasCurrentColorHint, biasMask.Width, biasMask.Height) : default;

            IntPtr* inputs = stackalloc IntPtr[6];
            inputs[0] = (IntPtr)(&vp);
            inputs[1] = (IntPtr)(&tDepth);
            inputs[2] = (IntPtr)(&tMotion);
            inputs[3] = (IntPtr)(&tInput);
            inputs[4] = (IntPtr)(&tOutput);

            uint inputCount = 5;
            if (useBias)
            {
                inputs[5] = (IntPtr)(&tBias);
                inputCount = 6;
            }

            int re = slEvaluateFeature(FeatureDLSS, token, inputs, inputCount, cmdBuffer);
            if (re != 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS: slEvaluateFeature failed ({re}).");
                UiActive = false;

                return false;
            }

            UiActive = true;

            return true;
        }

        public static bool UiActive
        {
            get => Environment.TickCount64 - System.Threading.Interlocked.Read(ref _uiActiveAtMs) < 2000;
            set => System.Threading.Interlocked.Exchange(ref _uiActiveAtMs, value ? Environment.TickCount64 : 0);
        }

        private static long _uiActiveAtMs;
    }
}
