
using Ryujinx.Graphics.GAL.Multithreading.Model;
using System;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands.Buffer
{
    struct BufferSetDataBatchEntry
    {
        public BufferHandle Buffer;
        public int Offset;
        public int Size;
    }

    struct BufferSetDataBatchCommand : IGALCommand, IGALCommand<BufferSetDataBatchCommand>
    {
        public readonly CommandType CommandType => CommandType.BufferSetDataBatch;
        private SpanRef<BufferSetDataBatchEntry> _entries;
        private SpanRef<byte> _data;
        private int _count;

        public void Set(SpanRef<BufferSetDataBatchEntry> entries, SpanRef<byte> data, int count)
        {
            _entries = entries;
            _data = data;
            _count = count;
        }

        public static void Run(ref BufferSetDataBatchCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            Span<BufferSetDataBatchEntry> entries = command._count <= 64
                ? stackalloc BufferSetDataBatchEntry[64]
                : new BufferSetDataBatchEntry[command._count];

            command._entries.Get(threaded)[..command._count].CopyTo(entries);
            command._entries.Dispose(threaded);
            entries = entries[..command._count];

            ReadOnlySpan<byte> data = command._data.Get(threaded);

            int dataOffset = 0;
            foreach (BufferSetDataBatchEntry entry in entries)
            {
                renderer.SetBufferData(threaded.Buffers.MapBuffer(entry.Buffer), entry.Offset, data.Slice(dataOffset, entry.Size));
                dataOffset += entry.Size;
            }

            command._data.Dispose(threaded);
        }
    }
}
