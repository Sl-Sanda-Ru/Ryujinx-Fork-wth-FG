using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private unsafe delegate int AVCodec_decode(AVCodecContext* avctx, void* outdata, int* got_frame_ptr, AVPacket* avpkt);

        private readonly AVCodec_decode _decodeFrame;
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;

        public FFmpegContext(AVCodecID codecId)
        {
            _codec = FFmpegApi.avcodec_find_decoder(codecId);
            if (_codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");

                return;
            }

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");

                return;
            }

            if (FFmpegApi.avcodec_open2(_context, _codec, null) != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec couldn't be opened.");

                return;
            }

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");

                return;
            }

            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

            // libavcodec 59.24 changed AvCodec to move its private API and also move the codec function to a union.
            if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
            {
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
            }
            // libavcodec 59.x changed AvCodec private API layout.
            else if (avCodecMajorVersion == 59)
            {
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
            }
            // libavcodec 58.x and lower
            else
            {
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
            }
        }

        static FFmpegContext()
        {
            _logFunc = Log;

            // Redirect log output.
            FFmpegApi.av_log_set_level(AVLog.MaxOffset);
            FFmpegApi.av_log_set_callback(_logFunc);
        }

        private static void Log(void* ptr, AVLog level, string format, byte* vl)
        {
            if (level > FFmpegApi.av_log_get_level())
            {
                return;
            }

            int lineSize = 1024;
            byte* lineBuffer = stackalloc byte[lineSize];
            int printPrefix = 1;

            FFmpegApi.av_log_format_line(ptr, level, format, vl, lineBuffer, lineSize, &printPrefix);

            string line = Marshal.PtrToStringAnsi((nint)lineBuffer).Trim();

            switch (level)
            {
                case AVLog.Panic:
                case AVLog.Fatal:
                case AVLog.Error:
                    Logger.Error?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Warning:
                    Logger.Warning?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Info:
                    Logger.Info?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Verbose:
                case AVLog.Debug:
                    Logger.Debug?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Trace:
                    Logger.Trace?.Print(LogClass.FFmpeg, line);
                    break;
            }
        }

        private static readonly bool _vidProbe =
            Environment.GetEnvironmentVariable("RYUJINX_VIDPROBE") == "1";
        private static long _vpWindowMs;
        private static int _vpSubmitted, _vpDelivered, _vpDelayed, _vpFailed, _vpLastErr;
        private static long _vpTotalSubmitted, _vpTotalDelivered;

        private static void VidProbeTick(int submitted, int delivered, int delayed, int failed, int lastErr)
        {
            _vpSubmitted += submitted;
            _vpDelivered += delivered;
            _vpDelayed += delayed;
            _vpFailed += failed;
            _vpTotalSubmitted += submitted;
            _vpTotalDelivered += delivered;

            if (lastErr != 0)
            {
                _vpLastErr = lastErr;
            }

            long now = Environment.TickCount64;

            if (now - _vpWindowMs < 1000)
            {
                return;
            }

            if (_vpWindowMs != 0)
            {
                Logger.Info?.Print(LogClass.FFmpeg,
                    $"VIDPROBE 1s : soumis={_vpSubmitted} rendus={_vpDelivered} retardes={_vpDelayed} " +
                    $"echecs={_vpFailed} dernier-code={_vpLastErr} | cumul soumis={_vpTotalSubmitted} rendus={_vpTotalDelivered}");
            }

            _vpWindowMs = now;
            _vpSubmitted = _vpDelivered = _vpDelayed = _vpFailed = 0;
        }

        public int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream)
        {
            FFmpegApi.av_frame_unref(output.Frame);

            int result;
            int gotFrame;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
            }

            bool delayedTried = false;

            if (gotFrame == 0)
            {
                delayedTried = true;
                FFmpegApi.av_frame_unref(output.Frame);

                // If the frame was not delivered, it was probably delayed.
                // Get the next delayed frame by passing a 0 length packet.
                _packet->Data = null;
                _packet->Size = 0;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);

                // We need to set B frames to 0 as we already consumed all delayed frames.
                // This prevents the decoder from trying to return a delayed frame next time.
                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);

                if (_vidProbe)
                {
                    VidProbeTick(1, 0, delayedTried ? 1 : 0, 1, result);
                }

                return -1;
            }

            if (_vidProbe)
            {
                VidProbeTick(1, 1, delayedTried ? 1 : 0, result < 0 ? 1 : 0, result < 0 ? result : 0);
            }

            return result < 0 ? result : 0;
        }

        public void Dispose()
        {
            fixed (AVPacket** ppPacket = &_packet)
            {
                FFmpegApi.av_packet_free(ppPacket);
            }

            _ = FFmpegApi.avcodec_close(_context);

            fixed (AVCodecContext** ppContext = &_context)
            {
                FFmpegApi.avcodec_free_context(ppContext);
            }
        }
    }
}
