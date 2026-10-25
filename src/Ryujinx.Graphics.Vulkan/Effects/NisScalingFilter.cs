
using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Silk.NET.Vulkan;
using System;
using Extent2D = Ryujinx.Graphics.GAL.Extents2D;
using Format = Silk.NET.Vulkan.Format;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;

namespace Ryujinx.Graphics.Vulkan.Effects
{
    internal class NisScalingFilter : IScalingFilter
    {
        private const int BlockWidth = 32;
        private const int BlockHeight = 24;

        private const int ConfigFloats = 30;

        private readonly VulkanRenderer _renderer;
        private PipelineHelperShader _pipeline;
        private ISampler _sampler;
        private ShaderCollection _scalingProgram;
        private ShaderCollection _scalingProgramHdr;
        private ShaderCollection _scalingProgramPq;
        private Device _device;

        public float Level { get; set; } = 50f;

        public NisScalingFilter(VulkanRenderer renderer, Device device)
        {
            _device = device;
            _renderer = renderer;

            Initialize();
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            _scalingProgram.Dispose();
            _scalingProgramHdr.Dispose();
            _scalingProgramPq.Dispose();
            _sampler.Dispose();
        }

        public void Initialize()
        {
            _pipeline = new PipelineHelperShader(_renderer, _device);
            _pipeline.Initialize();

            byte[] scalingShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/NisScaling.spv");

            ResourceLayout scalingResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

            _sampler = _renderer.CreateSampler(SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            _scalingProgram = _renderer.CreateProgramWithMinimalLayout([
                new ShaderSource(scalingShader, ShaderStage.Compute, TargetLanguage.Spirv)
            ], scalingResourceLayout);

            byte[] scalingShaderHdr = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/NisScalingHdr.spv");

            ResourceLayout scalingResourceLayoutHdr = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 3)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

            _scalingProgramHdr = _renderer.CreateProgramWithMinimalLayout([
                new ShaderSource(scalingShaderHdr, ShaderStage.Compute, TargetLanguage.Spirv)
            ], scalingResourceLayoutHdr);

            byte[] scalingShaderPq = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/NisScalingPq.spv");

            _scalingProgramPq = _renderer.CreateProgramWithMinimalLayout([
                new ShaderSource(scalingShaderPq, ShaderStage.Compute, TargetLanguage.Spirv)
            ], scalingResourceLayoutHdr);
        }

        public bool IsResolutionSupported(int srcWidth, int srcHeight, int dstWidth, int dstHeight)
        {
            float scaleX = (float)srcWidth / dstWidth;
            float scaleY = (float)srcHeight / dstHeight;
            return scaleX >= 0.5f && scaleX <= 1.0f && scaleY >= 0.5f && scaleY <= 1.0f;
        }

        public void Run(
            TextureView view,
            CommandBufferScoped cbs,
            Auto<DisposableImageView> destinationTexture,
            Format format,
            int width,
            int height,
            Extent2D source,
            Extent2D destination,
            bool hdr = false,
            float paperWhite = 2.5f,
            float peak = 12.5f,
            float curve = 4.0f,
            float gamma = 2.2f,
            float blend = 0.5f,
            float whiten = 0.0f,
            bool pqOutput = false)
        {
            _pipeline.SetCommandBuffer(cbs);
            _pipeline.SetProgram(hdr ? (pqOutput ? _scalingProgramPq : _scalingProgramHdr) : _scalingProgram);
            _pipeline.SetTextureAndSampler(ShaderStage.Compute, 1, view, _sampler);

            uint inW = (uint)view.Width;
            uint inH = (uint)view.Height;
            uint outW = (uint)width;
            uint outH = (uint)height;

            float sharpness = Math.Clamp(Level * 0.01f, 0f, 1f);

            Span<float> cb = stackalloc float[ConfigFloats];
            BuildConfig(cb, sharpness, inW, inH, outW, outH);

            int rangeSize = ConfigFloats * sizeof(float);
            using ScopedTemporaryBuffer buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, rangeSize);
            buffer.Holder.SetDataUnchecked(buffer.Offset, cb);

            Span<float> hdrData = stackalloc float[] { paperWhite, peak, curve, gamma, blend, whiten };
            using ScopedTemporaryBuffer hdrBuffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, hdrData.Length * sizeof(float));
            hdrBuffer.Holder.SetDataUnchecked(hdrBuffer.Offset, hdrData);

            if (hdr)
            {
                _pipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range), new BufferAssignment(3, hdrBuffer.Range)]);
            }
            else
            {
                _pipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            }

            _pipeline.SetImage(0, destinationTexture);

            int dispatchX = ((int)outW + (BlockWidth - 1)) / BlockWidth;
            int dispatchY = ((int)outH + (BlockHeight - 1)) / BlockHeight;

            _pipeline.DispatchCompute(dispatchX, dispatchY, 1);
            _pipeline.ComputeBarrier();

            _pipeline.Finish();
        }

        private static void BuildConfig(Span<float> cb, float sharpness, uint inW, uint inH, uint outW, uint outH)
        {
            sharpness = Math.Clamp(sharpness, 0f, 1f);
            float sharpenSlider = sharpness - 0.5f;

            float maxScale = sharpenSlider >= 0.0f ? 1.25f : 1.75f;
            float minScale = sharpenSlider >= 0.0f ? 1.25f : 1.0f;
            float limitScale = sharpenSlider >= 0.0f ? 1.25f : 1.0f;

            float kDetectRatio = 2f * 1127f / 1024f;

            float kDetectThres = 64f / 1024f;
            float kMinContrastRatio = 2.0f;
            float kMaxContrastRatio = 10.0f;

            float kSharpStartY = 0.45f;
            float kSharpEndY = 0.9f;
            float kSharpStrengthMin = MathF.Max(0.0f, 0.4f + sharpenSlider * minScale * 1.2f);
            float kSharpStrengthMax = 1.6f + sharpenSlider * maxScale * 1.8f;
            float kSharpLimitMin = MathF.Max(0.1f, 0.14f + sharpenSlider * limitScale * 0.32f);
            float kSharpLimitMax = 0.5f + sharpenSlider * limitScale * 0.6f;

            float kRatioNorm = 1.0f / (kMaxContrastRatio - kMinContrastRatio);
            float kSharpScaleY = 1.0f / (kSharpEndY - kSharpStartY);
            float kSharpStrengthScale = kSharpStrengthMax - kSharpStrengthMin;
            float kSharpLimitScale = kSharpLimitMax - kSharpLimitMin;

            float kSrcNormX = 1f / inW;
            float kSrcNormY = 1f / inH;
            float kDstNormX = 1f / outW;
            float kDstNormY = 1f / outH;
            float kScaleX = inW / (float)outW;
            float kScaleY = inH / (float)outH;

            int i = 0;
            cb[i++] = kDetectRatio;
            cb[i++] = kDetectThres;
            cb[i++] = kMinContrastRatio;
            cb[i++] = kRatioNorm;
            cb[i++] = 1.0f;
            cb[i++] = 1.0f / 255.0f;
            cb[i++] = kSharpStartY;
            cb[i++] = kSharpScaleY;
            cb[i++] = kSharpStrengthMin;
            cb[i++] = kSharpStrengthScale;
            cb[i++] = kSharpLimitMin;
            cb[i++] = kSharpLimitScale;
            cb[i++] = kScaleX;
            cb[i++] = kScaleY;
            cb[i++] = kDstNormX;
            cb[i++] = kDstNormY;
            cb[i++] = kSrcNormX;
            cb[i++] = kSrcNormY;
            cb[i++] = AsFloat(0);
            cb[i++] = AsFloat(0);
            cb[i++] = AsFloat(inW);
            cb[i++] = AsFloat(inH);
            cb[i++] = AsFloat(0);
            cb[i++] = AsFloat(0);
            cb[i++] = AsFloat(outW);
            cb[i++] = AsFloat(outH);
            cb[i++] = 0f;
            cb[i++] = 0f;
        }

        private static float AsFloat(uint value) => BitConverter.Int32BitsToSingle((int)value);
    }
}
