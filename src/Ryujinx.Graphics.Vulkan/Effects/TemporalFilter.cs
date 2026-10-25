
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Silk.NET.Vulkan;
using System;
using System.Globalization;
using Format = Ryujinx.Graphics.GAL.Format;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;

namespace Ryujinx.Graphics.Vulkan.Effects
{
    internal class TemporalFilter : IPostProcessingEffect
    {
        public static readonly bool IsEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_TAA") is "1" or "true" or "TRUE" or "True";

        private const float MaxMotion = 32f;
        private const int CounterCount = 9;

        private static readonly float HistoryBlend = ParseBlend();
        private static readonly float MotionSign = ParseSign();
        private static readonly float ClampGamma = ParseClamp();
        private static readonly int MotionDownscale = ParseDownscale();

        private readonly VulkanRenderer _renderer;
        private readonly PipelineHelperShader _pipeline;
        private readonly PipelineHelperShader _motionPipeline;
        private ISampler _sampler;
        private ShaderCollection _program;
        private ShaderCollection _downsampleProgram;
        private ShaderCollection _motionProgram;
        private ShaderCollection _motionFilterProgram;
        private BufferHandle _sceneChangeBuffer;

        private TextureView _output;

        private TextureView _history0;
        private TextureView _history1;
        private bool _readFromHistory0;
        private bool _hasHistory;

        private TextureView _colorHalf0;
        private TextureView _colorHalf1;
        private TextureView _motion;
        private TextureView _motionFiltered;
        private bool _readColorHalf0;
        private bool _hasPrev;
        private int _motionWidth;
        private int _motionHeight;
        private float _mvScale;

        private bool _activeLogged;

        public TemporalFilter(VulkanRenderer renderer, Device device)
        {
            _renderer = renderer;
            _pipeline = new PipelineHelperShader(renderer, device);
            _motionPipeline = new PipelineHelperShader(renderer, device);

            Initialize();
        }

        private static float ParseBlend()
        {
            string value = Environment.GetEnvironmentVariable("RYUJINX_TAA_BLEND");

            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float blend) &&
                blend >= 0f && blend <= 0.99f)
            {
                return blend;
            }

            return 0.935f;
        }

        private static float ParseSign()
        {
            return Environment.GetEnvironmentVariable("RYUJINX_TAA_MV_SIGN") is "-1" ? -1f : 1f;
        }

        private static float ParseClamp()
        {
            string value = Environment.GetEnvironmentVariable("RYUJINX_TAA_CLAMP");

            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float gamma) &&
                gamma >= 0.25f && gamma <= 8f)
            {
                return gamma;
            }

            return 1.25f;
        }

        private static int ParseDownscale()
        {
            return Environment.GetEnvironmentVariable("RYUJINX_TAA_MV_DOWNSCALE") switch
            {
                "1" => 1,
                "4" => 4,
                _ => 2,
            };
        }

        private void Initialize()
        {
            _pipeline.Initialize();
            _motionPipeline.Initialize();

            _sampler = _renderer.CreateSampler(SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            byte[] blendShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/Temporal.spv");
            ResourceLayout blendLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 3)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 5)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true)
                .Add(ResourceStages.Compute, ResourceType.Image, 1, true).Build();

            _program = _renderer.CreateProgramWithMinimalLayout([
                new ShaderSource(blendShader, ShaderStage.Compute, TargetLanguage.Spirv)
            ], blendLayout);

            byte[] downsampleShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/TaaDownsample.spv");
            ResourceLayout downsampleLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

            _downsampleProgram = _renderer.CreateProgramWithMinimalLayout([
                new ShaderSource(downsampleShader, ShaderStage.Compute, TargetLanguage.Spirv)
            ], downsampleLayout);

            byte[] motionShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/MotionVectors.spv");
            ResourceLayout motionLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.StorageBuffer, 0)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 3)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

            _motionProgram = _renderer.CreateProgramWithMinimalLayout([
                new ShaderSource(motionShader, ShaderStage.Compute, TargetLanguage.Spirv)
            ], motionLayout);

            byte[] filterShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/MotionFilter.spv");
            ResourceLayout filterLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.StorageBuffer, 0)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true)
                .Add(ResourceStages.Compute, ResourceType.Image, 1, true).Build();

            _motionFilterProgram = _renderer.CreateProgramWithMinimalLayout([
                new ShaderSource(filterShader, ShaderStage.Compute, TargetLanguage.Spirv)
            ], filterLayout);

            _sceneChangeBuffer = _renderer.BufferManager.CreateWithHandle(_renderer, CounterCount * sizeof(uint));
        }

        private static TextureCreateInfo MakeInfo(TextureCreateInfo b, int w, int h, Format format, int bytesPerPixel)
        {
            return new TextureCreateInfo(
                w,
                h,
                1,
                1,
                1,
                1,
                1,
                bytesPerPixel,
                format,
                b.DepthStencilMode,
                Target.Texture2D,
                b.SwizzleR,
                b.SwizzleG,
                b.SwizzleB,
                b.SwizzleA);
        }

        private void EnsureResources(TextureView view)
        {
            if (_output != null && _output.Width == view.Width && _output.Height == view.Height)
            {
                return;
            }

            _output?.Dispose();
            _history0?.Dispose();
            _history1?.Dispose();
            _colorHalf0?.Dispose();
            _colorHalf1?.Dispose();
            _motion?.Dispose();
            _motionFiltered?.Dispose();

            _motionWidth = Math.Max(1, view.Width / MotionDownscale);
            _motionHeight = Math.Max(1, view.Height / MotionDownscale);
            _mvScale = (float)view.Width / _motionWidth;

            _output = _renderer.CreateTexture(view.Info) as TextureView;
            _history0 = _renderer.CreateTexture(MakeInfo(view.Info, view.Width, view.Height, Format.R16G16B16A16Float, 8)) as TextureView;
            _history1 = _renderer.CreateTexture(MakeInfo(view.Info, view.Width, view.Height, Format.R16G16B16A16Float, 8)) as TextureView;
            _colorHalf0 = _renderer.CreateTexture(MakeInfo(view.Info, _motionWidth, _motionHeight, Format.R16G16B16A16Float, 8)) as TextureView;
            _colorHalf1 = _renderer.CreateTexture(MakeInfo(view.Info, _motionWidth, _motionHeight, Format.R16G16B16A16Float, 8)) as TextureView;
            _motion = _renderer.CreateTexture(MakeInfo(view.Info, _motionWidth, _motionHeight, Format.R16G16Float, 4)) as TextureView;
            _motionFiltered = _renderer.CreateTexture(MakeInfo(view.Info, _motionWidth, _motionHeight, Format.R16G16Float, 4)) as TextureView;

            _readFromHistory0 = true;
            _hasHistory = false;
            _readColorHalf0 = false;
            _hasPrev = false;
        }

        public TextureView Run(TextureView view, CommandBufferScoped cbs, int width, int height)
        {
            EnsureResources(view);

            if (!_activeLogged)
            {
                _activeLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"TAA: active (YCoCg clamp, blend {HistoryBlend:0.00}, mv sign {MotionSign:+0;-0}, clamp {ClampGamma:0.00}, " +
                    $"flow 1/{MotionDownscale} = {_motionWidth}x{_motionHeight}).");
            }

            TextureView curHalf = _readColorHalf0 ? _colorHalf1 : _colorHalf0;
            TextureView prevHalf = _readColorHalf0 ? _colorHalf0 : _colorHalf1;
            RunDownsamplePass(view, curHalf, cbs);

            if (_hasPrev)
            {
                RunMotionPass(curHalf, prevHalf, cbs);
                RunMotionFilterPass(cbs);
            }

            TextureView historyRead = _readFromHistory0 ? _history0 : _history1;
            TextureView historyWrite = _readFromHistory0 ? _history1 : _history0;

            _pipeline.SetCommandBuffer(cbs);
            _pipeline.SetProgram(_program);
            _pipeline.SetTextureAndSampler(ShaderStage.Compute, 1, view, _sampler);
            _pipeline.SetTextureAndSampler(ShaderStage.Compute, 3, historyRead, _sampler);
            _pipeline.SetTextureAndSampler(ShaderStage.Compute, 5, _motionFiltered, _sampler);

            ReadOnlySpan<float> paramsBuffer =
            [
                view.Width,
                view.Height,
                HistoryBlend,
                _hasHistory ? 1f : 0f,
                MotionSign,
                _hasPrev ? 1f : 0f,
                ClampGamma,
                _mvScale,
            ];
            int rangeSize = paramsBuffer.Length * sizeof(float);
            using ScopedTemporaryBuffer buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, rangeSize);
            buffer.Holder.SetDataUnchecked(buffer.Offset, paramsBuffer);

            _pipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _pipeline.SetImage(ShaderStage.Compute, 0, _output.GetView(FormatTable.ConvertRgba8SrgbToUnorm(view.Info.Format)));
            _pipeline.SetImage(1, historyWrite.GetImageView());

            int dispatchX = BitUtils.DivRoundUp(view.Width, 16);
            int dispatchY = BitUtils.DivRoundUp(view.Height, 16);
            _pipeline.DispatchCompute(dispatchX, dispatchY, 1);
            _pipeline.ComputeBarrier();

            _pipeline.Finish();

            _readFromHistory0 = !_readFromHistory0;
            _readColorHalf0 = !_readColorHalf0;
            _hasHistory = true;
            _hasPrev = true;

            return _output;
        }

        private void RunDownsamplePass(TextureView source, TextureView dst, CommandBufferScoped cbs)
        {
            _motionPipeline.SetCommandBuffer(cbs);
            _motionPipeline.SetProgram(_downsampleProgram);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 1, source, _sampler);

            ReadOnlySpan<float> p = [dst.Width, dst.Height];
            using ScopedTemporaryBuffer buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, p.Length * sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, p);

            _motionPipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _motionPipeline.SetImage(0, dst.GetImageView());

            _motionPipeline.DispatchCompute(BitUtils.DivRoundUp(dst.Width, 16), BitUtils.DivRoundUp(dst.Height, 16), 1);
            _motionPipeline.ComputeBarrier();
            _motionPipeline.Finish();
        }

        private void RunMotionPass(TextureView current, TextureView previous, CommandBufferScoped cbs)
        {
            _motionPipeline.SetCommandBuffer(cbs);
            _motionPipeline.SetProgram(_motionProgram);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 1, current, _sampler);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 3, previous, _sampler);

            ReadOnlySpan<float> p = [_motionWidth, _motionHeight, MaxMotion, 0f, 0f, 0f, 0f, 0f];
            using ScopedTemporaryBuffer buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, p.Length * sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, p);

            _motionPipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _motionPipeline.SetStorageBuffers([new BufferAssignment(0, new BufferRange(_sceneChangeBuffer, 0, CounterCount * sizeof(uint), true))]);
            _motionPipeline.SetImage(0, _motion.GetImageView());

            _motionPipeline.DispatchCompute(BitUtils.DivRoundUp(_motionWidth, 16), BitUtils.DivRoundUp(_motionHeight, 16), 1);
            _motionPipeline.ComputeBarrier();
            _motionPipeline.Finish();
        }

        private void RunMotionFilterPass(CommandBufferScoped cbs)
        {
            _motionPipeline.SetCommandBuffer(cbs);
            _motionPipeline.SetProgram(_motionFilterProgram);

            ReadOnlySpan<float> p = [_motionWidth, _motionHeight, MaxMotion, 0f, 0f, 0f, 0f, 0f];
            using ScopedTemporaryBuffer buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, p.Length * sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, p);

            _motionPipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _motionPipeline.SetStorageBuffers([new BufferAssignment(0, new BufferRange(_sceneChangeBuffer, 0, CounterCount * sizeof(uint), true))]);
            _motionPipeline.SetImage(0, _motionFiltered.GetImageView());
            _motionPipeline.SetImage(1, _motion.GetImageView());

            _motionPipeline.DispatchCompute(BitUtils.DivRoundUp(_motionWidth, 16), BitUtils.DivRoundUp(_motionHeight, 16), 1);
            _motionPipeline.ComputeBarrier();
            _motionPipeline.Finish();
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            _motionPipeline.Dispose();
            _program.Dispose();
            _downsampleProgram.Dispose();
            _motionProgram.Dispose();
            _motionFilterProgram.Dispose();
            _sampler.Dispose();
            _output?.Dispose();
            _history0?.Dispose();
            _history1?.Dispose();
            _colorHalf0?.Dispose();
            _colorHalf1?.Dispose();
            _motion?.Dispose();
            _motionFiltered?.Dispose();
            _renderer.BufferManager.Delete(_sceneChangeBuffer);
        }
    }
}
