using System.Buffers.Binary;
using System.IO.Compression;

namespace RpfPreview.Core;








public static class Rpf9TextureRecovery
{
    public const int MaxScanBytes = 16 * 1024 * 1024;
    private const long DecodeBudget = 512L * 1024 * 1024;
    private const int MaxDictionaries = 32;
    private const int ScanBlock = 4 * 1024 * 1024;
    private sealed record Block(long Offset, long Packed, byte[] Data);







    public static IReadOnlyList<long> FindCandidateOffsets(string path, long scanStart = 0,
        long? scanLength = null, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (scanStart < 0 || scanLength < 0) throw new ArgumentOutOfRangeException(nameof(scanStart));
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ScanBlock, FileOptions.SequentialScan);
        if (scanStart > file.Length) throw new ArgumentOutOfRangeException(nameof(scanStart));
        long end = scanLength is null ? file.Length : Math.Min(file.Length, checked(scanStart + scanLength.Value));
        long position = scanStart & ~4095L;
        var candidates = new List<long>(); byte[] scan = new byte[ScanBlock + 65536];
        while (position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(ScanBlock, end - position);
            int readCount = (int)Math.Min(count + 65536L, file.Length - position);
            file.Position = position; file.ReadExactly(scan.AsSpan(0, readCount));
            for (int page = 0; page < count && page + 20 < readCount; page += 4096)
            {
                long offset = position + page + 16;
                if (offset < scanStart || offset >= end || scan.AsSpan(page, 16).IndexOfAnyExcept((byte)0) >= 0 ||
                    !ZlibHeader(scan.AsSpan(page + 16, 2)) || !LooksLikeSystem(scan, page + 16, readCount - page - 16)) continue;
                if (candidates.Count >= 200_000) throw new InvalidDataException("The texture candidate limit was reached; scan a smaller range.");
                candidates.Add(offset);
            }
            position += count; progress?.Report(position);
        }
        return candidates;
    }









    public static IReadOnlyList<ArchiveEntry> RecoverAtOffsets(string path, long fileSize,
        IReadOnlyList<long> candidateOffsets, int firstIndex = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateOffsets);
        if (firstIndex < 0 || candidateOffsets.Count > 200_000) throw new ArgumentOutOfRangeException(nameof(firstIndex));
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != fileSize) throw new IOException("The archive changed while it was being opened.");
        var entries = new List<ArchiveEntry>(); var seen = new HashSet<long>();
        Span<byte> prefix = stackalloc byte[16];
        foreach (long candidate in candidateOffsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(candidate) || candidate < 16 || candidate > fileSize - 6) continue;
            file.Position = candidate - 16; file.ReadExactly(prefix);
            long resourceBudget = 0;
            if (prefix.IndexOfAnyExcept((byte)0) >= 0 ||
                !TryInflate(file, candidate, RageTextureDecoder.MaxSystemBytes, ref resourceBudget, out var first, cancellationToken)) continue;
            var blocks = new List<Block> { first }; int systemLength = first.Data.Length, graphicsLength = 0;
            byte[] system = first.Data; bool valid = true;
            while (true)
            {
                if (!RageTextureDecoder.TryGetMinimumSystemBytes(system, out int requiredSystem)) { valid = false; break; }
                if (requiredSystem <= system.Length) break;
                cancellationToken.ThrowIfCancellationRequested();
                long previousEnd = checked(blocks[^1].Offset + blocks[^1].Packed), next = (previousEnd + 15) & ~15L;
                if (blocks.Count >= 128 || !ZeroPadding(file, previousEnd, next) ||
                    !TryInflate(file, next, RageTextureDecoder.MaxSystemBytes - systemLength, ref resourceBudget, out var cpu, cancellationToken))
                { valid = false; break; }
                blocks.Add(cpu); int oldLength = systemLength; systemLength = checked(systemLength + cpu.Data.Length);
                byte[] combined = new byte[systemLength]; system.CopyTo(combined, 0); cpu.Data.CopyTo(combined, oldLength); system = combined;
            }
            if (!valid || !RageTextureDecoder.TryGetResourcePageCounts(system, out _, out _) ||
                !RageTextureDecoder.TryGetRequiredGraphicsBytes(system, out int requiredGraphics)) continue;
            int systemChunks = blocks.Count;
            while (graphicsLength < requiredGraphics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long previousEnd = checked(blocks[^1].Offset + blocks[^1].Packed), next = (previousEnd + 15) & ~15L;
                int maximum = RageTextureDecoder.MaxGraphicsBytes - systemLength - graphicsLength;
                if (maximum < 1 || blocks.Count > 4095 || !ZeroPadding(file, previousEnd, next) ||
                    !TryInflate(file, next, maximum, ref resourceBudget, out var block, cancellationToken))
                { valid = false; break; }
                blocks.Add(block);
                graphicsLength = checked(graphicsLength + block.Data.Length);
            }
            if (!valid || graphicsLength < 1) continue;
            byte[] graphics = new byte[graphicsLength]; int gfxAt = 0;
            for (int i = systemChunks; i < blocks.Count; i++)
            {
                blocks[i].Data.CopyTo(graphics, gfxAt); gfxAt += blocks[i].Data.Length;
            }
            if (RageTextureDecoder.Inspect(system, graphics) is null) continue;
            var segments = blocks.Select(b => new ArchiveSegment(b.Offset, b.Packed, b.Data.Length)).ToArray();
            entries.Add(new($"recovered/textures/dictionary_at_{candidate:X16}.r9texdata",
                segments.Sum(s => s.StoredSize), checked((long)systemLength + graphicsLength), candidate,
                false, false, false, "Recovered texture dictionary", checked(firstIndex + entries.Count))
            { Segments = segments, ResourceSystemBytes = systemLength });
        }
        return entries;
    }

    public static IReadOnlyList<ArchiveEntry> Recover(string path, long fileSize, byte[] header, int firstIndex)
    {
        var entries = new List<ArchiveEntry>();
        if (header.Length < 64 || firstIndex < 0) return entries;
        long metadataEnd = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (metadataEnd < 64 || metadataEnd > fileSize || metadataEnd > MaxScanBytes) return entries;
        long start = (metadataEnd + 15) & ~15L;
        if (start >= fileSize) return entries;
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != fileSize) return entries;
        byte[] scan = new byte[(int)Math.Min(fileSize - start, MaxScanBytes)];
        file.Position = start; file.ReadExactly(scan);
        long budget = 0;
        Span<byte> padding = stackalloc byte[15];
        for (int p = 0; p + 64 <= scan.Length && entries.Count < MaxDictionaries && budget < DecodeBudget; p += 16)
        {
            if (!ZlibHeader(scan.AsSpan(p))) continue;
            if (!LooksLikeSystem(scan, p)) continue;
            long systemOffset = start + p;
            if (!TryInflate(file, systemOffset, RageTextureDecoder.MaxSystemBytes, ref budget, out var system)) continue;
            long end = system.Offset + system.Packed, graphicsOffset = (end + 15) & ~15L;
            if (graphicsOffset + 6 > fileSize) continue;

            file.Position = end;
            int pad = (int)(graphicsOffset - end); file.ReadExactly(padding[..pad]);
            if (padding[..pad].IndexOfAnyExcept((byte)0) >= 0) continue;
            if (!TryInflate(file, graphicsOffset, RageTextureDecoder.MaxGraphicsBytes, ref budget, out var graphics)) continue;
            var dictionary = RageTextureDecoder.Inspect(system.Data, graphics.Data);
            if (dictionary is null || (long)system.Data.Length + graphics.Data.Length > RageTextureDecoder.MaxGraphicsBytes) continue;
            entries.Add(new($"recovered/textures/dictionary_at_{systemOffset:X16}.r9texdata",
                checked(system.Packed + graphics.Packed), checked((long)system.Data.Length + graphics.Data.Length),
                systemOffset, false, false, false, "Recovered texture dictionary", checked(firstIndex + entries.Count))
            {
                ResourceSystemBytes = system.Data.Length,
                Segments = [new(system.Offset, system.Packed, system.Data.Length),
                            new(graphics.Offset, graphics.Packed, graphics.Data.Length)]
            });

            long next = (graphics.Offset + graphics.Packed + 15) & ~15L;
            if (next > systemOffset && next - start <= int.MaxValue) p = (int)(next - start) - 16;
        }
        return entries;
    }

    private static bool LooksLikeSystem(byte[] scan, int offset, int? length = null)
    {
        try
        {
            using var input = new MemoryStream(scan, offset, Math.Min(length ?? scan.Length - offset, 65536), false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            Span<byte> prefix = stackalloc byte[64];
            int read = 0;
            while (read < prefix.Length) { int n = zlib.Read(prefix[read..]); if (n == 0) return false; read += n; }
            return RageTextureDecoder.LooksLikeDictionary(prefix);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { return false; }
    }

    private static bool ZeroPadding(FileStream file, long from, long to)
    {
        if (from < 0 || to < from || to - from > 15 || to > file.Length - 6) return false;
        file.Position = from; Span<byte> padding = stackalloc byte[15]; int length = (int)(to - from);
        file.ReadExactly(padding[..length]); return padding[..length].IndexOfAnyExcept((byte)0) < 0;
    }

    private static bool TryInflate(FileStream file, long offset, int maximum, ref long budget, out Block block,
        CancellationToken cancellationToken = default)
    {
        block = new(0, 0, []);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximum < 1 || offset < 0 || offset > file.Length - 6 || budget >= DecodeBudget) return false;
        try
        {
            file.Position = offset;
            Span<byte> header = stackalloc byte[2]; file.ReadExactly(header);
            if (!ZlibHeader(header)) return false;
            file.Position = offset;
            using var small = new SmallReads(file, offset, maximum + 1024L * 1024);
            using var zlib = new ZLibStream(small, CompressionMode.Decompress, true);
            using var output = new MemoryStream(); byte[] buffer = new byte[8192]; uint a = 1, b = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = zlib.Read(buffer); if (n == 0) break;
                budget += n;
                if (budget > DecodeBudget || output.Length > maximum - n) return false;
                output.Write(buffer, 0, n);

                for (int start = 0; start < n; start += 4096)
                {
                    for (int i = start; i < Math.Min(start + 4096, n); i++) { a += buffer[i]; b += a; }
                    a %= 65521; b %= 65521;
                }
            }
            if (output.Length < 8) return false;
            long approximateEnd = file.Position, searchStart = Math.Max(offset + 2, approximateEnd - 260);
            int length = checked((int)(approximateEnd - searchStart));
            byte[] tail = new byte[length]; file.Position = searchStart; file.ReadExactly(tail);
            uint adler = (b << 16) | a; long packed = -1;
            for (int i = 0; i + 4 <= tail.Length; i++)
                if (BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(i, 4)) == adler)
                {

                    long candidate = searchStart + i + 4 - offset;
                    if (!ValidateExact(file, offset, candidate, output.GetBuffer().AsSpan(0, (int)output.Length), cancellationToken)) continue;
                    packed = candidate; break;
                }
            if (packed < 0) return false;
            block = new(offset, packed, output.ToArray()); return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OverflowException)
        { return false; }
    }

    private static bool ValidateExact(FileStream file, long offset, long packed, ReadOnlySpan<byte> expected,
        CancellationToken cancellationToken = default)
    {
        file.Position = offset;
        using var limited = new SmallReads(file, offset, packed);
        using var zlib = new ZLibStream(limited, CompressionMode.Decompress, true);
        byte[] buffer = new byte[8192]; int length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = zlib.Read(buffer); if (read == 0) break;
            if (read > expected.Length - length || !buffer.AsSpan(0, read).SequenceEqual(expected.Slice(length, read))) return false;
            length += read;
        }
        return length == expected.Length;
    }

    private static bool ZlibHeader(ReadOnlySpan<byte> bytes) => bytes.Length >= 2 &&
        (bytes[0] & 15) == 8 && (bytes[0] >> 4) <= 7 && ((bytes[0] << 8) + bytes[1]) % 31 == 0 && (bytes[1] & 32) == 0;

    private sealed class SmallReads(Stream inner, long origin, long maximum) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => maximum;
        public override long Position { get => inner.Position - origin; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Bound(count));
        public override int Read(Span<byte> buffer) => inner.Read(buffer[..Bound(buffer.Length)]);
        private int Bound(int count) => (int)Math.Max(0, Math.Min(Math.Min(count, 256), maximum - (inner.Position - origin)));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
