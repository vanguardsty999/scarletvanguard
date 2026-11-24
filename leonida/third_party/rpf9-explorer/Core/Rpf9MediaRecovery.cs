using System.Buffers.Binary;

namespace RpfPreview.Core;






internal static class Rpf9MediaRecovery
{
    private const int PageSize = 4096;
    private const int MaxRecords = 20_000;
    private const int MaxFrames = 1_000_000;
    private const long ReadBudget = 64L * 1024 * 1024;
    private const int MaxConsecutiveZeroPages = 256;

    internal sealed record BinkInfo(string Signature, long Length, int Frames,
        int Width, int Height, uint FpsNumerator, uint FpsDenominator,
        int AudioTracks, int FrameIndexOffset, int FirstFrameOffset, int LargestFrame)
    {
        public double FramesPerSecond => (double)FpsNumerator / FpsDenominator;
        public double DurationSeconds => (double)Frames * FpsDenominator / FpsNumerator;
        public string Description => $"{Width:N0} × {Height:N0} · {FramesPerSecond:0.###} fps · " +
            $"{DurationSeconds:0.###} seconds · {Frames:N0} frames · {AudioTracks} audio track(s)";
    }

    internal static IReadOnlyList<ArchiveEntry> Recover(string path, long fileSize, byte[] header)
    {
        var entries = new List<ArchiveEntry>();
        if (header.Length < 64 || fileSize < PageSize ||
            !header.AsSpan(0, 4).SequenceEqual("9FPR"u8)) return entries;




        uint metadataEnd = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (metadataEnd < 0x150 || metadataEnd > ReadBudget || metadataEnd > fileSize) return entries;
        long position = AlignPage(metadataEnd);
        if (position > fileSize - 48) return entries;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            PageSize, FileOptions.RandomAccess);
        if (stream.Length != fileSize) throw new IOException("The archive changed while it was being opened.");
        long budget = ReadBudget;
        int consecutiveZeroPages = 0;
        byte[] zeroPage = new byte[PageSize];
        while (entries.Count < MaxRecords && position <= fileSize - 48 && budget >= 64)
        {
            BinkInfo? info = ReadValidated(stream, position, fileSize - position, ref budget);
            if (info is null)
            {



                int count = (int)Math.Min(PageSize, fileSize - position);
                if (entries.Count == 0 || count > budget ||
                    consecutiveZeroPages >= MaxConsecutiveZeroPages) break;
                stream.Position = position;
                stream.ReadExactly(zeroPage.AsSpan(0, count));
                budget -= count;
                if (zeroPage.AsSpan(0, count).IndexOfAnyExcept((byte)0) >= 0) break;
                consecutiveZeroPages++;
                position = checked(position + PageSize);
                continue;
            }

            consecutiveZeroPages = 0;
            string extension = info.Signature.StartsWith("KB2", StringComparison.Ordinal) ? ".bk2" : ".bik";
            entries.Add(new ArchiveEntry(
                $"recovered/movie_{entries.Count + 1:0000}_at_{position:X16}{extension}",
                info.Length, info.Length, position, false, false, false,
                info.Signature.StartsWith("KB2", StringComparison.Ordinal) ? "Recovered Bink2" : "Recovered Bink",
                entries.Count));
            position = AlignPage(checked(position + info.Length));
        }
        return entries;
    }


    internal static BinkInfo? Inspect(string path, ArchiveEntry entry)
    {
        if (entry.Offset < 0 || entry.StoredSize < 48) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            PageSize, FileOptions.RandomAccess);
        if (entry.Offset > stream.Length || entry.StoredSize > stream.Length - entry.Offset) return null;
        long budget = ReadBudget;
        BinkInfo? info = ReadValidated(stream, entry.Offset, entry.StoredSize, ref budget);
        return info?.Length == entry.StoredSize ? info : null;
    }

    private static BinkInfo? ReadValidated(FileStream stream, long offset, long available, ref long budget)
    {
        if (available < 48 || offset < 0 || offset > stream.Length - 48 || budget < 64) return null;
        int prefixLength = (int)Math.Min(64, available);
        byte[] prefix = new byte[prefixLength];
        stream.Position = offset;
        stream.ReadExactly(prefix);
        budget -= prefixLength;
        bool bink2 = prefix.AsSpan(0, 3).SequenceEqual("KB2"u8);
        bool bink1 = prefix.AsSpan(0, 3).SequenceEqual("BIK"u8);
        if ((!bink1 && !bink2) || prefix[3] is < (byte)'a' or > (byte)'z') return null;

        uint Read(int p) => BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(p, 4));
        long length = (long)Read(4) + 8;
        uint frames = Read(8), largest = Read(12), repeatedFrames = Read(16);
        uint width = Read(20), height = Read(24), fpsNum = Read(28), fpsDen = Read(32), tracks = Read(40);
        if (length < 48 || length > available || frames is 0 or > MaxFrames ||
            repeatedFrames != frames || largest == 0 || largest > length ||
            width is 0 or > 16384 || height is 0 or > 16384 ||
            fpsNum is 0 or > 1_000_000 || fpsDen is 0 or > 1_000_000 || tracks > 256) return null;

        int tableBytes = checked(((int)frames + 1) * 4);



        foreach (int extraBytes in new[] { 4, 0 })
        {
            int tableOffset = checked(44 + extraBytes + (int)tracks * 12);
            long tableEnd = (long)tableOffset + tableBytes;
            if (tableEnd > length || tableBytes > budget) continue;
            byte[] table = new byte[tableBytes];
            stream.Position = checked(offset + tableOffset);
            stream.ReadExactly(table);
            budget -= tableBytes;

            uint first = BinaryPrimitives.ReadUInt32LittleEndian(table) & ~1U;
            if (first < tableEnd || first > tableEnd + 16) continue;
            uint previous = first;
            uint maxFrame = 0;
            bool valid = true;
            for (int i = 1; i <= frames; i++)
            {
                uint current = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4, 4)) & ~1U;
                if (current <= previous || current > length) { valid = false; break; }
                maxFrame = Math.Max(maxFrame, current - previous);
                previous = current;
            }
            if (!valid || previous != length || maxFrame > (long)largest + 16 || maxFrame > int.MaxValue) continue;
            string signature = System.Text.Encoding.ASCII.GetString(prefix.AsSpan(0, 4));
            return new(signature, length, (int)frames, (int)width, (int)height, fpsNum, fpsDen,
                (int)tracks, tableOffset, checked((int)first), checked((int)maxFrame));
        }
        return null;
    }

    private static long AlignPage(long value) => checked((value + PageSize - 1) & ~(PageSize - 1L));
}
