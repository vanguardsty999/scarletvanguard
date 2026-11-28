using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RpfPreview.Core;


public sealed record AwcStreamInfo(int Index, uint Id, int SampleRate, long Samples,
    int Codec, long DataOffset, long DataLength)
{
    public int Channels { get; init; }
    public long DeclaredInterleavedSamples => Samples;
    public long? OpusFrameCountPerChannel { get; init; }
    public int DecodedSampleRate => Codec == 0x0B && SampleRate != 32000 ? 48000 : SampleRate;
    public double DurationSeconds => DecodedSampleRate > 0
        ? (double)(Codec == 0x0B ? OpusFrameCountPerChannel ?? Samples / Math.Max(1, Channels) : Samples) / DecodedSampleRate : 0;
    public bool IsStreamed { get; init; }
    public int MusicChannel { get; init; } = -1;
    public int BlockCount { get; init; }
    public int BlockSize { get; init; }
    public bool SupportsPreview => DataLength > 0 && Channels is 1 or 2 &&
        ((Codec == 0x0B && SampleRate is >= 1000 and <= 192000) || Codec == 5 ||
         (Codec == 0 && DataLength == Samples * Channels * 2));
}





public sealed record AwcInfo(long Length, IReadOnlyList<AwcStreamInfo> Streams)
{
    public int DeclaredStreams { get; init; }
    public bool HasOpaquePrefixTable { get; init; }
    public uint Flags { get; init; }

    public static AwcInfo? Inspect(string source, long offset, long availableLength)
    {
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.RandomAccess);
        if (offset < 0 || availableLength < 16 || offset > stream.Length || availableLength > stream.Length - offset) return null;
        return AwcRecovery.ReadValidated(stream, offset, availableLength);
    }
}





public static class AwcRecovery
{
    public const long PrefixScanBytes = 128L * 1024 * 1024;
    public const long SuffixScanBytes = 16L * 1024 * 1024;
    private const int PageSize = 4096;
    private const int MaxHeaderBytes = 4 * 1024 * 1024;
    private const int MaxContainers = 8192;
    private const int MaxStreams = 8192;
    private const long HeaderReadBudget = 32L * 1024 * 1024;

    public static IReadOnlyList<ArchiveEntry> Recover(string source, long fileSize, byte[] header, int firstIndex = 0)
    {
        var result = new List<ArchiveEntry>();
        if (fileSize < 16 || header.Length < 4 || !header.AsSpan(0, 4).SequenceEqual("9FPR"u8)) return result;
        var cache = LoadCache(source, firstIndex);
        if (cache is not null) return cache;
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            PageSize, FileOptions.RandomAccess);
        if (stream.Length != fileSize) throw new IOException("The archive changed while audio was being opened.");
        long budget = HeaderReadBudget;
        long firstEnd = Math.Min(fileSize, PrefixScanBytes);
        Scan(0, firstEnd);
        long lastStart = Math.Max(firstEnd, Math.Max(0, fileSize - SuffixScanBytes));
        if (lastStart < fileSize) Scan(lastStart, fileSize);
        return result;

        void Scan(long start, long end)
        {
            const int blockSize = 1024 * 1024;
            byte[] block = new byte[blockSize + 3];
            for (long blockStart = start; blockStart < end && result.Count < MaxContainers && budget >= 16; blockStart += blockSize)
            {
                int count = (int)Math.Min(block.Length, end - blockStart);
                stream.Position = blockStart;
                stream.ReadExactly(block.AsSpan(0, count));
                int search = 0;
                while (search <= count - 4 && result.Count < MaxContainers && budget >= 16)
                {
                    int found = block.AsSpan(search, count - search).IndexOf("ADAT"u8);
                    if (found < 0) break;
                    int within = search + found;
                    if (within >= blockSize) break;
                    long position = checked(blockStart + within);
                    search = within + 1;
                    if (position > end - 16) continue;
                    AwcInfo? info = ReadValidated(stream, position, fileSize - position, ref budget, false);
                    if (info is null) continue;
                    result.Add(new ArchiveEntry($"recovered/audio_{result.Count + 1:0000}_at_{position:X16}.awc",
                        info.Length, info.Length, position, false, false, false, "Recovered AWC", checked(firstIndex + result.Count)));
                }
            }
        }
    }






    public static IReadOnlyList<ArchiveEntry> RecoverFull(string source, IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            PageSize, FileOptions.SequentialScan);
        long length = stream.Length;
        if (length < 4) throw new InvalidDataException("The file is too short to contain an RPF9 header.");
        byte[] signature = new byte[4];
        stream.ReadExactly(signature);
        if (!signature.AsSpan().SequenceEqual("9FPR"u8) && !signature.AsSpan().SequenceEqual("RPF9"u8))
            throw new InvalidDataException("Full AWC recovery requires an RPF9 archive.");

        const int blockSize = 4 * 1024 * 1024;
        byte[] block = new byte[blockSize + 3];
        var entries = new List<ArchiveEntry>();
        progress?.Report(0);
        for (long blockStart = 0; blockStart < length; blockStart += blockSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(block.Length, length - blockStart);
            stream.Position = blockStart;
            stream.ReadExactly(block.AsSpan(0, count));
            int search = 0;
            while (search <= count - 4)
            {
                int found = block.AsSpan(search, count - search).IndexOf("ADAT"u8);
                if (found < 0) break;
                ct.ThrowIfCancellationRequested();
                int within = search + found;
                if (within >= blockSize) break;
                search = within + 1;
                long position = checked(blockStart + within);
                if (position > length - 16) continue;


                AwcInfo? info = ReadValidated(stream, position, length - position, false);
                if (info is null) continue;
                if (entries.Count >= MaxContainers)
                    throw new InvalidDataException($"The archive exceeds the supported limit of {MaxContainers:N0} AWC containers. The full scan was not completed.");
                entries.Add(new ArchiveEntry($"recovered/audio_{entries.Count + 1:0000}_at_{position:X16}.awc",
                    info.Length, info.Length, position, false, false, false, "Recovered AWC", entries.Count));
            }
            progress?.Report(Math.Min(length, checked(blockStart + blockSize)));
        }
        ct.ThrowIfCancellationRequested();
        if (stream.Length != length) throw new IOException("The archive changed during the full audio scan.");
        return entries;
    }

    private sealed record CacheRange(long Offset, long Length);
    private sealed record CacheRecord(int Schema, string Source, long SourceLength, long LastWriteUtcTicks,
        string HeaderSha256, bool CompleteScan, CacheRange[] Ranges);

    public static string CachePath(string source)
    {
        string normalized = Path.GetFullPath(source).ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RpfPreview", "Indexes", key + ".awc.json");
    }


    public static void WriteCache(string source, IEnumerable<ArchiveEntry> entries, bool completeScan = true)
    {
        source = Path.GetFullPath(source);
        var ranges = entries.Select(e => new CacheRange(e.Offset, e.StoredSize)).OrderBy(e => e.Offset).ToArray();
        if (ranges.Length > MaxContainers || ranges.Select(x => x.Offset).Distinct().Count() != ranges.Length)
            throw new InvalidDataException("The AWC cache contains too many or duplicate ranges.");
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, PageSize, FileOptions.RandomAccess);
        long length = stream.Length;
        string hash = HeaderHash(stream);
        long ticks = File.GetLastWriteTimeUtc(source).Ticks;
        foreach (var range in ranges)
        {
            AwcInfo? info = ReadValidated(stream, range.Offset, range.Length, false);
            if (info is null || info.Length != range.Length) throw new InvalidDataException($"AWC cache range at 0x{range.Offset:X} length 0x{range.Length:X} failed validation.");
        }
        var record = new CacheRecord(1, source, length, ticks, hash, completeScan, ranges);
        string path = CachePath(source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, record);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<ArchiveEntry>? LoadCache(string source, int firstIndex = 0)
    {
        CacheRecord? record = ReadCache(source);
        if (record is null) return null;
        return record.Ranges.Select((r, i) => new ArchiveEntry($"recovered/audio_{i + 1:0000}_at_{r.Offset:X16}.awc",
            r.Length, r.Length, r.Offset, false, false, false, "Recovered AWC", checked(firstIndex + i))).ToArray();
    }

    public static bool HasCompleteCache(string source) => ReadCache(source)?.CompleteScan == true;

    private static CacheRecord? ReadCache(string source)
    {
        try
        {
            source = Path.GetFullPath(source);
            string path = CachePath(source);
            var cacheFile = new FileInfo(path);
            if (!cacheFile.Exists || cacheFile.Length > 2 * 1024 * 1024) return null;
            using var input = File.OpenRead(path);
            CacheRecord? record = JsonSerializer.Deserialize<CacheRecord>(input);
            if (record is null || record.Schema != 1 || !string.Equals(record.Source, source, StringComparison.OrdinalIgnoreCase) ||
                record.Ranges is null || record.Ranges.Length > MaxContainers) return null;
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, PageSize, FileOptions.RandomAccess);
            if (record.SourceLength != stream.Length || record.LastWriteUtcTicks != File.GetLastWriteTimeUtc(source).Ticks ||
                !string.Equals(record.HeaderSha256, HeaderHash(stream), StringComparison.Ordinal)) return null;
            long previous = -1;
            foreach (var range in record.Ranges)
            {
                if (range.Offset <= previous || range.Offset < 0 || range.Length < 16 || range.Offset > stream.Length || range.Length > stream.Length - range.Offset) return null;
                previous = range.Offset;
            }
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    private static string HeaderHash(FileStream stream)
    {
        byte[] header = new byte[(int)Math.Min(65536, stream.Length)];
        stream.Position = 0; stream.ReadExactly(header);
        return Convert.ToHexString(SHA256.HashData(header));
    }


    public static void WriteXmaWave(string source, long offset, long availableLength, int streamIndex, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, PageSize, FileOptions.RandomAccess);
        AwcInfo? info = ReadValidated(input, offset, availableLength);
        if (info is null || info.Length != availableLength) throw new InvalidDataException("The AWC span failed validation.");
        AwcStreamInfo audio = info.Streams.SingleOrDefault(s => s.Index == streamIndex)
            ?? throw new InvalidDataException("The selected AWC stream does not contain independent audio.");
        if (audio.Codec != 5 || audio.IsStreamed || audio.Channels is < 1 or > 2 || audio.DataLength > uint.MaxValue - 80)
            throw new InvalidDataException("This stream is not supported nonstreamed XMA2.");
        byte[] h = new byte[80];
        "RIFF"u8.CopyTo(h); Put32(4, checked((uint)(72 + audio.DataLength))); "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        Put32(16, 52); Put16(20, 0x166); Put16(22, (ushort)audio.Channels); Put32(24, (uint)audio.SampleRate);
        Put32(28, (uint)(audio.SampleRate * audio.Channels / 2)); Put16(32, (ushort)(audio.Channels * 2)); Put16(34, 16);
        Put16(36, 34); Put16(38, (ushort)((audio.Channels + 1) / 2)); Put32(40, audio.Channels == 1 ? 4U : 3U);
        Put32(44, checked((uint)(audio.Samples * audio.Channels * 2))); Put32(48, 0x8000); h[69] = 4;
        Put16(70, checked((ushort)((audio.DataLength + 0x7FFF) / 0x8000))); "data"u8.CopyTo(h.AsSpan(72)); Put32(76, (uint)audio.DataLength);
        bool created = false;
        try
        {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true; output.Write(h);
            input.Position = checked(offset + audio.DataOffset);
            byte[] block = new byte[65536]; long remaining = audio.DataLength;
            while (remaining > 0)
            {
                int n = (int)Math.Min(remaining, block.Length); input.ReadExactly(block.AsSpan(0, n)); output.Write(block.AsSpan(0, n)); remaining -= n;
            }
        }
        catch { if (created && File.Exists(destination)) File.Delete(destination); throw; }
        void Put16(int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(p, 2), v);
        void Put32(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(p, 4), v);
    }

    internal static AwcInfo? ReadValidated(FileStream stream, long offset, long available, bool includeStreamDetails = true)
    {
        long budget = MaxHeaderBytes + 16L * MaxStreams + 16;
        return ReadValidated(stream, offset, available, ref budget, includeStreamDetails);
    }

    private static AwcInfo? ReadValidated(FileStream stream, long offset, long available, ref long budget, bool includeStreamDetails = true)
    {
        try
        {
            if (offset < 0 || available < 16 || offset > stream.Length || available > stream.Length - offset || budget < 16) return null;
            byte[] prefix = new byte[16];
            stream.Position = offset; stream.ReadExactly(prefix); budget -= 16;
            if (!prefix.AsSpan(0, 4).SequenceEqual("ADAT"u8)) return null;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4, 4));
            uint countValue = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(8, 4));
            uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12, 4));

            if ((flags & 0xFF00FFFFU) != 0xFF000001U || (flags & 0x00980000U) != 0 ||
                countValue > MaxStreams || headerLength is < 20 or > MaxHeaderBytes ||
                headerLength > available || headerLength > budget) return null;
            int count = (int)countValue;
            byte[] h = new byte[headerLength];
            stream.Position = offset; stream.ReadExactly(h); budget -= headerLength;
            int p = 16 + ((flags & 0x400000) != 0 ? 4 : 0);
            int indexStart = (flags & 0x10000) != 0 ? p : -1;
            if (indexStart >= 0) p = checked(p + count * 2);
            bool extra = (flags & 0x200000) != 0;


            if (extra) p = checked(p + (count + 1) * 4);
            int infoStart = p;
            p = checked(p + count * 4);
            if (p > h.Length) return null;
            int tags = 0;
            uint previousId = 0;
            for (int i = 0; i < count; i++)
            {
                uint packed = U32(h, infoStart + i * 4);
                uint id = packed & 0x1FFFFFFF;
                if (i > 0 && id < previousId) return null;
                previousId = id;
                if (indexStart >= 0 && U16(h, indexStart + i * 2) != tags) return null;
                tags = checked(tags + (int)(packed >> 29));
            }
            if (checked((long)p + tags * 8L) != h.Length) return null;
            long extent = h.Length;
            int tagCursor = p;
            bool anyData = false;
            var audio = new List<AwcStreamInfo>();
            var music = new List<(long Format, long FormatLength, long Data, long DataLength)>();
            for (int i = 0; i < count; i++)
            {
                uint packed = U32(h, infoStart + i * 4);
                int tagCount = (int)(packed >> 29);
                long dataOffset = -1, dataLength = 0, formatOffset = -1, formatLength = 0, musicOffset = -1, musicLength = 0;
                for (int t = 0; t < tagCount; t++, tagCursor += 8)
                {
                    ulong tag = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(tagCursor, 8));
                    byte type = (byte)(tag >> 56);
                    long size = (long)((tag >> 28) & 0x0FFFFFFF), rel = (long)(tag & 0x0FFFFFFF);
                    if (size != 0 && (rel < h.Length || rel > available || size > available - rel)) return null;
                    extent = Math.Max(extent, checked(rel + size));
                    if (type == 0x55)
                    {
                        if (dataOffset >= 0) return null;
                        dataOffset = rel; dataLength = size; anyData |= size > 0;
                    }
                    if (type == 0x48) { if (musicOffset >= 0) return null; musicOffset = rel; musicLength = size; }
                    if (type == 0x76)
                    {
                        if (formatOffset >= 0) return null;
                        formatOffset = rel; formatLength = size;
                    }
                }
                if (!includeStreamDetails) continue;
                if (musicOffset >= 0 && dataOffset >= 0 && dataLength > 0) music.Add((musicOffset, musicLength, dataOffset, dataLength));
                if (formatLength < 32 || dataOffset < 0 || dataLength == 0) continue;
                if (budget < 32) return null;
                byte[] format = new byte[32];
                stream.Position = checked(offset + formatOffset); stream.ReadExactly(format); budget -= 32;
                long samples = U32(format, 0);
                int rate = U16(format, 8), codec = format[28], channels = format[29];
                if (rate is < 1000 or > 192000 || channels is < 1 or > 8 || samples > 192000L * 60 * 60 * 24) return null;
                if (codec == 0x0B && (channels > 2 || dataLength % 2048 != 0)) return null;
                audio.Add(new(i, packed & 0x1FFFFFFF, rate, samples, codec, dataOffset, dataLength) { Channels = channels, OpusFrameCountPerChannel = codec == 0x0B ? (long?)U32(format, 12) : null });
            }
            foreach (var descriptor in music)
            {
                if (descriptor.FormatLength < 12 || descriptor.FormatLength > 12 + 32 * 16 || descriptor.FormatLength > budget) return null;
                byte[] m = new byte[descriptor.FormatLength];
                stream.Position = checked(offset + descriptor.Format); stream.ReadExactly(m); budget -= m.Length;
                uint blockCount = U32(m, 0), blockSize = U32(m, 4), channels = U32(m, 8);
                if (channels is 0 or > 32 || m.Length != 12 + channels * 16 || blockCount is 0 or > 1000000 ||
                    blockSize is < 2048 or > 16 * 1024 * 1024 ||
                    (long)(blockCount - 1) * blockSize >= descriptor.DataLength ||
                    (long)blockCount * blockSize < descriptor.DataLength) return null;
                for (int ch = 0; ch < channels; ch++)
                {
                    int record = 12 + ch * 16;
                    uint id = U32(m, record) & 0x1FFFFFFF;
                    long samples = U32(m, record + 4);
                    int rate = U16(m, record + 10), codec = m[record + 12];
                    if (rate is < 1000 or > 192000 || codec != 0x0B) return null;
                    int index = count + ch;
                    for (int i = 0; i < count; i++) if ((U32(h, infoStart + i * 4) & 0x1FFFFFFF) == id) { index = i; break; }
                    if (audio.Any(x => x.Index == index)) continue;
                    audio.Add(new(index, id, rate, samples, codec, descriptor.Data, descriptor.DataLength)
                    { Channels = 1, IsStreamed = true, MusicChannel = ch, BlockCount = (int)blockCount, BlockSize = (int)blockSize, OpusFrameCountPerChannel = samples });
                }
            }
            return (anyData || count == 0) && extent <= available
                ? new AwcInfo(extent, audio) { DeclaredStreams = count, HasOpaquePrefixTable = extra, Flags = flags }
                : null;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or EndOfStreamException)
        {
            return null;
        }
    }

    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));
    private static ushort U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset, 2));
}
