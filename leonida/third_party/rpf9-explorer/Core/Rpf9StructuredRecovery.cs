using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace RpfPreview.Core;


internal static class Rpf9StructuredRecovery
{
    internal const int MaxScanBytes = 16 * 1024 * 1024;
    private const int MaxOutput = 16 * 1024 * 1024;
    private const long DecodeBudget = 128L * 1024 * 1024;
    private const int XmlBlockBytes = 65536;
    private const int MaxMeasuredStreams = 16_384;
    private const int MaxEntries = 2048;
    private const int MaxXmlDepth = 128;

    private sealed record MeasuredStream(int Offset, int Packed, int Unpacked, byte[] Prefix);

    internal static IReadOnlyList<ArchiveEntry> Recover(string path, long fileSize, byte[] header, int firstIndex)
    {
        var entries = new List<ArchiveEntry>();
        if (header.Length < 64) return entries;
        long metadataEnd = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (metadataEnd < 64 || metadataEnd > fileSize || metadataEnd > 16 * 1024 * 1024) return entries;


        long start = metadataEnd;
        if (start >= fileSize) return entries;
        byte[] sample = new byte[(int)Math.Min(fileSize - start, MaxScanBytes)];
        using (var file = File.OpenRead(path)) { file.Position = start; file.ReadExactly(sample); }
        long decodedBudget = 0;
        var measured = new List<MeasuredStream>();
        for (int p = 0; p + 6 <= sample.Length && measured.Count < MaxMeasuredStreams && decodedBudget < DecodeBudget; p++)
        {
            byte cmf = sample[p], flg = sample[p + 1];
            if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) + flg) % 31 != 0 || (flg & 32) != 0) continue;
            if (!TryMeasure(sample, p, ref decodedBudget, out int packed, out int unpacked, out byte[] prefix)) continue;
            measured.Add(new(p, packed, unpacked, prefix));
            p += packed - 1;
        }




        var byOffset = measured.ToDictionary(block => block.Offset);
        var consumed = new HashSet<int>();
        foreach (var first in measured)
        {
            if (entries.Count >= MaxEntries) break;
            if (consumed.Contains(first.Offset)) continue;
            bool isPsin = LooksPsin(first.Prefix);
            bool isXml = !isPsin && LooksXml(first.Prefix);
            if (!isPsin && !isXml) continue;
            var blocks = new List<MeasuredStream> { first };
            long unpacked = first.Unpacked;
            string rootName = "PSIN";
            bool valid = isPsin ? ValidatePsin(sample, blocks, unpacked)
                : ValidateXml(sample, blocks, unpacked, out rootName);
            if (!valid && first.Unpacked == XmlBlockBytes && (isPsin || DeclaresXml(first.Prefix)))
            {





                var current = first;
                while (current.Unpacked == XmlBlockBytes && blocks.Count <= MaxOutput / XmlBlockBytes)
                {
                    int end = checked(current.Offset + current.Packed);
                    int next = checked((end + 15) & ~15);
                    if (next > sample.Length ||
                        sample.AsSpan(end, next - end).IndexOfAnyExcept((byte)0) >= 0 ||
                        !byOffset.TryGetValue(next, out var continuation) ||
                        consumed.Contains(next) || DeclaresXml(continuation.Prefix) ||
                        (isPsin && LooksPsin(continuation.Prefix)) ||
                        continuation.Unpacked > XmlBlockBytes ||
                        continuation.Unpacked > MaxOutput - unpacked) break;
                    blocks.Add(continuation);
                    unpacked += continuation.Unpacked;
                    current = continuation;
                }
                if (blocks.Count > 1) valid = isPsin ? ValidatePsin(sample, blocks, unpacked)
                    : ValidateXml(sample, blocks, unpacked, out rootName);
            }
            if (!valid) continue;

            long offset = start + first.Offset;
            long packed = blocks.Sum(block => (long)block.Packed);
            string safeRoot = new(rootName.Take(64).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
            string memberPath = isPsin ? $"recovered/metadata_PSIN_at_{offset:X16}.psin"
                : $"recovered/xml_{safeRoot}_at_{offset:X16}.xml";
            entries.Add(new(memberPath, packed, unpacked, offset,
                false, false, false, "Recovered zlib", firstIndex + entries.Count)
            {
                Segments = blocks.Count > 1
                    ? blocks.Select(block => new ArchiveSegment(start + block.Offset, block.Packed, block.Unpacked)).ToArray()
                    : null
            });
            foreach (var block in blocks) consumed.Add(block.Offset);
        }
        return entries;
    }

    private static bool TryMeasure(byte[] sample, int offset, ref long budget, out int packed, out int unpacked, out byte[] prefix)
    {
        packed = unpacked = 0; prefix = Array.Empty<byte>();
        try
        {
            using var input = new MemoryStream(sample, offset, sample.Length - offset, false);
            using var smallReads = new SmallReads(input);
            using var zlib = new ZLibStream(smallReads, CompressionMode.Decompress, true);
            using var preview = new MemoryStream(512);
            byte[] buffer = new byte[4096]; uint a = 1, b = 0;
            while (true)
            {
                int read = zlib.Read(buffer);
                if (read == 0) break;
                budget += read;
                if (budget > DecodeBudget || unpacked > MaxOutput - read) return false;
                if (preview.Length < 512) preview.Write(buffer, 0, (int)Math.Min(read, 512 - preview.Length));
                for (int i = 0; i < read; i++) { a += buffer[i]; b += a; }
                a %= 65521; b %= 65521;
                unpacked += read;
            }
            if (unpacked < 8) return false;
            uint adler = (b << 16) | a;
            int approximateEnd = checked(offset + (int)input.Position);
            int minimum = Math.Max(offset + 2, approximateEnd - 260);
            int checksumAt = -1;
            for (int i = minimum; i + 4 <= approximateEnd; i++)
                if (BinaryPrimitives.ReadUInt32BigEndian(sample.AsSpan(i, 4)) == adler)
                { checksumAt = i; break; }
            if (checksumAt < 0) return false;
            packed = checksumAt + 4 - offset;

            using var exact = new MemoryStream(sample, offset, packed, false);
            using var check = new ZLibStream(exact, CompressionMode.Decompress);
            long length = 0;
            while (true)
            {
                int read = check.Read(buffer); if (read == 0) break;
                length += read;
                if (length > unpacked) return false;
            }
            if (length != unpacked) return false;
            prefix = preview.ToArray(); return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { return false; }
    }

    private static string PrefixText(byte[] prefix)
    {
        Encoding encoding = prefix.Length >= 2 && prefix[0] == 0xff && prefix[1] == 0xfe ? Encoding.Unicode
            : prefix.Length >= 2 && prefix[0] == 0xfe && prefix[1] == 0xff ? Encoding.BigEndianUnicode
            : prefix.Length >= 2 && prefix[0] == '<' && prefix[1] == 0 ? Encoding.Unicode
            : prefix.Length >= 2 && prefix[0] == 0 && prefix[1] == '<' ? Encoding.BigEndianUnicode
            : Encoding.UTF8;
        return encoding.GetString(prefix).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
    }

    private static bool LooksXml(byte[] prefix) => PrefixText(prefix).StartsWith('<');
    private static bool DeclaresXml(byte[] prefix) => PrefixText(prefix).StartsWith("<?xml", StringComparison.Ordinal);
    private static bool LooksPsin(byte[] prefix) => prefix.Length >= 8 && prefix.AsSpan(0, 4).SequenceEqual("PSIN"u8);

    private static bool ValidatePsin(byte[] sample, IReadOnlyList<MeasuredStream> blocks, long unpacked)
    {
        try
        {
            if (unpacked < 68 || unpacked > MaxOutput) return false;
            var segments = blocks.Select(block => new ArchiveSegment(block.Offset, block.Packed, block.Unpacked)).ToArray();
            using var stream = new ZlibSegmentsStream(segments,
                segment => new MemoryStream(sample, checked((int)segment.Offset), checked((int)segment.StoredSize), false));
            using var output = new MemoryStream(checked((int)unpacked));
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = stream.Read(buffer);
                if (read == 0) break;
                if (output.Length > unpacked - read) return false;
                output.Write(buffer, 0, read);
            }
            return output.Length == unpacked &&
                PsinMetadata.Inspect(output.GetBuffer().AsSpan(0, checked((int)output.Length))) is not null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { return false; }
    }

    private static bool ValidateXml(byte[] sample, IReadOnlyList<MeasuredStream> blocks, long unpacked, out string rootName)
    {
        rootName = "document";
        try
        {
            if (unpacked <= 0 || unpacked > MaxOutput) return false;
            var segments = blocks.Select(block => new ArchiveSegment(block.Offset, block.Packed, block.Unpacked)).ToArray();
            using var stream = new ZlibSegmentsStream(segments,
                segment => new MemoryStream(sample, checked((int)segment.Offset), checked((int)segment.StoredSize), false));
            using var xml = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = Math.Min(MaxOutput, unpacked * 2L + 1024),
                MaxCharactersFromEntities = 1024,
                ConformanceLevel = ConformanceLevel.Document
            });
            bool sawRoot = false, closedRoot = false;
            while (xml.Read())
            {
                if (xml.Depth > MaxXmlDepth) return false;
                if (xml.NodeType == XmlNodeType.Element && xml.Depth == 0)
                {
                    rootName = xml.LocalName; sawRoot = true;
                    if (xml.IsEmptyElement) closedRoot = true;
                }
                else if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == 0) closedRoot = true;
            }
            return sawRoot && closedRoot;
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or ArgumentException) { return false; }
    }

    private sealed class SmallReads(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 256));
        public override int Read(Span<byte> buffer) => inner.Read(buffer[..Math.Min(buffer.Length, 256)]);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
