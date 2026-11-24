using System.Buffers.Binary;

namespace RpfPreview.Core;

public sealed record PsinSection(string Tag, int Offset, int Length);

public sealed record PsinInspection(int Length, IReadOnlyList<PsinSection> Sections)
{
    public uint StoredChecksum { get; init; }
    public uint PlatformTag { get; init; }


    public bool InternalChecksumVerified => false;
}





public static class PsinMetadata
{
    private const int MaxLength = 16 * 1024 * 1024;
    private static readonly string[] Tags = { "PSIN", "LSCH", "LENM", "LSNS", "LGID", "LRSC", "CHKS" };

    public static PsinInspection? Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 68 || data.Length > MaxLength || !data[..4].SequenceEqual("PSIN"u8)) return null;
        var sections = new PsinSection[Tags.Length];
        int position = 0;
        for (int i = 0; i < Tags.Length; i++)
        {
            if (position > data.Length - 8) return null;
            ReadOnlySpan<byte> tag = data.Slice(position, 4);
            string expected = Tags[i];
            for (int c = 0; c < 4; c++) if (tag[c] != expected[c]) return null;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position + 4, 4));
            if (length < 8 || length > data.Length - position) return null;
            if (i == Tags.Length - 1 && length != 20) return null;
            sections[i] = new(expected, position, checked((int)length));
            position += (int)length;
        }
        if (position != data.Length) return null;
        int trailer = sections[^1].Offset;
        if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(trailer + 8, 4)) != data.Length) return null;
        return new(data.Length, Array.AsReadOnly(sections))
        {
            StoredChecksum = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(trailer + 12, 4)),
            PlatformTag = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(trailer + 16, 4))
        };
    }
}
