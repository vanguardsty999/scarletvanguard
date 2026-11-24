using System.Buffers.Binary;
using System.IO.Compression;

namespace RpfPreview.Core;







public static class Rpf9ModelRecovery
{
    public const int MaxRecoveredModels = 500_000;
    public const int MaxCandidateOffsets = 500_000;
    public const long DecodeBudget = 1024L * 1024 * 1024;
    private const int ScanBlock = 4 * 1024 * 1024;
    private const int MaxDictionaryCheckBytes = 64 * 1024;
    private const int MaxResourceBlocks = 4096;
    private const int RawSystemTailBytes = 16;
    private const int RawGraphicsTailBytes = 48;
    private sealed record Block(long Offset, long Packed, byte[] Data, bool IsRaw = false);






    public static IReadOnlyList<long> FindCandidateOffsets(string path, long scanStart = 0,
        long? scanLength = null, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => FindCandidateOffsetsCore(path, scanStart, scanLength, progress, cancellationToken, false);

    private static IReadOnlyList<long> FindCandidateOffsetsCore(string path, long scanStart,
        long? scanLength, IProgress<long>? progress, CancellationToken cancellationToken, bool fragmentsOnly)
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
                if (offset < scanStart || offset >= end || scan.AsSpan(page, 16).IndexOfAnyExcept((byte)0) >= 0 || !ZlibHeader(scan.AsSpan(page + 16, 2))) continue;
                if (!LooksLikeSystem(scan, page + 16, readCount - page - 16, fragmentsOnly)) continue;
                if (candidates.Count >= MaxCandidateOffsets) throw new InvalidDataException("The candidate limit was reached; scan a smaller range.");
                candidates.Add(offset);
            }
            position += count; progress?.Report(position);
        }
        return candidates;
    }

    public static IReadOnlyList<long> FindFragmentCandidateOffsets(string path, long scanStart = 0,
        long? scanLength = null, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => FindCandidateOffsetsCore(path, scanStart, scanLength, progress, cancellationToken, true);

    public static IReadOnlyList<ArchiveEntry> Recover(string path, long fileSize, byte[] header, int firstIndex)
    {
        if (header.Length < 64 || !header.AsSpan(0, 4).SequenceEqual("9FPR"u8)) return [];
        return RecoverAtOffsets(path, fileSize, FindCandidateOffsets(path, 0, Math.Min(fileSize, 16 * 1024 * 1024)), firstIndex);
    }







    public static IReadOnlyList<ArchiveEntry> RecoverAtOffsets(string path, long fileSize,
        IReadOnlyList<long> candidateOffsets, int firstIndex = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateOffsets);
        if (firstIndex < 0 || candidateOffsets.Count > MaxCandidateOffsets) throw new ArgumentOutOfRangeException(nameof(firstIndex));
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != fileSize) throw new IOException("The archive changed while it was being opened.");
        var entries = new List<ArchiveEntry>(); var seen = new HashSet<long>();
        Span<byte> prefix = stackalloc byte[16];
        foreach (long candidate in candidateOffsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(candidate) || candidate < 16 || candidate > fileSize - 6 || (candidate & 4095) != 16) continue;
            file.Position = candidate - 16; file.ReadExactly(prefix);
            long budget = 0;
            if (prefix.IndexOfAnyExcept((byte)0) >= 0 ||
                !TryResource(file, candidate, ref budget, cancellationToken, out var blocks, out int systemLength,
                    out var models, out bool dictionary)) continue;
            if (models.Count > MaxRecoveredModels - entries.Count)
                throw new InvalidDataException("The recovered model limit was reached; recover a smaller batch.");
            long stored = 0, decoded = 0; var segments = new List<ArchiveSegment>(blocks.Count);
            foreach (var block in blocks)
            { stored = checked(stored + block.Packed); decoded = checked(decoded + block.Data.Length); segments.Add(new(block.Offset, block.Packed, block.Data.Length) { IsRaw = block.IsRaw }); }
            foreach (var model in models)
            {
                string safeName = SafeName(model.Name, dictionary);
                string child = dictionary ? $"_child_{model.DrawableOffset:X8}" : "";
                entries.Add(new($"recovered/models/{safeName}_at_{candidate:X16}{child}.r9modeldata", stored,
                    decoded, candidate, false, false, false,
                    "Recovered model", checked(firstIndex + entries.Count))
                {
                    Segments = segments, ResourceSystemBytes = systemLength,
                    ResourceDrawableOffset = dictionary ? model.DrawableOffset : null
                });
            }
        }
        return entries;
    }

    private static bool TryResource(FileStream file, long candidate, ref long budget, CancellationToken cancellationToken,
        out List<Block> blocks, out int systemLength, out IReadOnlyList<RageModel> models, out bool dictionary)
    {
        blocks = []; systemLength = 0; models = []; dictionary = false;
        if (!TryInflate(file, candidate, RageModelDecoder.MaxSystemBytes, ref budget, out var first, cancellationToken) ||
            !RageModelDecoder.LooksLikeResource(first.Data)) return false;
        dictionary = RageModelDecoder.LooksLikeDrawableDictionary(first.Data);
        blocks.Add(first); int total = first.Data.Length;
        const int maximum = RageModelDecoder.MaxSystemBytes + RageModelDecoder.MaxGraphicsBytes;
        while (blocks.Count <= MaxResourceBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool needsMore = true;
            if (!(blocks[^1].IsRaw && blocks[^1].Data.Length == RawSystemTailBytes) &&
                TrySplit(blocks, total, dictionary, cancellationToken, out systemLength, out models, out needsMore)) return true;
            if (!needsMore || blocks.Count == MaxResourceBlocks || total >= maximum ||
                (!TryNext(file, blocks, maximum - total, ref budget, out var next, cancellationToken) &&
                 !TryRawTail(file, blocks, total, dictionary, ref budget, out next, cancellationToken))) return false;
            blocks.Add(next); total = checked(total + next.Data.Length);
        }
        return false;
    }

    private static bool TrySplit(List<Block> blocks, int total, bool dictionary, CancellationToken cancellationToken,
        out int systemLength, out IReadOnlyList<RageModel> models, out bool needsMore)
    {
        systemLength = 0; models = []; needsMore = false; int prefixLength = 0;
        for (int split = 1; split <= blocks.Count; split++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            prefixLength = checked(prefixLength + blocks[split - 1].Data.Length);
            if (prefixLength > RageModelDecoder.MaxSystemBytes) break;
            byte[] system = Join(blocks, 0, split, prefixLength);
            RageModelExtent? extent = RageModelDecoder.ProbeRequiredExtents(system);
            if (extent is null) continue;
            if (!extent.MetadataComplete) { needsMore = true; continue; }
            if (extent.RequiredSystemBytes > prefixLength) continue;
            int graphicsLength = total - prefixLength;
            if (graphicsLength < extent.RequiredGraphicsBytes) { needsMore = true; continue; }
            if (graphicsLength > RageModelDecoder.MaxGraphicsBytes) continue;
            byte[] graphics = Join(blocks, split, blocks.Count, graphicsLength);


            if (!TryDecodeResource(system, graphics, dictionary, out models)) continue;
            systemLength = prefixLength; return true;
        }
        return false;
    }

    private static bool TryDecodeResource(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics,
        bool dictionary, out IReadOnlyList<RageModel> models)
    {
        models = [];
        if (dictionary)
        {
            var owned = RageModelDecoder.InspectDrawableDictionary(system);
            if (owned is null || owned.Count == 0) return false;
            var decoded = RageModelDecoder.DecodeModels(system, graphics);
            if (decoded.Count != owned.Count || decoded.Where((model, index) =>
                model.DrawableOffset != owned[index].DrawableOffset || model.Name != owned[index].Name).Any()) return false;
            models = decoded; return true;
        }
        var model = RageModelDecoder.Decode(system, graphics);
        if (model is null) return false;
        models = [model]; return true;
    }

    private static byte[] Join(List<Block> blocks, int begin, int end, int length)
    {
        byte[] result = new byte[length]; int copied = 0;
        for (int i = begin; i < end; i++) { blocks[i].Data.CopyTo(result, copied); copied += blocks[i].Data.Length; }
        return result;
    }

    private static string SafeName(string name, bool preserveNativeHash = false)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ||
            preserveNativeHash && c == '#' ? c : '_').ToArray();
        string result = new(chars); result = result.Trim('.');
        return result.Length == 0 ? "drawable" : result[..Math.Min(160, result.Length)];
    }

    private static bool TryNext(FileStream file, List<Block> blocks, int maximum, ref long budget, out Block block,
        CancellationToken cancellationToken)
    {
        block = new(0, 0, []);
        long end = checked(blocks[^1].Offset + blocks[^1].Packed), next = (end + 15) & ~15L;
        return ZeroPadding(file, end, next) && TryInflate(file, next, maximum, ref budget, out block, cancellationToken);
    }

    private static bool TryRawTail(FileStream file, List<Block> blocks, int total, bool dictionary, ref long budget, out Block block,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        block = new(0, 0, []);
        if (blocks[^1].IsRaw || (blocks[^1].Data.Length & 0xFFFF) != 0) return false;
        long end = checked(blocks[^1].Offset + blocks[^1].Packed), next = (end + 15) & ~15L;
        if (!ZeroPadding(file, end, next)) return false;
        Span<byte> header = stackalloc byte[2];
        file.Position = next;
        file.ReadExactly(header);
        if (ZlibHeader(header)) return false;

        if (total <= RageModelDecoder.MaxSystemBytes - RawSystemTailBytes && next <= file.Length - RawSystemTailBytes - 2)
        {
            byte[] system = Join(blocks, 0, blocks.Count, total);
            var incomplete = RageModelDecoder.ProbeRequiredExtents(system);
            if (incomplete is { MetadataComplete: false } && incomplete.RequiredSystemBytes > total &&
                incomplete.RequiredSystemBytes - total <= RawSystemTailBytes)
            {
                file.Position = next + RawSystemTailBytes;
                file.ReadExactly(header);
                if (ZlibHeader(header) && budget <= DecodeBudget - RawSystemTailBytes)
                {
                    file.Position = next;
                    byte[] tail = new byte[RawSystemTailBytes];
                    file.ReadExactly(tail);
                    byte[] combined = [.. system, .. tail];
                    if (RageModelDecoder.ProbeRequiredExtents(combined) is { MetadataComplete: true })
                    {
                        budget += RawSystemTailBytes;
                        block = new(next, RawSystemTailBytes, tail, true);
                        return true;
                    }
                }
            }
        }

        int prefixLength = 0;
        for (int split = 1; split < blocks.Count; split++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            prefixLength = checked(prefixLength + blocks[split - 1].Data.Length);
            if (prefixLength > RageModelDecoder.MaxSystemBytes) break;
            byte[] system = Join(blocks, 0, split, prefixLength);
            if (RageModelDecoder.ProbeRequiredExtents(system) is not { MetadataComplete: true } extent) continue;
            int graphicsLength = total - prefixLength;
            int remaining = extent.RequiredGraphicsBytes - graphicsLength;
            if (remaining != RawGraphicsTailBytes || next > file.Length - remaining || budget > DecodeBudget - remaining) continue;
            file.Position = next;
            byte[] tail = new byte[remaining];
            file.ReadExactly(tail);
            byte[] graphics = [.. Join(blocks, split, blocks.Count, graphicsLength), .. tail];
            if (!TryDecodeResource(system, graphics, dictionary, out _)) continue;
            budget += remaining;
            block = new(next, remaining, tail, true);
            return true;
        }
        return false;
    }

    private static bool LooksLikeSystem(byte[] bytes, int offset, int length, bool fragmentsOnly)
    {
        try
        {
            using var input = new MemoryStream(bytes, offset, Math.Min(length, 65536), false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            Span<byte> prefix = stackalloc byte[176];
            int read = 0;
            while (read < prefix.Length) { int n = zlib.Read(prefix[read..]); if (n == 0) return false; read += n; }
            if (fragmentsOnly) return RageModelDecoder.LooksLikeFragment(prefix);
            if (RageModelDecoder.LooksLikeDrawable(prefix) || RageModelDecoder.LooksLikeFragment(prefix)) return true;


            byte[] head = prefix.ToArray();
            for (int needed = RageModelDecoder.DrawableDictionaryCheckBytes(head); needed > head.Length;
                 needed = RageModelDecoder.DrawableDictionaryCheckBytes(head))
            {
                if (needed > MaxDictionaryCheckBytes) return true;
                int have = head.Length;
                Array.Resize(ref head, needed);
                while (have < needed) { int n = zlib.Read(head, have, needed - have); if (n == 0) return true; have += n; }
            }
            return RageModelDecoder.LooksLikeDrawableDictionary(head);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { return false; }
    }

    private static bool ZeroPadding(FileStream file, long from, long to)
    {
        if (from < 0 || to < from || to - from > 15 || to > file.Length - 6) return false;
        file.Position = from; Span<byte> bytes = stackalloc byte[15]; int count = (int)(to - from);
        file.ReadExactly(bytes[..count]); return bytes[..count].IndexOfAnyExcept((byte)0) < 0;
    }

    private static bool TryInflate(FileStream file, long offset, int maximum, ref long budget, out Block block,
        CancellationToken cancellationToken)
    {
        block = new(0, 0, []);
        if (maximum < 1 || offset < 0 || offset > file.Length - 6 || budget >= DecodeBudget) return false;
        try
        {
            file.Position = offset; Span<byte> header = stackalloc byte[2]; file.ReadExactly(header);
            if (!ZlibHeader(header)) return false;
            file.Position = offset;
            using var small = new SmallReads(file, offset, Math.Min(file.Length - offset, maximum + 1024L * 1024));
            using var zlib = new ZLibStream(small, CompressionMode.Decompress, true);
            using var output = new MemoryStream(); byte[] buffer = new byte[8192]; uint a = 1, b = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = zlib.Read(buffer); if (n == 0) break;
                budget += n; if (budget > DecodeBudget || output.Length > maximum - n) return false;
                output.Write(buffer, 0, n);
                for (int start = 0; start < n; start += 4096)
                {
                    for (int i = start; i < Math.Min(start + 4096, n); i++) { a += buffer[i]; b += a; }
                    a %= 65521; b %= 65521;
                }
            }
            if (output.Length < 8) return false;
            long approximateEnd = file.Position, searchStart = Math.Max(offset + 2, approximateEnd - 260);
            byte[] tail = new byte[checked((int)(approximateEnd - searchStart))];
            file.Position = searchStart; file.ReadExactly(tail);
            uint adler = (b << 16) | a; long packed = -1;
            for (int i = 0; i + 4 <= tail.Length; i++)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(i, 4)) != adler) continue;
                long candidate = searchStart + i + 4 - offset;
                if (!ValidateExact(file, offset, candidate, output.GetBuffer().AsSpan(0, (int)output.Length), cancellationToken)) continue;
                packed = candidate; break;
            }
            if (packed < 0) return false;
            block = new(offset, packed, output.ToArray()); return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OverflowException) { return false; }
    }

    private static bool ValidateExact(FileStream file, long offset, long length, ReadOnlySpan<byte> expected,
        CancellationToken cancellationToken)
    {
        file.Position = offset;
        using var limited = new SmallReads(file, offset, length);
        using var zlib = new ZLibStream(limited, CompressionMode.Decompress, true);
        byte[] buffer = new byte[8192]; int at = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = zlib.Read(buffer); if (count == 0) break;
            if (count > expected.Length - at || !buffer.AsSpan(0, count).SequenceEqual(expected.Slice(at, count))) return false;
            at += count;
        }
        return at == expected.Length;
    }

    private static bool ZlibHeader(ReadOnlySpan<byte> bytes) => bytes.Length >= 2 && (bytes[0] & 15) == 8 &&
        bytes[0] >> 4 <= 7 && ((bytes[0] << 8) + bytes[1]) % 31 == 0 && (bytes[1] & 32) == 0;

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
