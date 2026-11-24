using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RpfPreview.Core;






public static class Rpf9ShaderRecovery
{
    public const int MaxShaderBytes = 8 * 1024 * 1024;
    public const int MaxStoredBytes = 1024 * 1024;
    public const int MaxRecoveredShaders = 100_000;
    public const long DecodeBudget = 512L * 1024 * 1024;
    private const int ScanBlock = 4 * 1024 * 1024, PrefixBytes = 262;
    private const uint WrapperMagic = 0x000DDCE7;

    public static IReadOnlyList<ArchiveEntry> Recover(string path, long fileSize, byte[] header, int firstIndex)
    {
        if (header.Length < 64 || !header.AsSpan(0, 4).SequenceEqual("9FPR"u8)) return [];
        return RecoverRange(path, fileSize, 0, Math.Min(fileSize, 16 * 1024 * 1024), firstIndex);
    }








    public static IReadOnlyList<ArchiveEntry> RecoverRange(string path, long fileSize, long scanStart, long scanLength,
        int firstIndex = 0, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (scanStart < 0 || scanLength < 0 || firstIndex < 0) throw new ArgumentOutOfRangeException(nameof(scanStart));
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ScanBlock, FileOptions.SequentialScan);
        if (file.Length != fileSize) throw new IOException("The archive changed while it was being opened.");
        if (scanStart > fileSize) throw new ArgumentOutOfRangeException(nameof(scanStart));
        Span<byte> magic = stackalloc byte[4];
        if (fileSize < 4) return [];
        file.ReadExactly(magic); if (!magic.SequenceEqual("9FPR"u8)) return [];
        long end = Math.Min(fileSize, checked(scanStart + scanLength)), position = scanStart, budget = 0;
        var result = new List<ArchiveEntry>(); byte[] buffer = new byte[ScanBlock + PrefixBytes + MaxStoredBytes];
        while (position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long blockEnd = Math.Min(end, position + ScanBlock), windowStart = Math.Max(0, position - PrefixBytes);
            int count = checked((int)Math.Min(buffer.Length, end - windowStart));
            file.Position = windowStart; file.ReadExactly(buffer.AsSpan(0, count));
            int begin = checked((int)(position - windowStart)), stop = checked((int)(blockEnd - windowStart));
            for (int at = begin; at < stop && at <= count - 12; at++)
            {
                if ((at & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (buffer[at] != 0xE7 || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at)) != WrapperMagic) continue;
                if (!TryHeader(buffer.AsSpan(0, count), at, out int stored, out int plain, out string name)) continue;
                if (result.Count >= MaxRecoveredShaders)
                    throw new InvalidDataException("The recovered shader count limit was reached; scan smaller overlapping ranges.");
                if (plain > DecodeBudget - budget)
                    throw new InvalidDataException("The shader decode budget was reached; scan smaller overlapping ranges.");
                ReadOnlySpan<byte> raw = buffer.AsSpan(at + 12, stored - 12);
                if (!TryDecode(raw, plain, out byte[] decoded, ref budget) || !ValidateDxbc(decoded)) continue;
                long offset = checked(windowStart + at + 12);
                string safe = name.Length > 180 ? name[..180] : name;
                result.Add(new($"recovered/shaders/{safe}_at_{offset:X16}.cso", raw.Length, plain,
                    offset, false, false, false, "Recovered raw deflate shader", checked(firstIndex + result.Count)));
            }
            position = blockEnd; progress?.Report(position);
        }
        return result;
    }

    private static bool TryHeader(ReadOnlySpan<byte> bytes, int at, out int stored, out int plain, out string name)
    {
        stored = plain = 0; name = "";
        if (at < 5 || at > bytes.Length - 12 || bytes[at - 5] is 0 or >= 128) return false;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
        uint decoded = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]);
        if (size is <= 12 or > MaxStoredBytes || decoded is < 32 or > MaxShaderBytes || size > bytes.Length - at ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at - 4)..]) != size) return false;
        for (int length = 10; length <= 255; length++)
        {
            int prefix = at - 6 - length;
            if (prefix < 0) break;
            if (bytes[prefix] != length || bytes[prefix + length] != 0) continue;
            ReadOnlySpan<byte> text = bytes.Slice(prefix + 1, length - 1);
            if (!text.EndsWith("_Wrapped"u8)) continue;
            bool valid = true;
            foreach (byte value in text)
                if (!(value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or
                    >= (byte)'0' and <= (byte)'9' or (byte)'_')) { valid = false; break; }
            if (!valid) continue;
            name = Encoding.ASCII.GetString(text); stored = (int)size; plain = (int)decoded; return true;
        }
        return false;
    }

    private static bool TryDecode(ReadOnlySpan<byte> raw, int length, out byte[] decoded, ref long budget)
    {
        decoded = [];



        if (!ValidateDeflate(raw, length)) return false;
        try
        {
            using var input = new TailExactReads(raw.ToArray());
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            byte[] result = new byte[length]; int at = 0;
            while (at < result.Length)
            {
                int count = deflate.Read(result.AsSpan(at)); if (count == 0) return false;
                budget += count; if (budget > DecodeBudget) return false; at += count;
            }




            if (deflate.ReadByte() != -1 || input.Position != raw.Length) return false;
            decoded = result; return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { return false; }
    }

    private static readonly int[] LengthBase = [3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258];
    private static readonly int[] LengthExtra = [0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0];
    private static readonly int[] DistanceBase = [1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577];
    private static readonly int[] DistanceExtra = [0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13];
    private static readonly Huffman FixedLiteral = MakeFixedLiteral();
    private static readonly Huffman FixedDistance = new(Enumerable.Repeat((byte)5,32).ToArray());

    private static Huffman MakeFixedLiteral()
    {
        byte[] lengths = new byte[288];
        Array.Fill(lengths,(byte)8,0,144); Array.Fill(lengths,(byte)9,144,112);
        Array.Fill(lengths,(byte)7,256,24); Array.Fill(lengths,(byte)8,280,8); return new(lengths);
    }



    private static bool ValidateDeflate(ReadOnlySpan<byte> raw, int expected)
    {
        try
        {
            var bits = new Bits(raw); int output = 0, blocks = 0; bool final;
            do
            {
                if (++blocks > 16384) return false;
                final = bits.Read(1) != 0; int type = bits.Read(2);
                if (type == 0)
                {
                    bits.Align(); int length = bits.Read(16), inverse = bits.Read(16);
                    if ((length ^ inverse) != 65535 || length > expected - output) return false;
                    bits.SkipBytes(length); output += length; continue;
                }
                if (type == 3) return false;
                Huffman literal = FixedLiteral, distance = FixedDistance;
                if (type == 2)
                {
                    int literals = bits.Read(5) + 257, distances = bits.Read(5) + 1, codes = bits.Read(4) + 4;
                    if (literals > 286) return false;
                    ReadOnlySpan<int> order = [16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15];
                    byte[] codeLengths = new byte[19];
                    for (int i = 0; i < codes; i++) codeLengths[order[i]] = (byte)bits.Read(3);
                    var codeTree = new Huffman(codeLengths); byte[] lengths = new byte[literals + distances]; int at = 0;
                    while (at < lengths.Length)
                    {
                        int symbol = bits.Symbol(codeTree);
                        if (symbol <= 15) { lengths[at++] = (byte)symbol; continue; }
                        int repeat; byte value = 0;
                        if (symbol == 16) { if (at == 0) return false; value = lengths[at - 1]; repeat = bits.Read(2) + 3; }
                        else if (symbol == 17) repeat = bits.Read(3) + 3;
                        else if (symbol == 18) repeat = bits.Read(7) + 11;
                        else return false;
                        if (repeat > lengths.Length - at) return false;
                        Array.Fill(lengths,value,at,repeat); at += repeat;
                    }
                    if (lengths[256] == 0) return false;
                    literal = new(lengths.AsSpan(0,literals)); distance = new(lengths.AsSpan(literals),true);
                }
                while (true)
                {
                    int symbol = bits.Symbol(literal);
                    if (symbol < 256) { if (++output > expected) return false; continue; }
                    if (symbol == 256) break;
                    if (symbol is < 257 or > 285) return false;
                    int index = symbol - 257, length = LengthBase[index] + bits.Read(LengthExtra[index]);
                    int ds = bits.Symbol(distance); if (ds > 29) return false;
                    int back = DistanceBase[ds] + bits.Read(DistanceExtra[ds]);
                    if (back > output || length > expected - output) return false;
                    output += length;
                }
            } while (!final);
            return output == expected && (bits.Position + 7) / 8 == raw.Length;
        }
        catch (InvalidDataException) { return false; }
    }

    private sealed class Huffman
    {
        public readonly ushort[] Table;
        public readonly int Width;
        public Huffman(ReadOnlySpan<byte> lengths, bool allowEmpty = false)
        {
            Span<int> counts = stackalloc int[16], next = stackalloc int[16]; int symbols = 0;
            foreach (byte length in lengths)
            {
                if (length > 15) throw new InvalidDataException("Invalid DEFLATE code length.");
                if (length > 0) { counts[length]++; symbols++; Width = Math.Max(Width,length); }
            }
            if (symbols == 0) { if (!allowEmpty) throw new InvalidDataException("Empty DEFLATE tree."); Table = [0]; return; }
            int left = 1, code = 0;
            for (int i = 1; i <= 15; i++)
            {
                left = (left << 1) - counts[i]; if (left < 0) throw new InvalidDataException("Oversubscribed DEFLATE tree.");
                code = (code + counts[i-1]) << 1; next[i] = code;
            }
            Table = new ushort[1 << Width];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int size = lengths[symbol]; if (size == 0) continue;
                int canonical = next[size]++, reverse = 0;
                for (int i = 0; i < size; i++) { reverse = (reverse << 1) | (canonical & 1); canonical >>= 1; }
                ushort value = (ushort)((size << 9) | (symbol + 1));
                for (int i = reverse; i < Table.Length; i += 1 << size) Table[i] = value;
            }
        }
    }

    private ref struct Bits(ReadOnlySpan<byte> raw)
    {
        private readonly ReadOnlySpan<byte> data = raw;
        public int Position { get; private set; }
        public int Read(int count)
        {
            if (count < 0 || count > 16 || count > data.Length * 8 - Position) throw new InvalidDataException("Truncated DEFLATE bits.");
            int result = Peek(count); Position += count; return result;
        }
        private int Peek(int count)
        {
            if (count == 0) return 0; int at = Position >> 3, shift = Position & 7, bytes = (shift + count + 7) >> 3;
            uint value = 0; for (int i = 0; i < bytes; i++) value |= (uint)data[at+i] << (i*8);
            return (int)((value >> shift) & ((1U << count)-1));
        }
        public int Symbol(Huffman tree)
        {
            int available = Math.Min(tree.Width,data.Length * 8 - Position);
            if (available == 0) throw new InvalidDataException("Truncated or empty DEFLATE tree.");
            int value = tree.Table[Peek(available)], size = value >> 9;
            if (size == 0 || size > available) throw new InvalidDataException("Invalid or truncated DEFLATE symbol.");
            Position += size; return (value & 511)-1;
        }
        public void Align() => Position = (Position + 7) & ~7;
        public void SkipBytes(int count)
        {
            if ((Position & 7) != 0 || count < 0 || count > (data.Length * 8 - Position) / 8) throw new InvalidDataException("Truncated stored DEFLATE block.");
            Position += count * 8;
        }
    }


    public static bool ValidateDxbc(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 32 || bytes.Length > MaxShaderBytes || !bytes[..4].SequenceEqual("DXBC"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) != bytes.Length) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        if (count is 0 or > 64 || 32 + count * 4 > bytes.Length) return false;
        Span<int> offsets = stackalloc int[(int)count];
        for (int i = 0; i < offsets.Length; i++)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(32 + i * 4)..]);
            if (offset < 32 + count * 4 || offset > bytes.Length - 8 || (offset & 3) != 0) return false;
            offsets[i] = (int)offset;
        }
        offsets.Sort(); int previousEnd = 32 + offsets.Length * 4;
        foreach (int at in offsets)
        {
            if (at < previousEnd) return false;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
            if (size > bytes.Length - at - 8) return false;
            for (int i = 0; i < 4; i++) if (bytes[at + i] is < 32 or > 126) return false;
            previousEnd = checked(at + 8 + (int)size);
        }
        return bytes.Length - previousEnd <= 3 && bytes[previousEnd..].IndexOfAnyExcept((byte)0) < 0;
    }

    private sealed class TailExactReads(byte[] data) : MemoryStream(data, false)
    {
        private int Bound(int count) => (int)Math.Max(0, Math.Min(count,
            Position < Length - 256 ? Math.Min(256, Length - 256 - Position) : 1));
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Bound(count));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Bound(buffer.Length)]);
    }
}
