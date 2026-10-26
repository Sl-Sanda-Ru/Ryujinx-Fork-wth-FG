using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    /// <summary>
    /// GPU image presentation window.
    /// </summary>
    public class Window
    {
        private readonly GpuContext _context;

        /// <summary>
        /// Texture presented on the window.
        /// </summary>
        private readonly struct PresentationTexture
        {
            /// <summary>
            /// Texture cache where the texture might be located.
            /// </summary>
            public TextureCache Cache { get; }

            /// <summary>
            /// Texture information.
            /// </summary>
            public TextureInfo Info { get; }

            /// <summary>
            /// Physical memory locations where the texture data is located.
            /// </summary>
            public MultiRange Range { get; }

            /// <summary>
            /// Texture crop region.
            /// </summary>
            public ImageCrop Crop { get; }

            /// <summary>
            /// Texture acquire callback.
            /// </summary>
            public Action<GpuContext, object> AcquireCallback { get; }

            /// <summary>
            /// Texture release callback.
            /// </summary>
            public Action<object> ReleaseCallback { get; }

            /// <summary>
            /// User defined object, passed to the various callbacks.
            /// </summary>
            public object UserObj { get; }

            /// <summary>
            /// </summary>
            public float JitterX { get; }
            public float JitterY { get; }

            public long JitterFrameId { get; }

            /// <summary>
            /// </summary>
            public System.Numerics.Matrix4x4 CameraVp { get; }
            public bool CameraVpValid { get; }

            /// <summary>
            /// Creates a new instance of the presentation texture.
            /// </summary>
            /// <param name="cache">Texture cache used to look for the texture to be presented</param>
            /// <param name="info">Information of the texture to be presented</param>
            /// <param name="range">Physical memory locations where the texture data is located</param>
            /// <param name="crop">Texture crop region</param>
            /// <param name="acquireCallback">Texture acquire callback</param>
            /// <param name="releaseCallback">Texture release callback</param>
            /// <param name="userObj">User defined object passed to the release callback, can be used to identify the texture</param>
            public PresentationTexture(
                TextureCache cache,
                TextureInfo info,
                MultiRange range,
                ImageCrop crop,
                Action<GpuContext, object> acquireCallback,
                Action<object> releaseCallback,
                object userObj,
                float jitterX,
                float jitterY,
                long jitterFrameId,
                System.Numerics.Matrix4x4 cameraVp,
                bool cameraVpValid)
            {
                Cache = cache;
                Info = info;
                Range = range;
                Crop = crop;
                AcquireCallback = acquireCallback;
                ReleaseCallback = releaseCallback;
                UserObj = userObj;
                JitterX = jitterX;
                JitterY = jitterY;
                JitterFrameId = jitterFrameId;
                CameraVp = cameraVp;
                CameraVpValid = cameraVpValid;
            }
        }

        private readonly ConcurrentQueue<PresentationTexture> _frameQueue;

        private int _framesAvailable;
        private long _mvppDepthLogMs;
        private long _mvppFifoLogMs;
        private long _jitTraceMs;
        private int _mvppTakes;
        private int _mvppTakeHits;
        private Image.Texture _mvppHeldSceneDepth;
        private int _mvppHeldSceneDepthSeq;
        private long _colorPubDiagMs;

        public bool IsFrameAvailable => _framesAvailable != 0;

        /// <summary>
        /// Creates a new instance of the GPU presentation window.
        /// </summary>
        /// <param name="context">GPU emulation context</param>
        public Window(GpuContext context)
        {
            _context = context;

            _frameQueue = new ConcurrentQueue<PresentationTexture>();
        }

        /// <summary>
        /// Enqueues a frame for presentation.
        /// This method is thread safe and can be called from any thread.
        /// When the texture is presented and not needed anymore, the release callback is called.
        /// It's an error to modify the texture after calling this method, before the release callback is called.
        /// </summary>
        /// <param name="pid">Process ID of the process that owns the texture pointed to by <paramref name="address"/></param>
        /// <param name="address">CPU virtual address of the texture data</param>
        /// <param name="width">Texture width</param>
        /// <param name="height">Texture height</param>
        /// <param name="stride">Texture stride for linear texture, should be zero otherwise</param>
        /// <param name="isLinear">Indicates if the texture is linear, normally false</param>
        /// <param name="gobBlocksInY">GOB blocks in the Y direction, for block linear textures</param>
        /// <param name="format">Texture format</param>
        /// <param name="bytesPerPixel">Texture format bytes per pixel (must match the format)</param>
        /// <param name="crop">Texture crop region</param>
        /// <param name="acquireCallback">Texture acquire callback</param>
        /// <param name="releaseCallback">Texture release callback</param>
        /// <param name="userObj">User defined object passed to the release callback</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="pid"/> is invalid</exception>
        /// <returns>True if the frame was added to the queue, false otherwise</returns>
        public bool EnqueueFrameThreadSafe(
            ulong pid,
            ulong address,
            int width,
            int height,
            int stride,
            bool isLinear,
            int gobBlocksInY,
            Format format,
            byte bytesPerPixel,
            ImageCrop crop,
            Action<GpuContext, object> acquireCallback,
            Action<object> releaseCallback,
            object userObj)
        {
            if (!_context.PhysicalMemoryRegistry.TryGetValue(pid, out PhysicalMemory physicalMemory))
            {
                return false;
            }

            FormatInfo formatInfo = new(format, 1, 1, bytesPerPixel, 4);

            TextureInfo info = new(
                0UL,
                width,
                height,
                1,
                1,
                1,
                1,
                stride,
                isLinear,
                gobBlocksInY,
                1,
                1,
                Target.Texture2D,
                formatInfo);

            int size = SizeCalculator.GetBlockLinearTextureSize(
                width,
                height,
                1,
                1,
                1,
                1,
                1,
                bytesPerPixel,
                gobBlocksInY,
                1,
                1).TotalSize;

            MultiRange range = new(address, (ulong)size);

            float jitterX = DlssJitterState.Enabled ? DlssJitterState.OffsetX : 0f;
            float jitterY = DlssJitterState.Enabled ? DlssJitterState.OffsetY : 0f;
            long jitterFrameId = DlssJitterState.Enabled ? DlssJitterState.FrameId : 0L;

            System.Numerics.Matrix4x4 cameraVp = default;
            bool cameraVpValid = DlssCameraState.Enabled && DlssCameraState.SnapshotCurrent(out cameraVp);
            if (DlssCameraState.Enabled)
            {
                Engine.Threed.MvppCameraCapture.OnFrameEnqueued();
            }

            Engine.Threed.MvppP2Probe.OnFrameEnqueued();

            Engine.Threed.MvppVelocityPass.OnFrameEnqueued();

            _frameQueue.Enqueue(new PresentationTexture(
                physicalMemory.TextureCache,
                info,
                range,
                crop,
                acquireCallback,
                releaseCallback,
                userObj,
                jitterX,
                jitterY,
                jitterFrameId,
                cameraVpValid ? cameraVp : default,
                cameraVpValid));

            return true;
        }

        /// <summary>
        /// Presents a texture on the queue.
        /// If the queue is empty, then no texture is presented.
        /// </summary>
        /// <param name="swapBuffersCallback">Callback method to call when a new texture should be presented on the screen</param>
        public void Present(Action swapBuffersCallback)
        {
            _context.AdvanceSequence();

            if (_frameQueue.TryDequeue(out PresentationTexture pt))
            {
                pt.AcquireCallback(_context, pt.UserObj);

                Image.Texture texture = pt.Cache.FindOrCreateTexture(null, TextureSearchFlags.WithUpscale, pt.Info, 0, range: pt.Range);

                pt.Cache.Tick();

                Engine.Threed.MvppPreSyncProbe.Before(texture);

                texture.SynchronizeMemory();

                Engine.Threed.MvppPreSyncProbe.After(texture);

                Engine.Threed.MvppLayoutProbe.Compare(pt.Info, texture);
                Engine.Threed.MvppPreSyncProbe.DumpGuest(texture);

                MvppDestProbe.RegisterPresented(texture);

                Engine.Threed.MvppRtDumpProbe.NotePresented(texture);

                Engine.Threed.MvppUiProbe.OnPresent();
                Engine.Threed.MvppDrawStepProbe.OnPresent(texture);
                Engine.Twod.MvppTwodProbe.OnPresent();
                Image.MvppCacheProbe.OnPresent();
                Image.MvppMvBufProbe.OnPresent();
                Image.MvppMvSyncProbe.OnPresent();
                Image.MvppWriterCensusProbe.OnPresent();
                Image.MvppTwinXferProbe.OnPresent();
                Memory.MvppPalProbe.OnPresent();
                Image.MvppTaaProbe.OnPresent();

                if (Image.MvppFeedbackProbe.Enabled)
                {
                    Image.MvppFeedbackProbe.OnFrameBoundary();
                }

                if (Image.MvppTraceProbe.Enabled)
                {
                    Image.MvppTraceProbe.OnFrameBoundary();
                }

                float cropScaleX = texture.EffectiveScaleX;
                float cropScaleY = texture.EffectiveScaleY;

                ImageCrop crop = new(
                    (int)(pt.Crop.Left * cropScaleX),
                    (int)MathF.Ceiling(pt.Crop.Right * cropScaleX),
                    (int)(pt.Crop.Top * cropScaleY),
                    (int)MathF.Ceiling(pt.Crop.Bottom * cropScaleY),
                    pt.Crop.FlipX,
                    pt.Crop.FlipY,
                    pt.Crop.IsStretched,
                    pt.Crop.AspectRatioX,
                    pt.Crop.AspectRatioY);

                if (texture.Info.Width > pt.Info.Width || texture.Info.Height > pt.Info.Height)
                {
                    int top = crop.Top;
                    int bottom = crop.Bottom;
                    int left = crop.Left;
                    int right = crop.Right;

                    if (top == 0 && bottom == 0)
                    {
                        bottom = Math.Min(texture.Info.Height, pt.Info.Height);
                    }

                    if (left == 0 && right == 0)
                    {
                        right = Math.Min(texture.Info.Width, pt.Info.Width);
                    }

                    crop = new ImageCrop(left, right, top, bottom, crop.FlipX, crop.FlipY, crop.IsStretched, crop.AspectRatioX, crop.AspectRatioY);
                }

                DlssJitterState.PresentX = pt.JitterX;
                DlssJitterState.PresentY = pt.JitterY;
                DlssJitterState.PresentFrameId = pt.JitterFrameId;

                if (DlssCameraState.FifoEnabled)
                {
                    bool orderedValid = DlssCameraState.TryConsumeOrdered(out System.Numerics.Matrix4x4 orderedVp);

                    float orderedJx = Engine.Threed.MvppSoloCamera.LastJitterX;
                    float orderedJy = Engine.Threed.MvppSoloCamera.LastJitterY;

                    _ = orderedJx;
                    _ = orderedJy;
                    DlssCameraState.PublishPresent(in orderedVp, orderedValid);

                    if (Engine.Threed.MvppFamHold.Enabled)
                    {
                        Engine.Threed.MvppFamHold.OnPresent(DlssCameraState.LastConsumeFresh, in orderedVp);
                    }

                    if (Engine.Threed.MvppExtProbe.Enabled)
                    {
                        Engine.Threed.MvppExtProbe.OnPresent(
                            DlssCameraState.LastConsumeFresh, in orderedVp, DlssCameraState.TeleportSeq);
                    }

                    if (Engine.Threed.MvppCameraCapture.JitTrace &&
                        Environment.TickCount64 - _jitTraceMs >= 1000)
                    {
                        _jitTraceMs = Environment.TickCount64;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"JITTRACE B (dequeued+published) : jx={orderedJx:0.000000} jy={orderedJy:0.000000} " +
                            $"valid={orderedValid} fresh={DlssCameraState.LastConsumeFresh}");
                    }

                    if (MvppDev.Enabled && Environment.TickCount64 - _mvppFifoLogMs >= 5000)
                    {
                        _mvppFifoLogMs = Environment.TickCount64;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP fifo: pushes {DlssCameraState.StatPushes}, fresh {DlssCameraState.StatFresh}, " +
                            $"holds {DlssCameraState.StatHolds}, drops {DlssCameraState.StatDrops}, max depth {DlssCameraState.StatMaxDepth}.");
                        DlssCameraState.StatPushes = 0;
                        DlssCameraState.StatFresh = 0;
                        DlssCameraState.StatHolds = 0;
                        DlssCameraState.StatDrops = 0;
                        DlssCameraState.StatMaxDepth = 0;
                    }
                }
                else
                {
                    DlssCameraState.PublishPresent(pt.CameraVp, pt.CameraVpValid);
                }

                Image.Texture presentDepth = null;
                if (DlssCameraState.Enabled)
                {
                    Image.Texture scene = Engine.Threed.MvppCameraCapture.TakeSceneDepth();

                    if (scene != null)
                    {
                        _mvppHeldSceneDepth = scene;
                        _mvppHeldSceneDepthSeq = scene.InvalidatedSequence;
                    }
                    else if (_mvppHeldSceneDepth != null && _mvppHeldSceneDepth.InvalidatedSequence != _mvppHeldSceneDepthSeq)
                    {
                        _mvppHeldSceneDepth = null;
                    }

                    presentDepth = _mvppHeldSceneDepth;
                }
                bool sceneDepthUsed = presentDepth != null;
                presentDepth ??= _context.LastPresentDepthStencil;

                _mvppTakes++;
                if (sceneDepthUsed)
                {
                    _mvppTakeHits++;
                }

                if (MvppDev.Enabled && DlssCameraState.Enabled && Environment.TickCount64 - _mvppDepthLogMs >= 5000)
                {
                    _mvppDepthLogMs = Environment.TickCount64;
                    Image.Texture d = presentDepth;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP depth probe: published={(d != null ? $"{d.Info.Width}x{d.Info.Height} scale={d.ScaleFactor} host={(d.HostTexture != null ? "ok" : "NULL")}" : "null")} " +
                        $"source={(sceneDepthUsed ? "scene-pass" : "last-bind fallback")}, " +
                        $"takes {_mvppTakeHits}/{_mvppTakes}, scaled draws {Engine.Threed.MvppCameraCapture.StatScaledDraws}, depth-bound {Engine.Threed.MvppCameraCapture.StatDepthBound}, " +
                        $"square-proj rejected {Engine.Threed.MvppCameraCapture.StatSquareRejects}.");
                    _mvppTakes = 0;
                    _mvppTakeHits = 0;
                    Engine.Threed.MvppCameraCapture.StatScaledDraws = 0;
                    Engine.Threed.MvppCameraCapture.StatDepthBound = 0;
                    Engine.Threed.MvppCameraCapture.StatSquareRejects = 0;
                }

                if (GAL.MvppColorSnapshot.Enabled)
                {
                    Image.Texture sceneColor = Engine.Threed.MvppCameraCapture.TakeSceneColor();
                    if (sceneColor?.HostTexture != null)
                    {
                        GAL.MvppColorSnapshot.SceneColorHost = sceneColor.HostTexture;
                        GAL.MvppColorSnapshot.FrameId = Engine.Threed.MvppCameraCapture.ColorCapFrameId;
                    }

                    if (Environment.TickCount64 - _colorPubDiagMs >= 3000)
                    {
                        _colorPubDiagMs = Environment.TickCount64;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP COLORCAP PUBLISH: takeColor={(sceneColor == null ? "null" : $"{sceneColor.Info.Width}x{sceneColor.Info.Height}")} " +
                            $"host={(sceneColor?.HostTexture != null ? $"{sceneColor.HostTexture.Width}x{sceneColor.HostTexture.Height}" : "null")} " +
                            $"bridge={(GAL.MvppColorSnapshot.SceneColorHost != null ? "set" : "null")}");
                    }
                }

                _context.Renderer.Window.Present(texture.HostTexture, presentDepth?.HostTexture, crop, swapBuffersCallback);

                pt.ReleaseCallback(pt.UserObj);
            }
        }

        /// <summary>
        /// Indicate that a frame on the queue is ready to be acquired.
        /// </summary>
        public void SignalFrameReady()
        {
            Interlocked.Increment(ref _framesAvailable);
        }

        /// <summary>
        /// Determine if any frames are available, and decrement the available count if there are.
        /// </summary>
        /// <returns>True if a frame is available, false otherwise</returns>
        public bool ConsumeFrameAvailable()
        {
            if (Interlocked.CompareExchange(ref _framesAvailable, 0, 0) != 0)
            {
                Interlocked.Decrement(ref _framesAvailable);

                return true;
            }

            return false;
        }
    }
}
