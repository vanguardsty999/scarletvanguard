using System.IO.Compression;

namespace RpfPreview.Core;


internal sealed class ZlibSegmentsStream : Stream
{
    private readonly ArchiveSegment[] _segments;
    private readonly Func<ArchiveSegment, Stream> _open;
    private readonly IDisposable? _owner;
    private Stream? _current;
    private int _index;
    private long _read;

    internal ZlibSegmentsStream(IReadOnlyList<ArchiveSegment> segments, Func<ArchiveSegment, Stream> open, IDisposable? owner = null)
    {
        _segments = segments.ToArray(); _open = open; _owner = owner;
        if (_segments.Any(s => s.Offset < 0 || s.StoredSize < 6 || s.Size < 0 || (s.IsRaw && s.StoredSize != s.Size)))
            throw new InvalidDataException("Invalid recovered compression-block descriptor.");
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _segments.Sum(s => s.Size);
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private bool EnsureCurrent()
    {
        if (_index >= _segments.Length) return false;
        var segment = _segments[_index];
        _current ??= segment.IsRaw ? _open(segment) : new ZLibStream(_open(segment), CompressionMode.Decompress);
        return true;
    }
    private void CompleteBlock()
    {
        if (_read != _segments[_index].Size) throw new InvalidDataException("Recovered compression block changed length.");
        _current?.Dispose(); _current = null; _read = 0; _index++;
    }
    private void Count(int read)
    {
        _read += read;
        if (_read > _segments[_index].Size) throw new InvalidDataException("Recovered block exceeds its validated length.");
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        while (EnsureCurrent())
        {
            int read = _current!.Read(buffer);
            if (read > 0) { Count(read); return read; }
            CompleteBlock();
        }
        return 0;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.Length == 0) return 0;
        while (EnsureCurrent())
        {
            token.ThrowIfCancellationRequested();
            int read = await _current!.ReadAsync(buffer, token);
            if (read > 0) { Count(read); return read; }
            CompleteBlock();
        }
        return 0;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _current?.Dispose(); _owner?.Dispose(); }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
