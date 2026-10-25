
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct MvppInjectDlssCommand : IGALCommand, IGALCommand<MvppInjectDlssCommand>
    {
        public readonly CommandType CommandType => CommandType.MvppInjectDlss;
        private TableRef<ITexture> _src;
        private TableRef<ITexture> _dst;
        private TableRef<ITexture> _depth;

        public void Set(TableRef<ITexture> src, TableRef<ITexture> dst, TableRef<ITexture> depth)
        {
            _src = src;
            _dst = dst;
            _depth = depth;
        }

        public static void Run(ref MvppInjectDlssCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            renderer.Pipeline.MvppInjectDlssUpscale(
                command._src.GetAs<ThreadedTexture>(threaded)?.Base,
                command._dst.GetAs<ThreadedTexture>(threaded)?.Base,
                command._depth.GetAs<ThreadedTexture>(threaded)?.Base);
        }
    }
}
