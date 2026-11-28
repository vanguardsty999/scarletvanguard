using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace RpfPreview.Core;

public sealed record Rds5WriteProgress(string Phase, long CompletedBytes, long TotalBytes);

public sealed record Rds5ReplacementResult(string DestinationPath, int TextureIndex, ulong AssetKey,
    long SourceBytes, long DestinationBytes, int ChangedChunks, long ChangedDecodedBytes,
    int UnchangedTextureSamplesVerified, string ValidationNote);







public static class Rds5TextureWriter
{
    public const string ValidationNote = "Experimental structural pool copy: original ETPF metadata and asset keys are retained; opaque metadata integrity semantics and game compatibility are not verified. Chunk ranges, copied bytes, decoded affected chunks and selected DDS payload are checked by this tool.";
    private const long MaxPhysicalOffset = 0x3FFFFFFL * 256;
    private const ulong SourceMask = 0x3FFFFFFUL;
    private const ulong PaddingMask = 0xFFFUL << 52;
    private const int BufferBytes = 1024 * 1024;

    public static Rds5ReplacementResult ExportReplacementCopy(string source, int index, byte[] dds, string destination,
        CancellationToken cancellationToken = default, IProgress<Rds5WriteProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(dds); cancellationToken.ThrowIfCancellationRequested();
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("The source pool cannot be the replacement destination.");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The replacement destination must be a new file.");
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);



        using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var original = TtpfReader.ReadIndex(source, cancellationToken); var selected = TtpfReader.Select(original, index);
        if (input.Length != original.Info.SourceLength) throw new IOException("The source pool changed while its index was read.");
        progress?.Report(new("Preparing texture", 0, selected.Layout!.Size));
        byte[] tiled = TtpfReader.RetileReplacement(input, original, selected, dds, cancellationToken);
        var imported = DdsTextureReader.Inspect(dds);
        byte[] table = new byte[checked((original.Chunks.Count + 1) * 8)]; input.Position = 24; input.ReadExactly(table);
        long firstData = original.Chunks[0].Source;
        var affectedHashes = new Dictionary<int, byte[]>();
        int changedChunks = 0, samplesVerified = 0; long changedBytes = 0, resultLength = 0;
        string temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        byte[] copyBuffer = new byte[BufferBytes]; byte[] expectedPhysicalHash;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes))
            {
                input.Position = 0; CopyExactly(input, output, firstData, copyBuffer, cancellationToken);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var bounded = new BoundedHashStream(output, hash);
                for (int i = 0; i < original.Chunks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); var chunk = original.Chunks[i];
                    long newStart = output.Position; int newPadding = chunk.Padding; bool changed = false;
                    byte[]? decoded = null;
                    long lo = Math.Max(selected.Info.VirtualOffset, chunk.Virtual),
                        hi = Math.Min(selected.Info.VirtualOffset + tiled.Length, chunk.Virtual + chunk.Size);
                    if (lo < hi)
                    {
                        decoded = TtpfReader.DecodeChunk(input, chunk, cancellationToken);
                        int count = checked((int)(hi - lo)), from = checked((int)(lo - selected.Info.VirtualOffset)), at = checked((int)(lo - chunk.Virtual));
                        for (int n = 0; n < count; n++)
                        {
                            if ((n & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                            if (decoded[at + n] != tiled[from + n]) { changedBytes++; changed = true; }
                        }
                        if (changed) tiled.AsSpan(from, count).CopyTo(decoded.AsSpan(at, count));
                    }
                    if (!changed)
                    {
                        input.Position = chunk.Source; CopyExactly(input, bounded, (long)chunk.Packed + chunk.Padding, copyBuffer, cancellationToken);
                    }
                    else
                    {
                        changedChunks++; affectedHashes.Add(i, SHA256.HashData(decoded!));
                        if (chunk.Codec == 0) WriteCancellable(bounded, decoded!, cancellationToken);
                        else if (chunk.Codec == 8)
                        {
                            using var zlib = new ZLibStream(bounded, CompressionLevel.SmallestSize, true);
                            WriteCancellable(zlib, decoded!, cancellationToken);
                        }
                        else throw new NotSupportedException("BCPack-dependent chunks cannot be replaced.");
                        long packed = output.Position - newStart;


                        int extra = (int)((256 - ((packed + chunk.Padding) & 255)) & 255);
                        newPadding = checked(chunk.Padding + extra);
                        if (newPadding > 4095) throw new NotSupportedException("Recompressed chunk cannot retain its padding within the12-bit field.");
                        input.Position = chunk.Source + chunk.Packed;
                        CopyExactly(input, bounded, chunk.Padding, copyBuffer, cancellationToken);
                        if (extra > 0) bounded.Write(new byte[extra]);
                    }
                    if ((newStart & 255) != 0 || (output.Position & 255) != 0 || output.Position > MaxPhysicalOffset)
                        throw new InvalidDataException("Rewritten chunk exceeds the26-bit physical offset range or alignment.");
                    ulong value = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(i * 8, 8));
                    value = (value & ~(SourceMask | PaddingMask)) | (ulong)(newStart / 256) | ((ulong)newPadding << 52);
                    BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(i * 8, 8), value);
                    progress?.Report(new("Writing pool copy", chunk.Source + chunk.Packed + chunk.Padding, input.Length));
                }
                resultLength = output.Position;
                ulong sentinel = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(table.Length - 8));
                sentinel = (sentinel & ~SourceMask) | (ulong)(resultLength / 256);
                BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(table.Length - 8), sentinel);
                expectedPhysicalHash = hash.GetHashAndReset();
                output.Position = 24; output.Write(table); output.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested(); progress?.Report(new("Verifying pool copy", 0, resultLength));
            var rewritten = TtpfReader.ReadIndex(temporary, cancellationToken);
            if (rewritten.Info.VirtualSize != original.Info.VirtualSize || rewritten.Chunks.Count != original.Chunks.Count || rewritten.Info.Textures.Count != original.Info.Textures.Count)
                throw new InvalidDataException("Rewritten pool changed the native virtual index shape.");
            using (var verify = File.Open(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
            {


                ComparePrefix(input, verify, firstData, table, copyBuffer, cancellationToken);
                verify.Position = firstData; using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long done = firstData;
                while (done < resultLength)
                {
                    cancellationToken.ThrowIfCancellationRequested(); int n = verify.Read(copyBuffer, 0, (int)Math.Min(copyBuffer.Length, resultLength - done));
                    if (n == 0) throw new EndOfStreamException("Rewritten physical data was truncated.");
                    digest.AppendData(copyBuffer, 0, n); done += n; progress?.Report(new("Verifying copied bytes", done, resultLength));
                }
                if (!digest.GetHashAndReset().AsSpan().SequenceEqual(expectedPhysicalHash)) throw new InvalidDataException("Rewritten physical payload checksum differs from the bytes written.");
                for (int i = 0; i < original.Chunks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); var a = original.Chunks[i]; var b = rewritten.Chunks[i];
                    if (a.Virtual != b.Virtual || a.Size != b.Size || a.Codec != b.Codec)
                        throw new InvalidDataException("Rewritten chunk changed virtual range or compression codec.");
                    if (affectedHashes.TryGetValue(i, out var expected) && !SHA256.HashData(TtpfReader.DecodeChunk(verify, b, cancellationToken)).AsSpan().SequenceEqual(expected))
                        throw new InvalidDataException("Affected chunk does not match its complete expected decoded contents.");
                }
            }
            byte[] exported = TtpfReader.CreateDds(temporary, index, cancellationToken);
            if (!exported.AsSpan(148).SequenceEqual(dds.AsSpan(imported.DataOffset)))
                throw new InvalidDataException("Selected native DDS payload does not reproduce the replacement exactly.");


            long start = selected.Info.VirtualOffset, end = start + tiled.Length;
            foreach (var sample in original.Native.Where(n => n.Info.Index != index && n.Info.CanExtract && !n.UsesBcpack && n.Layout is not null &&
                (n.Info.VirtualOffset >= end || n.Info.VirtualOffset + n.Layout.Size <= start)).OrderBy(n => n.Info.DdsSize).Take(2))
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] beforeHash = SHA256.HashData(TtpfReader.CreateDds(source, sample.Info.Index, cancellationToken));
                byte[] afterHash = SHA256.HashData(TtpfReader.CreateDds(temporary, sample.Info.Index, cancellationToken));
                if (!beforeHash.AsSpan().SequenceEqual(afterHash)) throw new InvalidDataException("An unaffected texture sample changed in the pool copy.");
                samplesVerified++;
            }
            progress?.Report(new("Publishing verified copy", resultLength, resultLength));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            return new(destination, index, selected.Info.AssetKey, original.Info.SourceLength, resultLength, changedChunks, changedBytes, samplesVerified, ValidationNote);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {   }
            throw;
        }
    }

    private static void ComparePrefix(FileStream source, FileStream output, long count, byte[] newTable, byte[] buffer, CancellationToken ct)
    {
        byte[] original = new byte[buffer.Length]; source.Position = output.Position = 0; long at = 0;
        while (at < count)
        {
            ct.ThrowIfCancellationRequested(); int n = (int)Math.Min(buffer.Length, count - at);
            source.ReadExactly(original.AsSpan(0, n)); output.ReadExactly(buffer.AsSpan(0, n));
            long lo = Math.Max(at, 24), hi = Math.Min(at + n, 24L + newTable.Length);
            if (lo < hi) newTable.AsSpan((int)(lo - 24), (int)(hi - lo)).CopyTo(original.AsSpan((int)(lo - at)));
            if (!original.AsSpan(0, n).SequenceEqual(buffer.AsSpan(0, n))) throw new InvalidDataException("Original header/ETPF metadata was not preserved exactly.");
            at += n;
        }
    }

    private static void CopyExactly(Stream source, Stream destination, long count, byte[] buffer, CancellationToken ct)
    {
        while (count > 0)
        {
            ct.ThrowIfCancellationRequested(); int n = (int)Math.Min(count, buffer.Length); source.ReadExactly(buffer.AsSpan(0, n));
            destination.Write(buffer, 0, n); count -= n;
        }
    }
    private static void WriteCancellable(Stream destination, byte[] data, CancellationToken ct)
    {
        for (int at = 0; at < data.Length; at += BufferBytes)
        { ct.ThrowIfCancellationRequested(); destination.Write(data, at, Math.Min(BufferBytes, data.Length - at)); }
    }

    private sealed class BoundedHashStream(FileStream target, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => target.Length;
        public override long Position { get => target.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MaxPhysicalOffset - target.Position) throw new NotSupportedException("Pool copy exceeds the26-bit physical source offset range.");
            target.Write(buffer); hash.AppendData(buffer);
        }
        public override void Flush() => target.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
