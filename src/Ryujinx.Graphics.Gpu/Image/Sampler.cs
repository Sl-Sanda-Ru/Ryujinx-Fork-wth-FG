using Ryujinx.Graphics.GAL;
using System;

namespace Ryujinx.Graphics.Gpu.Image
{
    /// <summary>
    /// Cached sampler entry for sampler pools.
    /// </summary>
    class Sampler : IDisposable
    {
        /// <summary>
        /// True if the sampler is disposed, false otherwise.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// True if the sampler has sRGB conversion enabled, false otherwise.
        /// </summary>
        public bool IsSrgb { get; }

        /// <summary>
        /// </summary>
        public MinFilter ProbeMinFilter { get; }
        public MagFilter ProbeMagFilter { get; }
        public float ProbeMinLod { get; }
        public float ProbeMaxLod { get; }
        public float ProbeMipLodBias { get; }

        /// <summary>
        /// </summary>
        public AddressMode ProbeAddressU { get; }
        public AddressMode ProbeAddressV { get; }
        public float ProbeBorderA { get; }

        /// <summary>
        /// Host sampler object.
        /// </summary>
        private readonly ISampler _hostSampler;

        /// <summary>
        /// Host sampler object, with anisotropy forced.
        /// </summary>
        private readonly ISampler _anisoSampler;

        private static bool _forceMipsTrilinear =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_FORCEMIPS_TRILINEAR") == "1";

        /// <summary>
        /// Re-reads the fields above that come from the settings profile, so a changed upscaler
        /// setting applies at the next game start without restarting the app.
        /// </summary>
        internal static void ReloadProfileSettings()
        {
            {
                _forceMipsTrilinear = System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_FORCEMIPS_TRILINEAR") == "1";
            }
        }

        private static readonly float _forceMipsBias =
            float.TryParse(
                System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_FORCEMIPS_BIAS"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float fmBias) ? fmBias : 0f;

        /// <summary>
        /// </summary>
        private readonly ISampler _trilinearSampler;

        /// <summary>
        /// Creates a new instance of the cached sampler.
        /// </summary>
        /// <param name="context">The GPU context the sampler belongs to</param>
        /// <param name="descriptor">The Maxwell sampler descriptor</param>
        public Sampler(GpuContext context, SamplerDescriptor descriptor)
        {
            IsSrgb = descriptor.UnpackSrgb();

            MinFilter minFilter = descriptor.UnpackMinFilter();
            MagFilter magFilter = descriptor.UnpackMagFilter();

            bool seamlessCubemap = descriptor.UnpackSeamlessCubemap();

            AddressMode addressU = descriptor.UnpackAddressU();
            AddressMode addressV = descriptor.UnpackAddressV();
            AddressMode addressP = descriptor.UnpackAddressP();

            CompareMode compareMode = descriptor.UnpackCompareMode();
            CompareOp compareOp = descriptor.UnpackCompareOp();

            ColorF color = new(
                descriptor.BorderColorR,
                descriptor.BorderColorG,
                descriptor.BorderColorB,
                descriptor.BorderColorA);

            float minLod = descriptor.UnpackMinLod();
            float maxLod = descriptor.UnpackMaxLod();
            float mipLodBias = descriptor.UnpackMipLodBias();

            ProbeMinFilter = minFilter;
            ProbeMagFilter = magFilter;
            ProbeMinLod = minLod;
            ProbeMaxLod = maxLod;
            ProbeMipLodBias = mipLodBias;
            ProbeAddressU = addressU;
            ProbeAddressV = addressV;
            ProbeBorderA = descriptor.BorderColorA;

            float maxRequestedAnisotropy = descriptor.UnpackMaxAnisotropy();
            float maxSupportedAnisotropy = context.Capabilities.MaximumSupportedAnisotropy;

            _hostSampler = context.Renderer.CreateSampler(new SamplerCreateInfo(
                minFilter,
                magFilter,
                seamlessCubemap,
                addressU,
                addressV,
                addressP,
                compareMode,
                compareOp,
                color,
                minLod,
                maxLod,
                mipLodBias,
                Math.Min(maxRequestedAnisotropy, maxSupportedAnisotropy)));

            bool hasMipFilter = minFilter is MinFilter.NearestMipmapNearest or MinFilter.LinearMipmapNearest
                or MinFilter.NearestMipmapLinear or MinFilter.LinearMipmapLinear;
            bool wantTrilinear = _forceMipsTrilinear &&
                (minFilter == MinFilter.NearestMipmapNearest || minFilter == MinFilter.LinearMipmapNearest);

            if (wantTrilinear || (_forceMipsBias != 0f && hasMipFilter))
            {
                MinFilter promoted = !wantTrilinear
                    ? minFilter
                    : minFilter == MinFilter.NearestMipmapNearest
                        ? MinFilter.NearestMipmapLinear
                        : MinFilter.LinearMipmapLinear;

                _trilinearSampler = context.Renderer.CreateSampler(new SamplerCreateInfo(
                    promoted,
                    magFilter,
                    seamlessCubemap,
                    addressU,
                    addressV,
                    addressP,
                    compareMode,
                    compareOp,
                    color,
                    minLod,
                    maxLod,
                    mipLodBias + _forceMipsBias,
                    Math.Min(maxRequestedAnisotropy, maxSupportedAnisotropy)));
            }

            if (GraphicsConfig.MaxAnisotropy >= 0 && GraphicsConfig.MaxAnisotropy <= 16 && (minFilter == MinFilter.LinearMipmapNearest || minFilter == MinFilter.LinearMipmapLinear))
            {
                maxRequestedAnisotropy = GraphicsConfig.MaxAnisotropy;

                _anisoSampler = context.Renderer.CreateSampler(new SamplerCreateInfo(
                    minFilter,
                    magFilter,
                    seamlessCubemap,
                    addressU,
                    addressV,
                    addressP,
                    compareMode,
                    compareOp,
                    color,
                    minLod,
                    maxLod,
                    mipLodBias,
                    Math.Min(maxRequestedAnisotropy, maxSupportedAnisotropy)));
            }
        }

        /// <summary>
        /// Gets a host sampler for the given texture.
        /// </summary>
        /// <param name="texture">Texture to be sampled</param>
        /// <returns>A host sampler</returns>
        public ISampler GetHostSampler(Texture texture)
        {
            if (_trilinearSampler != null && texture?.HasForcedMips == true)
            {
                return _trilinearSampler;
            }

            return _anisoSampler != null && texture?.CanForceAnisotropy == true ? _anisoSampler : _hostSampler;
        }

        /// <summary>
        /// Disposes the host sampler object.
        /// </summary>
        public void Dispose()
        {
            IsDisposed = true;

            _hostSampler.Dispose();
            _anisoSampler?.Dispose();
            _trilinearSampler?.Dispose();
        }
    }
}
