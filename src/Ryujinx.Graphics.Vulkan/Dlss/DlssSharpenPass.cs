
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Silk.NET.Vulkan;
using System;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    class DlssSharpenPass : IDisposable
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_SHARPEN") == "1";

        public static readonly int Level =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DLSS_SHARPEN_LEVEL"), out int l)
                ? Math.Clamp(l, 0, 100)
                : 25;

        private readonly VulkanRenderer _renderer;
        private readonly PipelineHelperShader _pipeline;
        private readonly IProgram _program;
        private readonly ISampler _sampler;
        private TextureView _sharpened;
        private bool _armedLogged;

        public DlssSharpenPass(VulkanRenderer renderer, Device device)
        {
            _renderer = renderer;

            _pipeline = new PipelineHelperShader(renderer, device);
            _pipeline.Initialize();

            ResourceLayout layout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 3)
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 4)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

            _sampler = renderer.CreateSampler(SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            _program = renderer.CreateProgramWithMinimalLayout([
                new ShaderSource(
                    EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/DlssSharpenLinear.spv"),
                    ShaderStage.Compute,
                    TargetLanguage.Spirv)
            ], layout);
        }

        public TextureView Run(TextureView input, CommandBufferScoped cbs)
        {
            if (_sharpened == null ||
                _sharpened.Width != input.Width ||
                _sharpened.Height != input.Height ||
                _sharpened.Info.Format != input.Info.Format)
            {
                _sharpened?.Dispose();
                _sharpened = _renderer.CreateTexture(input.Info) as TextureView;
            }

            if (_sharpened == null)
            {
                return null;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS nettete: ARMEE (SHARPEN=1, niveau {Level}/100) - RCAS lineaire maison en pre-passe sur la sortie DLSS.");
            }

            ReadOnlySpan<float> sharpening = [1.5f - (Level * 0.01f * 1.5f)];
            using ScopedTemporaryBuffer buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, sharpening);

            int threadGroupWorkRegionDim = 16;
            int dispatchX = (input.Width + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchY = (input.Height + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;

            _pipeline.SetCommandBuffer(cbs);
            _pipeline.SetProgram(_program);
            _pipeline.SetTextureAndSampler(ShaderStage.Compute, 1, input, _sampler);
            _pipeline.SetUniformBuffers([new BufferAssignment(4, buffer.Range)]);
            _pipeline.SetImage(ShaderStage.Compute, 0, _sharpened);
            _pipeline.DispatchCompute(dispatchX, dispatchY, 1);
            _pipeline.ComputeBarrier();

            _pipeline.Finish();

            return _sharpened;
        }

        public void Dispose()
        {
            _pipeline?.Dispose();
            _program?.Dispose();
            _sampler?.Dispose();
            _sharpened?.Dispose();
        }
    }
}
