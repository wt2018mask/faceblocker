using System.IO;
using System.Text;

namespace VeilDesk.Core.Export;

/// <summary>
/// Minimal, self-contained Motion JPEG AVI writer. No ffmpeg, media
/// framework extension or cloud encoder is involved: each frame is a JPEG
/// produced locally and stored in a standard RIFF AVI container with an idx1
/// index. The writer keeps the output streaming and patches the headers when
/// recording stops, so memory use stays bounded.
/// </summary>
public sealed class MjpegAviWriter : IDisposable
{
    private const uint AvifHasIndex = 0x10;
    private const uint AviifKeyframe = 0x10;

    private readonly FileStream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly List<(uint Offset, uint Size)> _index = new(1024);

    private readonly long _riffSizePosition;
    private readonly long _moviListSizePosition;
    private readonly long _moviFourCcPosition;
    private readonly long _avihTotalFramesPosition;
    private readonly long _avihSuggestedBufferPosition;
    private readonly long _avihMaxBytesPerSecondPosition;
    private readonly long _strhLengthPosition;
    private readonly long _strhSuggestedBufferPosition;

    private int _maxFrameSize;
    private bool _finished;
    private bool _disposed;

    public MjpegAviWriter(string path, int width, int height, int fps)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(fps));
        _width = width;
        _height = height;
        _fps = fps;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        OutPath = path;

        WriteFourCc("RIFF");
        _riffSizePosition = _stream.Position;
        WriteUInt32(0);
        WriteFourCc("AVI ");

        WriteFourCc("LIST");
        var hdrlSizePosition = _stream.Position;
        WriteUInt32(0);
        var hdrlStart = _stream.Position;
        WriteFourCc("hdrl");

        WriteFourCc("avih");
        WriteUInt32(56);
        WriteUInt32((uint)Math.Max(1, 1_000_000 / fps));
        _avihMaxBytesPerSecondPosition = _stream.Position;
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(AvifHasIndex);
        _avihTotalFramesPosition = _stream.Position;
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(1);
        _avihSuggestedBufferPosition = _stream.Position;
        WriteUInt32(0);
        WriteUInt32((uint)width);
        WriteUInt32((uint)height);
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);

        WriteFourCc("LIST");
        var strlSizePosition = _stream.Position;
        WriteUInt32(0);
        var strlStart = _stream.Position;
        WriteFourCc("strl");

        WriteFourCc("strh");
        WriteUInt32(56);
        WriteFourCc("vids");
        WriteFourCc("MJPG");
        WriteUInt32(0);
        WriteUInt16(0);
        WriteUInt16(0);
        WriteUInt32(0);
        WriteUInt32(1);
        WriteUInt32((uint)fps);
        WriteUInt32(0);
        _strhLengthPosition = _stream.Position;
        WriteUInt32(0);
        _strhSuggestedBufferPosition = _stream.Position;
        WriteUInt32(0);
        WriteUInt32(unchecked((uint)-1));
        WriteUInt32(0);
        WriteUInt16(0);
        WriteUInt16(0);
        WriteUInt16((ushort)Math.Min(width, ushort.MaxValue));
        WriteUInt16((ushort)Math.Min(height, ushort.MaxValue));

        WriteFourCc("strf");
        WriteUInt32(40);
        WriteUInt32(40);
        WriteUInt32((uint)width);
        WriteUInt32((uint)height);
        WriteUInt16(1);
        WriteUInt16(24);
        WriteFourCc("MJPG");
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);

        PatchListSize(strlSizePosition, strlStart);
        PatchListSize(hdrlSizePosition, hdrlStart);

        WriteFourCc("LIST");
        _moviListSizePosition = _stream.Position;
        WriteUInt32(0);
        _moviFourCcPosition = _stream.Position;
        WriteFourCc("movi");
    }

    public string OutPath { get; }
    public int FrameCount => _index.Count;
    public long Length => _stream.Length;

    public void WriteFrame(ReadOnlySpan<byte> jpeg)
    {
        if (_finished) throw new InvalidOperationException("The AVI has already been finalised.");
        if (jpeg.Length == 0) throw new ArgumentException("A JPEG frame is empty.", nameof(jpeg));
        if (_index.Count >= 9_000)
            throw new InvalidOperationException("Recording reached the 10 minute / 9,000 frame limit. Stop and start a new recording.");

        var chunkPosition = _stream.Position;
        WriteFourCc("00dc");
        WriteUInt32((uint)jpeg.Length);
        _stream.Write(jpeg);
        if ((jpeg.Length & 1) != 0) _stream.WriteByte(0);

        var offset = checked((uint)(chunkPosition - _moviFourCcPosition));
        _index.Add((offset, (uint)jpeg.Length));
        _maxFrameSize = Math.Max(_maxFrameSize, jpeg.Length);

        if (_stream.Length > 1_900_000_000L)
            throw new InvalidOperationException("Recording reached the 1.9 GB AVI limit. Stop and start a new recording.");
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;

        var moviEnd = _stream.Position;
        // The LIST size field covers the 'movi' FOURCC plus every frame chunk,
        // so it starts immediately after the size field itself.
        var moviSize = moviEnd - (_moviListSizePosition + 4);
        WriteAt(_moviListSizePosition, (uint)moviSize);

        WriteFourCc("idx1");
        WriteUInt32((uint)(_index.Count * 16));
        foreach (var (offset, size) in _index)
        {
            WriteFourCc("00dc");
            WriteUInt32(AviifKeyframe);
            WriteUInt32(offset);
            WriteUInt32(size);
        }

        if ((_stream.Position & 1) != 0) _stream.WriteByte(0);
        WriteAt(_avihTotalFramesPosition, (uint)_index.Count);
        WriteAt(_avihSuggestedBufferPosition, (uint)_maxFrameSize);
        WriteAt(_avihMaxBytesPerSecondPosition, (uint)Math.Min(uint.MaxValue,
            Math.Max(0, _maxFrameSize) * _fps));
        WriteAt(_strhLengthPosition, (uint)_index.Count);
        WriteAt(_strhSuggestedBufferPosition, (uint)_maxFrameSize);
        WriteAt(_riffSizePosition, (uint)Math.Max(0, _stream.Length - 8));
        _stream.Flush(flushToDisk: true);
    }

    private void PatchListSize(long sizePosition, long listContentStart)
    {
        WriteAt(sizePosition, (uint)(_stream.Position - listContentStart));
    }

    private void WriteAt(long position, uint value)
    {
        var end = _stream.Position;
        _stream.Position = position;
        WriteUInt32(value);
        _stream.Position = end;
    }

    private void WriteFourCc(string value)
    {
        Span<byte> bytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(value, bytes);
        _stream.Write(bytes);
    }

    private void WriteUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    private void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Finish();
        }
        finally
        {
            _stream.Dispose();
        }
    }
}
