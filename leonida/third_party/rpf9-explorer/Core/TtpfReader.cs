using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RpfPreview.Core;

public sealed record TtpfTextureInfo(int Index, string Name, int Width, int Height, int DxgiFormat,
    string Format, int Mips, int Depth, int Dimension, long StoredSize, long DdsSize)
{
    public int SourceMips { get; init; }
    public int NonTailMipCount { get; init; }
    public int PackedMipCount { get; init; }
    public bool SupportsNativeReplacement { get; init; }
    public int BaseMipLevel { get; init; }
    public int ExportWidth => Math.Max(1, Width >> BaseMipLevel);
    public int ExportHeight => Math.Max(1, Height >> BaseMipLevel);
    public int ExportDepth => Dimension == 3 ? Math.Max(1, Depth >> BaseMipLevel) : Depth;
    public string Shape => Dimension == 3 ? $"3D volume ({Depth} slices)" : Dimension == 2 ? (Depth == 6 ? "Cube map" : $"Cube array ({Depth / 6} cubes)") : Depth > 1 ? $"2D array ({Depth} slices)" : "2D";

    public ulong AssetKey { get; init; }

    public string Id { get; init; } = "";
    public long VirtualOffset { get; init; }
    public bool TopOnly => BaseMipLevel == 0 && Mips == 1 && SourceMips > 1;
    public bool PartialMips => BaseMipLevel > 0 || Mips < SourceMips;
    public bool CanExtract => Limitation is null && DdsSize > 0;
    public string? Limitation { get; init; }
    public string? PreviewNote { get; init; }
    public int PreviewMipLevel => BaseMipLevel + RageTextureDecoder.ChoosePreviewMip(ExportWidth, ExportHeight, Mips);
    public int PreviewWidth => Math.Max(1, Width >> PreviewMipLevel);
    public int PreviewHeight => Math.Max(1, Height >> PreviewMipLevel);
    public bool CanPreview => CanExtract && (long)PreviewWidth * PreviewHeight <= 16 * 1024 * 1024 && DxgiFormat is not (81 or 84);
}

public sealed record TtpfInfo(string SourcePath, long SourceLength, long VirtualSize, int Version,
    IReadOnlyList<TtpfTextureInfo> Textures, IReadOnlyDictionary<string, string> Metadata);











public static class TtpfReader
{
    private const int MaxOutput = 256 * 1024 * 1024;
    private const int MaxIndexBytes = 64 * 1024 * 1024;
    private const int MaxTextures = 250_000;
    internal sealed record Chunk(long Source, int Packed, long Virtual, int Size, int Codec, int Padding);
    internal sealed record Mip(int Width, int Height, int PaddedWidth, int Offset, int RegionSize, int TailX, int TailY);
    internal sealed record Layout(int Size, int BlockWidth, int BlockHeight, int FirstTail, int ElementBytes, IReadOnlyList<Mip> Mips)
    {
        public int TileBytes { get; init; } = 65536;
        public int SliceBytes { get; init; }
        public int DataOffset { get; init; }
        public int BlockDepth { get; init; } = 1;
        public int ZBlockStride { get; init; }
        public bool MipMajorSlices { get; init; }
        public bool Volume { get; init; }
    }
    internal sealed record Native(TtpfTextureInfo Info, Layout? Layout, bool UsesBcpack = false);
    internal sealed record Index(TtpfInfo Info, IReadOnlyList<Chunk> Chunks, IReadOnlyList<Native> Native);
    private static readonly int[][] Patterns = [
        [1,2,3,-2,-1,-3,4,-4,-5,5,-6,6,-7,7,-8,8],
        [0,1,2,3,-1,-2,-3,4,-4,5,-5,6,-6,7,-7,8],
        [0,0,1,2,-1,-2,-3,3,-4,4,-5,5,-6,6,-7,7],
        [0,0,0,1,-1,2,3,-2,-3,4,-4,5,-5,6,-6,7],
        [0,0,0,0,1,-1,2,-2,-3,3,-4,4,-5,5,-6,6]];
    private static readonly int[][] FourKbPatterns = [
        [1,2,3,4,-1,-2,-3,-4,-5,5,-6,6],
        [0,1,2,3,-1,-2,-3,4,-4,5,-5,6],
        [0,0,1,2,-1,-2,-3,3,-4,4,-5,5],
        [0,0,0,1,-1,-2,2,3,-3,4,-4,5],
        [0,0,0,0,-1,-2,1,2,-3,3,-4,4]];
    private static readonly int[][] VolumePatterns = [
        [1,2,101,-1,102,-2,3,103,-3,4,104,-4],
        [0,1,101,-1,102,-2,2,103,-3,3,104,-4],
        [0,0,1,-1,101,-2,2,102,-3,3,103,-4],
        [0,0,0,1,101,-1,2,102,-2,3,103,-3],
        [0,0,0,0,101,-1,1,102,-2,2,103,-3]];

    public static TtpfInfo Inspect(string path, CancellationToken cancellationToken = default) => ReadIndex(path, cancellationToken).Info;

    public static RageTexturePixels DecodeThumbnail(string path, int index, CancellationToken cancellationToken = default)
        => DecodeTexturePreview(path, index, 192, cancellationToken);

    public static RageTexturePixels DecodeTexturePreview(string path, int index, int maximumDimension = 1024, CancellationToken cancellationToken = default)
        => DecodeTexturePreview(path, index, maximumDimension, false, cancellationToken);

    public static RageTexturePixels DecodeTexturePreview(string path, int index, int maximumDimension, bool allowProvisional, CancellationToken cancellationToken = default)
    {
        if (maximumDimension is < 32 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        var source = ReadIndex(path, cancellationToken); var native = Select(source, index); var t = native.Info;
        int level = t.BaseMipLevel;
        while (level + 1 < t.BaseMipLevel + t.Mips && (Math.Max(1, t.Width >> level) > maximumDimension || Math.Max(1, t.Height >> level) > maximumDimension)) level++;
        int width = Math.Max(1, t.Width >> level), height = Math.Max(1, t.Height >> level);
        if ((long)width * height > 16 * 1024 * 1024) throw new NotSupportedException("No bounded thumbnail mip is available.");
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != source.Info.SourceLength) throw new IOException("Texture pool changed.");
        byte[] linear = ReadMip(input, source, native, level, 0, new(), cancellationToken, allowProvisional);
        return RageTextureDecoder.DecodeLinearTexturePixels(linear, width, height, t.DxgiFormat);
    }

    public static byte[] CreateDds(string path, int index, CancellationToken cancellationToken = default)
        => CreateDds(path, index, false, cancellationToken);

    public static byte[] CreateDds(string path, int index, bool allowProvisional, CancellationToken cancellationToken = default)
    {
        var source = ReadIndex(path, cancellationToken); var native = Select(source, index);
        var t = native.Info; var layout = native.Layout!;
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != source.Info.SourceLength) throw new IOException("The texture pool changed while it was being read.");
        var cache = new Dictionary<int, byte[]>();
        using var output = new MemoryStream(checked((int)t.DdsSize));
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        bool bc = RageTextureDecoder.IsBlockCompressed(t.DxgiFormat), volume = t.Dimension == 3, cube = t.Dimension == 2;
        writer.Write("DDS "u8); writer.Write(124); writer.Write((bc ? 0xA1007 : 0x2100F) | (volume ? 0x800000 : 0));
        writer.Write(t.ExportHeight); writer.Write(t.ExportWidth);
        writer.Write(bc ? LinearBytes(layout.Mips[t.BaseMipLevel], layout.ElementBytes) : t.ExportWidth * layout.ElementBytes);
        writer.Write(volume ? t.ExportDepth : 0); writer.Write(t.Mips); writer.Write(new byte[44]);
        writer.Write(32); writer.Write(4); writer.Write(0x30315844); writer.Write(new byte[20]);
        writer.Write((t.Mips > 1 ? 0x401008 : 0x1000) | (volume || cube || t.Depth > 1 ? 8 : 0));
        writer.Write(volume ? 0x200000 : cube ? 0xFE00 : 0); writer.Write(new byte[12]);
        writer.Write(t.DxgiFormat); writer.Write(volume ? 4 : 3); writer.Write(cube ? 4 : 0);
        writer.Write(volume ? 1 : cube ? t.Depth / 6 : t.Depth); writer.Write(0);
        if (volume)
        {
            for (int mip = t.BaseMipLevel; mip < t.BaseMipLevel + t.Mips; mip++)
                for (int z = 0; z < Math.Max(1, t.Depth >> mip); z++)
                    writer.Write(ReadMip(input, source, native, mip, z, cache, cancellationToken, allowProvisional));
        }
        else
        {
            for (int slice = 0; slice < t.Depth; slice++)
                for (int mip = t.BaseMipLevel; mip < t.BaseMipLevel + t.Mips; mip++)
                    writer.Write(ReadMip(input, source, native, mip, slice, cache, cancellationToken, allowProvisional));
        }
        if (output.Length != t.DdsSize) throw new InvalidDataException("Generated DDS length disagrees with the inspected texture.");
        return output.ToArray();
    }

    public static RageTexturePixels DecodeTexturePixels(string path, int index, int slice = 0, CancellationToken cancellationToken = default)
        => DecodeTexturePixels(path, index, slice, false, cancellationToken);

    public static RageTexturePixels DecodeTexturePixels(string path, int index, int slice, bool allowProvisional, CancellationToken cancellationToken = default)
    {
        var source = ReadIndex(path, cancellationToken); var native = Select(source, index); var t = native.Info;
        int sliceCount = t.Dimension == 3 ? Math.Max(1, t.Depth >> t.PreviewMipLevel) : t.Depth;
        if ((uint)slice >= sliceCount) throw new ArgumentOutOfRangeException(nameof(slice));
        if (!t.CanPreview) throw new NotSupportedException(t.Limitation ?? "No supported mip fits the preview output limit.");
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != source.Info.SourceLength) throw new IOException("The texture pool changed while it was being read.");
        byte[] linear = ReadMip(input, source, native, t.PreviewMipLevel, slice, new(), cancellationToken, allowProvisional);
        return RageTextureDecoder.DecodeLinearTexturePixels(linear, t.PreviewWidth, t.PreviewHeight, t.DxgiFormat);
    }

    internal static Index ReadIndex(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); path = Path.GetFullPath(path);
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileSize = input.Length; if (fileSize < 256) throw new InvalidDataException("Truncated RDS5 header.");
        byte[] header = new byte[24]; input.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RDS5"u8)) throw new InvalidDataException("Not an RDS5 container.");
        uint count = U32(header, 4); ulong sizeWord = U64(header, 8);
        long virtualSize = checked((long)(sizeWord & 0x00FFFFFFFFFFFFFFUL));
        if (count < 3 || count > 1_000_002 || (sizeWord >> 56) != 22 || virtualSize < 4096 || virtualSize > 1L << 34 ||
            U32(header, 16) < (count + 31) / 32 || U32(header, 16) > (count + 31) / 32 + 1)
            throw new InvalidDataException("Unsupported RDS5 index layout.");



        long tableEnd = checked(24L + (count - 1) * 8L), etpf = Align(tableEnd, 256);
        if (tableEnd > fileSize - 16 || (count - 1) * 8L > MaxIndexBytes) throw new InvalidDataException("RDS5 index exceeds its source or limit.");
        byte[] table = new byte[checked(((int)count - 1) * 8)]; input.ReadExactly(table);
        var chunks = new List<Chunk>(checked((int)count - 2));
        long sentinelSource = SourceOffset(U64(table, table.Length - 8)), sentinelVirtual = VirtualOffset(U64(table, table.Length - 8));
        if (sentinelSource != fileSize || sentinelVirtual != virtualSize) throw new InvalidDataException("RDS5 sentinel does not match its source/virtual length.");
        for (int i = 0; i < count - 2; i++)
        {
            ct.ThrowIfCancellationRequested(); ulong value = U64(table, i * 8), next = U64(table, (i + 1) * 8);
            long start = SourceOffset(value), end = SourceOffset(next), v = VirtualOffset(value), ve = VirtualOffset(next);
            int pad = (int)(value >> 52), codec = (int)((value >> 26) & 15);
            long packed = end - start - pad, size = ve - v;
            if (start < etpf + 16 || end > fileSize || packed < 1 || packed > MaxOutput || size < 1 || size > MaxOutput ||
                ve > virtualSize || (codec == 0 && packed != size)) throw new InvalidDataException("RDS5 chunk range is invalid or exceeds the per-chunk limit.");
            chunks.Add(new(start, (int)packed, v, (int)size, codec, pad));
        }
        input.Position = etpf; byte[] eh = new byte[16]; input.ReadExactly(eh);
        if (!eh.AsSpan(0, 4).SequenceEqual("ETPF"u8) || U32(eh, 4) != 634) throw new InvalidDataException("Unsupported embedded ETPF version.");
        int textures = checked((int)U32(eh, 8)), area = checked((int)U32(eh, 12));
        long metadataBytes = checked(16L + area + textures * 32L), paddedMetadata = Align(metadataBytes, 4096);
        uint alignmentWord = U32(header, 20); long sourcePadding = (alignmentWord >> 28) * 256L;
        if (textures < 1 || textures > MaxTextures || area < textures * 16L || metadataBytes > MaxIndexBytes ||
            (alignmentWord & 0x0FFFFFFF) != 0 || paddedMetadata != chunks[0].Virtual ||
            checked(etpf + paddedMetadata + sourcePadding) != chunks[0].Source)
            throw new InvalidDataException("ETPF tables do not match the source/virtual data boundary.");
        byte[] records = new byte[area]; input.ReadExactly(records);
        byte[] ids = new byte[checked(textures * 32)]; input.ReadExactly(ids);
        long keysEnd = checked(textures * 24L);
        if (keysEnd > (long)records.Length + ids.Length) throw new InvalidDataException("ETPF native asset-key array exceeds its metadata.");
        var native = new List<Native>(textures);
        for (int i = 0; i < textures; i++)
        {
            ct.ThrowIfCancellationRequested(); int p = i * 16;
            uint a = U32(records, p), b = U32(records, p + 4), c = U32(records, p + 8), d = U32(records, p + 12);
            int nonTail = (int)(b & 7), packedMips = (int)(a >> 28), mode = (int)(b >> 23);
            int width = (int)(a & 0x3FFF) + 1, height = (int)((a >> 14) & 0x3FFF) + 1, mips = packedMips + nonTail,
                format = (int)((b >> 3) & 0x7F), depth = (int)((b >> 10) & 0x7FF) + 1, dimension = (int)((b >> 21) & 3);
            long baseOffset = (long)(c & 0x1FFFFFUL) * 4096;
            string id = Convert.ToHexString(ids.AsSpan(i * 32, 32));
            int keyAt = checked(textures * 16 + i * 8);
            ulong assetKey = keyAt + 8 <= records.Length ? U64(records, keyAt) : keyAt >= records.Length
                ? U64(ids, keyAt - records.Length) : throw new InvalidDataException("ETPF asset key straddles an unsupported metadata boundary.");
            bool legacyTopOnlyName = LegacyTopOnlyName(width, height, format, packedMips + 1, b, c, d, paddedMetadata, virtualSize, chunks);
            bool auxiliaryMapping = (c >> 21) != 0 || d != 0;
            string? limitation = null; Layout? layout = null; int exported = 0, baseMip = 0;
            bool readOnlyProfile = dimension != 1 || depth != 1 || auxiliaryMapping || mode is not (0 or 2), usesBcpack = false; long stored = 0, ddsSize = 0;
            try
            {
                if (baseOffset == 0 || (c & 0x1FFFFF) == 0x1FFFFF) throw new NotSupportedException("This catalog record has no mapped allocation in this pool shard.");
                if (baseOffset < paddedMetadata || baseOffset >= virtualSize) throw new InvalidDataException("Texture virtual base exceeds the pool data.");
                long nextAllocation = virtualSize;
                for (int following = i + 1; following < textures; following++)
                {
                    long candidate = (long)(U32(records, following * 16 + 8) & 0x1FFFFF) * 4096;
                    if (candidate < baseOffset) throw new InvalidDataException("Texture allocations are not ordered.");
                    if (candidate > baseOffset) { nextAllocation = candidate; break; }
                }
                long allocationSpan = nextAllocation - baseOffset;
                if (allocationSpan < 1 || allocationSpan > MaxOutput) throw new NotSupportedException("Texture exceeds the 256MiB resource limit.");
                if (dimension == 1 && depth == 1)
                {
                    bool supportedMode = auxiliaryMapping
                        ? mode is 2 or 4 or 5 or 6 or 7 or 36 or 38
                        : mode is 0 or 1 or 2 or 32 or 34;
                    if (!supportedMode) throw new NotSupportedException("This compact mip mapping profile has not been validated.");
                    layout = SurfaceLayout(width, height, format, mips, (mode & 6) == 0 ? 65536 : 4096);
                    if (layout.Size != allocationSpan) throw new InvalidDataException("Texture footprint does not match its native allocation span.");
                    if (auxiliaryMapping && !ValidateCompactStreamingChunks(chunks, baseOffset, layout.Size, mode, nonTail, c >> 21, d))
                        throw new NotSupportedException("Auxiliary compact mip mapping is not decoded.");
                }
                else if (dimension == 1 && depth > 1)
                {
                    if (mode != 1 || auxiliaryMapping) throw new NotSupportedException("This ETPF array layout has not been validated.");
                    layout = ArrayLayout(width, height, depth, format, mips);
                    if (layout.Size != allocationSpan) throw new InvalidDataException("Texture array footprint does not match its native allocation span.");
                }
                else if (dimension == 2)
                {
                    if (depth != 6 || width != height || nonTail > 3 || mode != 18 + 2 * nonTail)
                        throw new NotSupportedException("This ETPF cubemap layout has not been validated.");
                    var faceLayout = SurfaceLayout(width, height, format, mips, 4096);
                    int facesBytes = checked(faceLayout.Size * depth);
                    int allocBytes = checked((int)allocationSpan);
                    if (!auxiliaryMapping || !ValidateCubemapStreamingChunks(chunks, baseOffset, allocBytes, nonTail, c >> 21, d, facesBytes, out int dataOffset))
                        throw new NotSupportedException("This cubemap streaming layout has not been validated.");
                    layout = faceLayout with { Size = allocBytes, SliceBytes = faceLayout.Size, DataOffset = dataOffset };
                }
                else if (dimension == 3)
                {
                    if (mode != 2 || auxiliaryMapping || depth < 2 || mips != 1)
                        throw new NotSupportedException("This ETPF volume layout has not been validated.");
                    layout = VolumeLayout(width, height, depth, format);
                    if (layout.Size != allocationSpan) throw new InvalidDataException("Volume texture footprint does not match its native allocation span.");
                }
                else throw new NotSupportedException("This ETPF array/volume layout has not been validated.");
                var available = new bool[mips];
                SortedSet<int>? unsupportedCodecs = null;
                for (int mip = 0; mip < mips; mip++)
                {
                    var level = layout.Mips[mip];
                    int slices = layout.Volume ? Math.Max(1, depth >> mip) : depth;
                    int step = layout.Volume ? layout.BlockDepth : 1;
                    bool ok = true;
                    for (int s = 0; s < slices; s += step)
                    {
                        foreach (var chunk in ChunksFor(chunks, MipSliceOffset(layout, baseOffset, level, s), level.RegionSize))
                        {
                            if (!IsDecodableCodec(chunk.Codec))
                            {
                                ok = false;
                                unsupportedCodecs ??= new SortedSet<int>();
                                unsupportedCodecs.Add(chunk.Codec);
                            }
                        }
                    }
                    available[mip] = ok;
                }
                baseMip = Array.FindIndex(available, usable => usable);
                if (baseMip < 0)
                {
                    string codecsText = unsupportedCodecs is { Count: > 0 }
                        ? string.Join("/", unsupportedCodecs.Select(BcpackDecoder.GetCodecName))
                        : "unknown";
                    throw new NotSupportedException($"All source mips require an unsupported Xbox BCPack codec ({codecsText}).");
                }
                if (baseMip == 0 && legacyTopOnlyName) exported = 1;
                else while (baseMip + exported < mips && available[baseMip + exported]) exported++;
                var usedSet = new HashSet<Chunk>();
                ddsSize = 148;
                for (int mip = baseMip; mip < baseMip + exported; mip++)
                {
                    var level = layout.Mips[mip];
                    int slices = layout.Volume ? Math.Max(1, depth >> mip) : depth;
                    ddsSize = checked(ddsSize + (long)LinearBytes(level, layout.ElementBytes) * slices);
                    int step = layout.Volume ? layout.BlockDepth : 1;
                    for (int s = 0; s < slices; s += step)
                        foreach (var chunk in ChunksFor(chunks, MipSliceOffset(layout, baseOffset, level, s), level.RegionSize)) usedSet.Add(chunk);
                }
                usesBcpack = usedSet.Any(x => x.Codec is not (0 or 8));
                stored = usedSet.Sum(x => (long)x.Packed);
                if (ddsSize > MaxOutput) throw new NotSupportedException("DDS exceeds the 256MiB output limit.");
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
            { limitation = ex.Message; exported = baseMip = 0; stored = ddsSize = 0; }
            string formatName = RageTextureDecoder.GetTextureFormatName(format);
            int exportDepth = dimension == 3 ? Math.Max(1, depth >> baseMip) : depth;
            string note = dimension == 3 ? $"Read-only 3D volume ({exportDepth} slices). Native replacement is unavailable. "
                : dimension == 2 ? $"Read-only cubemap ({depth} faces). Native replacement is unavailable. "
                : depth > 1 ? $"Read-only 2D array ({depth} slices). Native replacement is unavailable. "
                : auxiliaryMapping ? $"Read-only compact profile: {nonTail} streaming mips and {packedMips} packed mips. Native replacement is unavailable. "
                : readOnlyProfile ? $"Read-only native storage mode ({mode}). Native replacement is unavailable. "
                : usesBcpack ? "Read-only BCPack-compressed texture. Native replacement is unavailable. " : "";
            string sourceDims = dimension == 3 ? $"{width} × {height} × {depth}" : $"{width} × {height}";
            string exportDims = dimension == 3 ? $"{Math.Max(1, width >> baseMip)} × {Math.Max(1, height >> baseMip)} × {exportDepth}" : $"{Math.Max(1, width >> baseMip)} × {Math.Max(1, height >> baseMip)}";
            note += baseMip > 0 ? $"Source is {sourceDims} with {mips} mips. Export starts at source mip {baseMip}: {exportDims}, with {exported} decoded mip levels. Other source mips remain unavailable. "
                : exported > 0 && exported < mips ? $"Source is {sourceDims} with {mips} mips. Export contains {exported} decoded mip levels starting at source mip 0: {sourceDims}. Other source mips remain unavailable. " : "";
            if (format is 10 or 34 or 41 or 54 or 95 or 96) note += "Floating-point channels clip to 0–1 for display; DDS preserves source values. ";
            if (format == 65) note += "Alpha mask displayed as grayscale. ";
            int previewMip = baseMip + RageTextureDecoder.ChoosePreviewMip(Math.Max(1, width >> baseMip), Math.Max(1, height >> baseMip), exported);
            if (previewMip > 0) note += $"Preview uses mip {previewMip}: {Math.Max(1, width >> previewMip)} × {Math.Max(1, height >> previewMip)}. ";
            string suffix = legacyTopOnlyName ? "_top_mip" : "";
            var info = new TtpfTextureInfo(i, $"texture_{i:D6}_{id[..16]}{suffix}.dds",
                width, height, format, formatName == "Unknown" ? $"DXGI {format}" : formatName, exported, depth, dimension, stored, ddsSize)
            { SourceMips = mips, NonTailMipCount = nonTail, PackedMipCount = packedMips, BaseMipLevel = baseMip,
                SupportsNativeReplacement = !readOnlyProfile && !usesBcpack && limitation == null && baseMip == 0 && exported == mips,
                AssetKey = assetKey, Id = id, VirtualOffset = baseOffset, Limitation = limitation, PreviewNote = note.Length == 0 ? null : note.Trim() };
            native.Add(new(info, layout, usesBcpack));
        }
        var metadata = new Dictionary<string, string>
        {
            ["Descriptor format"] = "RDS5 with embedded ETPF634; validated AMD 64KB_D and 4KB_S2D/3D DDS exports",
            ["Texture names"] = "Generated ordinal/opaque-ID names; original filenames are unknown",
            ["Native binding key"] = "Validated64-bit AssetKey array immediately after fixed texture descriptors; Id is an opaque metadata token",
            ["Descriptor count"] = textures.ToString("N0"),
            ["Supported DDS exports"] = native.Count(x => x.Info.CanExtract).ToString("N0"),
            ["Top-mip-only exports"] = native.Count(x => x.Info.TopOnly).ToString("N0"),
            ["Lower-mip-only exports"] = native.Count(x => x.Info.CanExtract && x.Info.BaseMipLevel > 0).ToString("N0"),
            ["Source access"] = "Read-only; requested chunks only; zlib checksums and BCPack rANS streams checked on use",
            ["Remaining layouts"] = "Unvalidated storage profiles, unsupported formats, and unsupported BCPack codecs (BC6H/BC7) remain unavailable"
        };
        var result = new TtpfInfo(path, fileSize, virtualSize, 634, native.Select(x => x.Info).ToArray(), metadata);
        return new(result, chunks, native);
    }

    internal static Native Select(Index source, int index)
    {
        if ((uint)index >= source.Native.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var n = source.Native[index]; if (!n.Info.CanExtract || n.Layout is null) throw new NotSupportedException(n.Info.Limitation ?? "Texture layout unavailable."); return n;
    }
    private static bool TryDecodeChunkPages(uint meta, uint streamFlags, Span<int> expectedPages, out int count)
    {
        count = 0;
        if (((streamFlags >> 9) & 0x3FF) != 0) return false;
        ulong packed = ((ulong)streamFlags << 11) | meta;
        bool ended = false;
        for (int k = 0; k < 4; k++)
        {
            int code = (int)((packed >> (5 * k)) & 0x1F);
            if (code == 0) { ended = true; continue; }
            if (ended) return false;
            int pages = ChunkCodePages(code);
            if (pages <= 0) return false;
            expectedPages[count++] = pages;
        }
        return count > 0;
    }

    private static bool TryValidateChunkSizes(IReadOnlyList<Chunk> chunks, long baseOffset, int allocationSize,
        ReadOnlySpan<int> expectedPages, out List<Chunk> mapped)
    {
        mapped = ChunksFor(chunks, baseOffset, allocationSize);
        if (mapped.Count != expectedPages.Length || mapped[0].Virtual != baseOffset) return false;
        long totalBytes = 0;
        for (int k = 0; k < expectedPages.Length; k++)
        {
            int expectedBytes = checked(expectedPages[k] * 4096);
            if (mapped[k].Size != expectedBytes) return false;
            totalBytes += expectedBytes;
        }
        return totalBytes == allocationSize;
    }

    private static bool ValidateCompactStreamingChunks(IReadOnlyList<Chunk> chunks, long baseOffset, int allocationSize,
        int mode, int nonTail, uint meta, uint streamFlags)
    {
        Span<int> expectedPages = stackalloc int[4];
        if (!TryDecodeChunkPages(meta, streamFlags, expectedPages, out int count)) return false;
        int alignGroup = (int)((streamFlags >> 19) & 3);
        int padPages = (int)(streamFlags >> 21);
        if (nonTail == 1)
        {
            if (mode is not (4 or 5 or 36) || alignGroup != 0 || padPages != 0) return false;
        }
        else if (nonTail == 3)
        {
            bool alignOk = alignGroup switch
            {
                1 => mode is 2 or 4 && count == 3 && expectedPages[0] + padPages == 16,
                2 => mode is 4 or 5 or 36 && count >= 2 && expectedPages[0] + expectedPages[1] + padPages == 16,
                3 => mode is 6 or 7 or 38 && count >= 2 && expectedPages[0] + expectedPages[1] + padPages == 8,
                _ => false
            };
            if (!alignOk) return false;
        }
        else return false;
        return TryValidateChunkSizes(chunks, baseOffset, allocationSize, expectedPages[..count], out _);
    }

    private static bool ValidateCubemapStreamingChunks(IReadOnlyList<Chunk> chunks, long baseOffset, int allocationSize,
        int nonTail, uint meta, uint streamFlags, int facesBytes, out int dataOffset)
    {
        dataOffset = 0;
        if ((streamFlags >> 9) != 0) return false;
        Span<int> expectedPages = stackalloc int[4];
        if (!TryDecodeChunkPages(meta, streamFlags, expectedPages, out int count) || count != nonTail + 1) return false;
        if (!TryValidateChunkSizes(chunks, baseOffset, allocationSize, expectedPages[..count], out var mapped)) return false;
        int expectedLastSize = nonTail > 0 && facesBytes >= 36 * 4096 ? checked((int)Align(facesBytes, 65536)) : facesBytes;
        if (mapped[^1].Size != expectedLastSize) return false;
        dataOffset = checked((int)(mapped[^1].Virtual - baseOffset));
        return true;
    }

    private static int ChunkCodePages(int code) => code switch
    {
        >= 1 and <= 8 => code,
        10 => 12, 11 => 16, 12 => 24, 13 => 32, 14 => 48, 15 => 64, 16 => 128,
        21 => 18, 22 => 80, 23 => 96, 24 => 144, 26 => 272, 27 => 384, 28 => 528,
        _ => 0
    };
    private static bool LegacyTopOnlyName(int width, int height, int format, int oldMips, uint b, uint c, uint d,
        long metadata, long virtualSize, IReadOnlyList<Chunk> chunks)
    {
        try
        {
            if ((b >> 23) != 0 || ((b >> 10) & 0x7FF) != 0 || ((b >> 21) & 3) != 1 ||
                (c >> 22) != 0 || d != 0 || oldMips <= 1) return false;
            long offset = (long)(c & 0x3FFFFF) * 4096;
            if (offset < metadata || offset >= virtualSize) return false;
            var layout = SurfaceLayout(width, height, format, oldMips);
            if ((b & 7) != layout.FirstTail || layout.Size > virtualSize - offset) return false;
            if (ChunksFor(chunks, offset, layout.Size).All(x => IsDecodableCodec(x.Codec))) return false;
            var top = layout.Mips[0];
            return ChunksFor(chunks, offset + top.Offset, top.RegionSize).All(x => IsDecodableCodec(x.Codec));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException) { return false; }
    }
    internal static List<Chunk> ChunksFor(IReadOnlyList<Chunk> chunks, long start, int count)
    {
        long end = checked(start + count), covered = start; var result = new List<Chunk>();

        int lo = 0, hi = chunks.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (chunks[mid].Virtual + chunks[mid].Size <= start) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < chunks.Count && chunks[i].Virtual < end; i++)
        {
            var c = chunks[i]; if (c.Virtual > covered) throw new InvalidDataException("Texture crosses an unmapped virtual range.");
            result.Add(c); covered = Math.Min(end, c.Virtual + c.Size);
        }
        if (covered != end) throw new InvalidDataException("Texture range is not completely mapped."); return result;
    }

    private static long MipSliceOffset(Layout layout, long baseOffset, Mip m, int slice) =>
        layout.Volume ? checked(baseOffset + (long)(slice / layout.BlockDepth) * layout.ZBlockStride + m.Offset)
        : layout.MipMajorSlices ? checked(baseOffset + m.Offset + (long)slice * m.RegionSize)
        : checked(baseOffset + layout.DataOffset + (long)slice * layout.SliceBytes + m.Offset);

    private static byte[] ReadMip(FileStream input, Index source, Native native, int mip, int slice, Dictionary<int, byte[]> cache, CancellationToken ct, bool allowProvisional = false)
    {
        var layout = native.Layout!; var m = layout.Mips[mip];
        long start = MipSliceOffset(layout, native.Info.VirtualOffset, m, slice); byte[] tiled = new byte[m.RegionSize];
        foreach (var chunk in ChunksFor(source.Chunks, start, m.RegionSize))
        {
            ct.ThrowIfCancellationRequested(); int key = source.Chunks.IndexOfReference(chunk);
            if (!cache.TryGetValue(key, out var decoded)) { decoded = DecodeChunk(input, chunk, ct, allowProvisional); cache.Add(key, decoded); }
            long from = Math.Max(start, chunk.Virtual), to = Math.Min(start + m.RegionSize, chunk.Virtual + chunk.Size);
            decoded.AsSpan((int)(from - chunk.Virtual), (int)(to - from)).CopyTo(tiled.AsSpan((int)(from - start)));
        }
        byte[] linear = new byte[LinearBytes(m, layout.ElementBytes)];
        for (int y = 0; y < m.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < m.Width; x++)
            {
                int offset = TiledOffset(layout, m, x, y, slice);
                if (offset > tiled.Length - layout.ElementBytes) throw new InvalidDataException("Tiled address exceeds its source range.");
                tiled.AsSpan(offset, layout.ElementBytes).CopyTo(linear.AsSpan((y * m.Width + x) * layout.ElementBytes));
            }
        }
        return linear;
    }

    private static bool IsDecodableCodec(int codec) => codec is 0 or 8 || BcpackDecoder.IsSupportedCodec(codec);

    internal static byte[] DecodeChunk(FileStream input, Chunk chunk, CancellationToken ct, bool allowProvisional = false)
    {
        if (!IsDecodableCodec(chunk.Codec))
            throw new NotSupportedException($"Xbox BCPack codec {chunk.Codec} ({BcpackDecoder.GetCodecName(chunk.Codec)}) is not supported.");
        byte[] packed = new byte[chunk.Packed]; input.Position = chunk.Source; input.ReadExactly(packed);
        if (chunk.Codec == 0) return packed;
        if (BcpackDecoder.IsSupportedCodec(chunk.Codec))
            return BcpackDecoder.Decode(packed, chunk.Codec, chunk.Size, allowProvisional, ct);
        if (packed.Length < 6) throw new InvalidDataException("Truncated zlib chunk.");
        using var data = new MemoryStream(packed, false); using var zlib = new ZLibStream(data, CompressionMode.Decompress);
        byte[] decoded = new byte[chunk.Size]; int copied = 0; uint a = 1, b = 0;
        while (copied < decoded.Length)
        {
            ct.ThrowIfCancellationRequested(); int n = zlib.Read(decoded.AsSpan(copied, Math.Min(4096, decoded.Length - copied)));
            if (n == 0) throw new InvalidDataException("Zlib output is shorter than its virtual chunk.");
            for (int i = copied; i < copied + n; i++) { a += decoded[i]; b += a; } a %= 65521; b %= 65521; copied += n;
        }
        if (zlib.ReadByte() != -1 || BinaryPrimitives.ReadUInt32BigEndian(packed.AsSpan(packed.Length - 4)) != ((b << 16) | a))
            throw new InvalidDataException("Zlib output length/checksum disagrees with its chunk.");
        return decoded;
    }



    internal static byte[] RetileReplacement(FileStream input, Index source, Native native, byte[] dds, CancellationToken ct)
    {
        var t = native.Info; var layout = native.Layout ?? throw new NotSupportedException("Texture layout is unavailable.");
        if (!t.SupportsNativeReplacement) throw new NotSupportedException("Native replacement is not validated for this texture storage profile or partial mip chain.");
        if (!t.CanExtract || t.BaseMipLevel != 0 || t.Mips != t.SourceMips || t.Dimension != 1 || t.Depth != 1)
            throw new NotSupportedException("Pool replacement requires a complete supported 2D mip chain; top-only exports are unavailable for replacement.");
        var imported = DdsTextureReader.Inspect(dds);
        if (imported.Width != t.Width || imported.Height != t.Height || imported.DxgiFormat != t.DxgiFormat ||
            imported.Mips != t.SourceMips || imported.Depth != 1 || imported.Dimension != 1 ||
            imported.ResourceDimension != 3 || imported.ArraySize != 1 || imported.SliceCount != 1)
            throw new InvalidDataException("DDS dimensions, DXGI format, complete mip count and 2D shape must match the native pool descriptor exactly.");
        if (imported.AlphaMode is not (0 or 1)) throw new NotSupportedException("Replacement DDS requires unknown or straight alpha semantics.");
        byte[] tiled = new byte[layout.Size];
        foreach (var chunk in ChunksFor(source.Chunks, t.VirtualOffset, layout.Size))
        {
            ct.ThrowIfCancellationRequested(); byte[] decoded = DecodeChunk(input, chunk, ct);
            long lo = Math.Max(t.VirtualOffset, chunk.Virtual), hi = Math.Min(t.VirtualOffset + layout.Size, chunk.Virtual + chunk.Size);
            decoded.AsSpan(checked((int)(lo - chunk.Virtual)), checked((int)(hi - lo))).CopyTo(tiled.AsSpan(checked((int)(lo - t.VirtualOffset))));
        }
        for (int mip = 0; mip < t.SourceMips; mip++)
        {
            var m = layout.Mips[mip]; var linear = DdsTextureReader.GetMipPlane(dds, imported, mip, 0);
            for (int y = 0; y < m.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (int x = 0; x < m.Width; x++)
                {
                    int at = checked(m.Offset + TiledOffset(layout, m, x, y));
                    if (at < 0 || at > tiled.Length - layout.ElementBytes) throw new InvalidDataException("Retiled address exceeds the native allocation.");
                    linear.Slice((y * m.Width + x) * layout.ElementBytes, layout.ElementBytes).CopyTo(tiled.AsSpan(at, layout.ElementBytes));
                }
            }
        }
        return tiled;
    }

    private static int TiledOffset(Layout layout, Mip mip, int x, int y, int slice = 0)
    {
        int offset = checked(((y / layout.BlockHeight) * (mip.PaddedWidth / layout.BlockWidth) + x / layout.BlockWidth) * layout.TileBytes);
        var pattern = (layout.Volume ? VolumePatterns : layout.TileBytes == 4096 ? FourKbPatterns : Patterns)[Log(layout.ElementBytes)];
        for (int bit = 0; bit < pattern.Length; bit++)
        {
            int s = pattern[bit];
            if (s >= 101) offset |= ((slice >> (s - 101)) & 1) << bit;
            else if (s > 0) offset |= (((x + mip.TailX) >> (s - 1)) & 1) << bit;
            else if (s < 0) offset |= (((y + mip.TailY) >> (-s - 1)) & 1) << bit;
        }
        return offset;
    }

    private static Layout ArrayLayout(int w, int h, int depth, int format, int mips)
    {
        var slice = SurfaceLayout(w, h, format, mips, 65536);
        var ms = slice.Mips.ToArray();
        int off = slice.FirstTail < mips ? checked(depth * 65536) : 0;
        for (int i = slice.FirstTail - 1; i >= 0; i--)
        {
            ms[i] = ms[i] with { Offset = off };
            off = checked(off + depth * ms[i].RegionSize);
        }
        if (off != checked(slice.Size * depth) || off > MaxOutput) throw new NotSupportedException("Texture exceeds the 256MiB resource limit.");
        return slice with { Size = off, Mips = ms, MipMajorSlices = true };
    }

    private static Layout VolumeLayout(int w, int h, int depth, int format)
    {
        int eb = RageTextureDecoder.GetTextureElementBytes(format), log = Log(eb), px = RageTextureDecoder.IsBlockCompressed(format) ? 4 : 1;
        (int bw, int bh, int bd) = log switch { 0 => (16, 16, 16), 1 => (8, 16, 16), 2 => (8, 16, 8), 3 => (8, 8, 8), _ => (4, 8, 8) };
        int ew = (w + px - 1) / px, eh = (h + px - 1) / px;
        int pw = checked((int)Align(ew, bw)), ph = checked((int)Align(eh, bh));
        int zBlockStride = checked(pw * ph * eb * bd);
        int zBlocks = checked((int)Align(depth, bd)) / bd;
        int sizeBytes = checked(zBlockStride * zBlocks);
        if (sizeBytes < 1 || sizeBytes > MaxOutput) throw new NotSupportedException("Texture exceeds the 256MiB resource limit.");
        return new(sizeBytes, bw, bh, 1, eb, [new(ew, eh, pw, 0, zBlockStride, 0, 0)])
        { TileBytes = 4096, Volume = true, BlockDepth = bd, ZBlockStride = zBlockStride };
    }

    private static Layout SurfaceLayout(int w, int h, int format, int mips, int tileBytes = 65536)
    {
        int levels = 1; for (int v = Math.Max(w, h); v > 1; v >>= 1) levels++;
        if (mips < 1 || mips > levels) throw new InvalidDataException("Invalid source mip count.");
        int eb = RageTextureDecoder.GetTextureElementBytes(format), log = Log(eb), px = RageTextureDecoder.IsBlockCompressed(format) ? 4 : 1;
        int halfBlockBits = tileBytes == 4096 ? 6 : 8, maximumTailLevels = tileBytes == 4096 ? 8 : 12;
        int bw = 1 << (halfBlockBits - log / 2), bh = 1 << (halfBlockBits - (log + 1) / 2);
        (int mw, int mh) = log switch { 0 => (16, 16), 1 => (16, 8), 2 => (8, 8), 3 => (8, 4), _ => (4, 4) };
        int ew = (w + px - 1) / px, eh = (h + px - 1) / px, tail = mips;
        var sizes = new List<int>(); var ms = new List<Mip>();
        for (int i = 0; i < mips; i++)
        {
            int wc = Math.Max(1, (ew + (1 << i) - 1) >> i), hc = Math.Max(1, (eh + (1 << i) - 1) >> i);
            if (mips > 1 && wc <= bw / 2 && hc <= bh && mips - i <= maximumTailLevels) { tail = i; break; }
            int pw = (int)Align(wc, bw), ph = (int)Align(hc, bh), size = checked(pw * ph * eb);
            sizes.Add(size); ms.Add(new(Elements(w, i, px), Elements(h, i, px), pw, 0, size, 0, 0));
        }
        int off = tail < mips ? tileBytes : 0;
        for (int i = tail - 1; i >= 0; i--) { ms[i] = ms[i] with { Offset = off }; off = checked(off + sizes[i]); }
        for (int i = tail; i < mips; i++)
        {
            int entry = maximumTailLevels - 1 - (i - tail), moff = entry > 6 ? 16 << entry : entry << 8, x = 0, y = 0;
            for (int bit = 0; bit < 6; bit++) { x |= ((moff >> (9 + 2 * bit)) & 1) << bit; y |= ((moff >> (8 + 2 * bit)) & 1) << bit; }
            ms.Add(new(Elements(w, i, px), Elements(h, i, px), bw, 0, tileBytes, x * mw, y * mh));
        }
        if (off < 1 || off > MaxOutput) throw new NotSupportedException("Texture exceeds the 256MiB resource limit.");
        return new(off, bw, bh, tail, eb, ms) { TileBytes = tileBytes };
    }
    private static int LinearBytes(Mip mip, int eb) => checked(mip.Width * mip.Height * eb);
    private static int Elements(int pixels, int mip, int px) => (Math.Max(1, pixels >> mip) + px - 1) / px;
    private static int Log(int eb) => eb switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, 16 => 4, _ => throw new InvalidDataException("Invalid element stride.") };
    private static long Align(long value, int boundary) => checked((value + boundary - 1) / boundary * boundary);
    private static long SourceOffset(ulong value) => (long)(value & 0x3FFFFFF) * 256;
    private static long VirtualOffset(ulong value) => (long)((value >> 30) & 0x3FFFFF) * 4096;
    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4));
    private static ulong U64(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at, 8));
    private static int IndexOfReference(this IReadOnlyList<Chunk> chunks, Chunk chunk)
    {
        int lo = 0, hi = chunks.Count - 1;
        while (lo <= hi) { int m = (lo + hi) / 2; if (chunks[m].Source == chunk.Source) return m; if (chunks[m].Source < chunk.Source) lo = m + 1; else hi = m - 1; }
        throw new InvalidDataException("Chunk is not part of the validated index.");
    }
}
