using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// The UI's side of the performance overlay. The status bar is hidden in fullscreen and an
    /// Avalonia control cannot draw over the native render surface, so the overlay is drawn into the
    /// swapchain image itself; the UI only decides whether it shows and what it says.
    /// </summary>
    public static class PerformanceOverlay
    {
        private static volatile bool _enabled;
        private static string _text = string.Empty;

        /// <summary>Raised when <see cref="Enabled"/> changes, so every toggle bound to it can follow.</summary>
        public static event Action EnabledChanged;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                EnabledChanged?.Invoke();
            }
        }

        /// <summary>Lines separated by '\n'. Only ASCII is drawn; anything else shows as a blank.</summary>
        public static void SetText(string text) => Volatile.Write(ref _text, text ?? string.Empty);

        internal static string Text => Volatile.Read(ref _text);

        private static string _resolution = string.Empty;

        /// <summary>
        /// "source -> output  upscaler" for the last presented frame: the size the game rendered at,
        /// the size it filled on screen, and what scaled it.
        /// </summary>
        public static string ResolutionLine => Volatile.Read(ref _resolution);

        internal static void ReportResolution(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, string scaler)
        {
            string line = scaler.Length == 0
                ? $"{sourceWidth}x{sourceHeight} -> {outputWidth}x{outputHeight}"
                : $"{sourceWidth}x{sourceHeight} -> {outputWidth}x{outputHeight}  {scaler}";

            // Same frame size every frame in practice, so only a change allocates a new string.
            if (line != _resolution)
            {
                Volatile.Write(ref _resolution, line);
            }
        }

        private static long _presentsInWindow;
        private static long _windowStartMs;
        private static long _hostPresentFpsMilli;

        /// <summary>
        /// Presents the renderer issued per second, measured over half-second windows. Without
        /// frame generation this is what reaches the screen; with it, use the generator's count.
        /// </summary>
        public static float HostPresentFps => Interlocked.Read(ref _hostPresentFpsMilli) / 1000f;

        internal static void NotePresent()
        {
            long now = Environment.TickCount64;

            _presentsInWindow++;

            if (_windowStartMs == 0)
            {
                _windowStartMs = now;
            }
            else if (now - _windowStartMs >= 500)
            {
                Interlocked.Exchange(ref _hostPresentFpsMilli, _presentsInWindow * 1000L * 1000L / (now - _windowStartMs));

                _presentsInWindow = 0;
                _windowStartMs = now;
            }
        }
    }

    /// <summary>
    /// Draws <see cref="PerformanceOverlay"/> text into the top-left corner of the presented image.
    /// The text is rasterised on the CPU with a built-in 5x7 font into a small texture, uploaded only
    /// when it changes, and blitted with the same helper the scene uses, so every swapchain format
    /// (HDR included) is handled the same way.
    /// </summary>
    class PerformanceOverlayRenderer : IDisposable
    {
        private const int GlyphWidth = 5;
        private const int GlyphHeight = 7;
        private const int CellWidth = GlyphWidth + 1;
        private const int CellHeight = GlyphHeight + 2;
        private const int Padding = 3;

        private TextureView _texture;
        private string _textureText;
        private int _textureScale;
        private int _textureWidth;
        private int _textureHeight;

        public void Draw(
            VulkanRenderer gd,
            CommandBufferScoped cbs,
            TextureView dst,
            int dstWidth,
            int dstHeight,
            bool hdr,
            float paperWhite,
            float peak,
            float curve,
            float gamma,
            float blend,
            float whiten,
            bool pqOutput)
        {
            string text = PerformanceOverlay.Text;

            if (!PerformanceOverlay.Enabled || text.Length == 0 || dstWidth <= 0 || dstHeight <= 0)
            {
                return;
            }

            // Sized against the output height, so the text reads the same at 720p and at 4K.
            int scale = Math.Max(2, dstHeight / 360);

            if (_texture == null || _textureText != text || _textureScale != scale)
            {
                Upload(gd, text, scale);
            }

            int margin = 4 * scale;
            int width = Math.Min(_textureWidth, dstWidth - margin);
            int height = Math.Min(_textureHeight, dstHeight - margin);

            if (width <= 0 || height <= 0)
            {
                return;
            }

            gd.HelperShader.BlitColor(
                gd,
                cbs,
                _texture,
                dst,
                new Extents2D(0, 0, width, height),
                new Extents2D(margin, margin, margin + width, margin + height),
                linearFilter: false,
                clearAlpha: true,
                hdr,
                paperWhite,
                peak,
                curve,
                gamma,
                blend,
                whiten,
                pqOutput,
                clearTarget: false);
        }

        private void Upload(VulkanRenderer gd, string text, int scale)
        {
            string[] lines = text.Split('\n');
            int columns = 1;

            foreach (string line in lines)
            {
                columns = Math.Max(columns, line.Length);
            }

            int width = (columns * CellWidth - 1 + Padding * 2) * scale;
            int height = (lines.Length * CellHeight - 2 + Padding * 2) * scale;

            if (_texture == null || width != _textureWidth || height != _textureHeight)
            {
                _texture?.Dispose();
                _texture = gd.CreateTexture(new TextureCreateInfo(
                    width,
                    height,
                    1,
                    1,
                    1,
                    1,
                    1,
                    4,
                    Format.R8G8B8A8Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red,
                    SwizzleComponent.Green,
                    SwizzleComponent.Blue,
                    SwizzleComponent.Alpha)) as TextureView;

                _textureWidth = width;
                _textureHeight = height;
            }

            MemoryOwner<byte> pixels = MemoryOwner<byte>.Rent(width * height * 4);
            Span<byte> span = pixels.Span;

            // Dark backing box, so the text stays readable over bright scenes.
            for (int i = 0; i < span.Length; i += 4)
            {
                span[i] = 16;
                span[i + 1] = 16;
                span[i + 2] = 16;
                span[i + 3] = 255;
            }

            for (int row = 0; row < lines.Length; row++)
            {
                string line = lines[row];

                for (int column = 0; column < line.Length; column++)
                {
                    ReadOnlySpan<byte> glyph = Glyph(line[column]);

                    int originX = (Padding + column * CellWidth) * scale;
                    int originY = (Padding + row * CellHeight) * scale;

                    for (int gy = 0; gy < GlyphHeight; gy++)
                    {
                        for (int gx = 0; gx < GlyphWidth; gx++)
                        {
                            if ((glyph[gy] & (1 << (GlyphWidth - 1 - gx))) == 0)
                            {
                                continue;
                            }

                            for (int sy = 0; sy < scale; sy++)
                            {
                                int y = originY + gy * scale + sy;
                                int rowOffset = y * width;

                                for (int sx = 0; sx < scale; sx++)
                                {
                                    int offset = (rowOffset + originX + gx * scale + sx) * 4;

                                    span[offset] = 255;
                                    span[offset + 1] = 255;
                                    span[offset + 2] = 255;
                                }
                            }
                        }
                    }
                }
            }

            // SetData records into the preload command buffer, which Present's FlushAllCommands
            // submits ahead of the present command buffer, so the blit sees the new contents.
            _texture.SetData(pixels);

            _textureText = text;
            _textureScale = scale;
        }

        private static ReadOnlySpan<byte> Glyph(char c)
        {
            if (c >= 'a' && c <= 'z' && c != 'x')
            {
                c = (char)(c - 'a' + 'A');
            }

            int index = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'Z' => 10 + (c - 'A'),
                '.' => 36,
                ':' => 37,
                '-' => 38,
                '>' => 39,
                '/' => 40,
                '%' => 41,
                '|' => 42,
                'x' => 43,
                '(' => 44,
                ')' => 45,
                _ => -1,
            };

            return index < 0 ? Blank : Font.AsSpan(index * GlyphHeight, GlyphHeight);
        }

        private static readonly byte[] Blank = new byte[GlyphHeight];

        // 5x7 glyphs, one byte per row, bit 4 is the leftmost column.
        private static readonly byte[] Font =
        [
            0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E, // 0
            0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E, // 1
            0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F, // 2
            0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E, // 3
            0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02, // 4
            0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E, // 5
            0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E, // 6
            0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08, // 7
            0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E, // 8
            0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C, // 9
            0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11, // A
            0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E, // B
            0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E, // C
            0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C, // D
            0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F, // E
            0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10, // F
            0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F, // G
            0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11, // H
            0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E, // I
            0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C, // J
            0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11, // K
            0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F, // L
            0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11, // M
            0x11, 0x11, 0x19, 0x15, 0x13, 0x11, 0x11, // N
            0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E, // O
            0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10, // P
            0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D, // Q
            0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11, // R
            0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E, // S
            0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, // T
            0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E, // U
            0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04, // V
            0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A, // W
            0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11, // X
            0x11, 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, // Y
            0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F, // Z
            0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C, // .
            0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00, // :
            0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00, // -
            0x08, 0x04, 0x02, 0x01, 0x02, 0x04, 0x08, // >
            0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x00, // /
            0x18, 0x19, 0x02, 0x04, 0x08, 0x13, 0x03, // %
            0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, // |
            0x00, 0x00, 0x11, 0x0A, 0x04, 0x0A, 0x11, // x (small, for "x2")
            0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02, // (
            0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08, // )
        ];

        public void Dispose()
        {
            _texture?.Dispose();
            _texture = null;
        }
    }
}
