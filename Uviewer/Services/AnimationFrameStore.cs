using System;
using System.IO;
using System.IO.Compression;

namespace Uviewer.Services;

// Composited frames can be gigabytes even when the source is only a few MB.
// Keep lossless frames in a private, delete-on-close spool instead of the LOH.
internal sealed class AnimationFrameStore : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _stream;

    public AnimationFrameStore()
    {
        _stream = new FileStream(Path.Combine(Path.GetTempPath(), $"Uviewer-animation-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
            FileOptions.DeleteOnClose | FileOptions.RandomAccess);
    }

    public readonly record struct Frame(long Offset, int Length);

    public Frame Write(byte[] pixels)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stream == null, this);
            _stream.Position = _stream.Length;
            var frame = new Frame(_stream.Position, pixels.Length);
            using (var compressor = new DeflateStream(_stream, CompressionLevel.Fastest, leaveOpen: true))
                compressor.Write(pixels);
            return frame;
        }
    }

    public byte[] Read(Frame frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stream == null, this);
            _stream.Position = frame.Offset;
            byte[] pixels = GC.AllocateUninitializedArray<byte>(frame.Length);
            using var decompressor = new DeflateStream(_stream, CompressionMode.Decompress, leaveOpen: true);
            decompressor.ReadExactly(pixels);
            return pixels;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
