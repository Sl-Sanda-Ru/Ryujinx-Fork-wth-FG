using System;

namespace Ryujinx.Ava.UI.Models
{
    internal class StatusUpdatedEventArgs : EventArgs
    {
        public string VSyncMode { get; }
        public string VolumeStatus { get; }
        public string AspectRatio { get; }
        public string DockedMode { get; }
        public string FifoStatus { get; }
        public string GameStatus { get; }
        public uint ShaderCount { get; }
        public string FrameGen { get; }
        public string Dlss { get; }

        public StatusUpdatedEventArgs(string vSyncMode, string volumeStatus, string dockedMode, string aspectRatio, string gameStatus, string fifoStatus, uint shaderCount, string frameGen, string dlss)
        {
            VSyncMode = vSyncMode;
            VolumeStatus = volumeStatus;
            DockedMode = dockedMode;
            AspectRatio = aspectRatio;
            GameStatus = gameStatus;
            FifoStatus = fifoStatus;
            ShaderCount = shaderCount;
            FrameGen = frameGen;
            Dlss = dlss;
        }

        public override bool Equals(object obj)
        {
            if (obj is not StatusUpdatedEventArgs suea)
                return false;
            return
                VSyncMode == suea.VSyncMode &&
                VolumeStatus == suea.VolumeStatus &&
                DockedMode == suea.DockedMode &&
                AspectRatio == suea.AspectRatio &&
                GameStatus == suea.GameStatus &&
                FifoStatus == suea.FifoStatus &&
                ShaderCount == suea.ShaderCount &&
                FrameGen == suea.FrameGen &&
                Dlss == suea.Dlss;
        }

        public override int GetHashCode()
            => HashCode.Combine(VSyncMode, VolumeStatus, AspectRatio, DockedMode, FifoStatus, GameStatus, HashCode.Combine(ShaderCount, FrameGen, Dlss));
    }
}
