using System.Buffers.Binary;
using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace RpfPreview.Core;

public sealed record RageTextureInfo(int Index, string Name, int Width, int Height, int DxgiFormat,
    string Format, int MipCount, int GraphicsOffset, int AllocationBytes, int TileMode)
{
    public uint NameHash { get; init; }
    public ulong? AssetKey { get; init; }
    public int Depth { get; init; } = 1;
    public int Dimension { get; init; } = 1;
    public int PreviewMipLevel => RageTextureDecoder.ChoosePreviewMip(Width, Height, MipCount);
    public int PreviewWidth => Math.Max(1, Width >> PreviewMipLevel);
    public int PreviewHeight => Math.Max(1, Height >> PreviewMipLevel);
    public string Shape => Dimension == 3 ? $"3D volume ({Depth} slices)" : Dimension == 2 ? $"Cube array ({Depth / 6} cubes)" : Depth > 1 ? $"2D array ({Depth} slices)" : "2D";
    public string? PreviewNote
    {
        get
        {
            string note = PreviewMipLevel > 0 ? $"Preview uses mip {PreviewMipLevel}: {PreviewWidth} × {PreviewHeight}; source is {Width} × {Height}. " : "";
            note += DxgiFormat == 65 ? "Alpha mask displayed as grayscale."
        : DxgiFormat is 10 or 34 or 41 or 54 or 95 or 96 ? "Floating-point channels clipped to 0–1 for this 8-bit preview; DDS preserves source values." : "";
            return note.Length == 0 ? null : note.Trim();
        }
    }
}
public sealed record RageTextureDictionary(int SystemBytes, int GraphicsBytes, IReadOnlyList<RageTextureInfo> Textures);
public sealed record RageTexturePixels(int Width, int Height, byte[] Bgra32);








public static class RageTextureDecoder
{
    public static RageTextureDictionary? InspectMetadata(ReadOnlySpan<byte> system, int graphicsLength)
    {
        try { return new(system.Length, graphicsLength, Parse(system, graphicsLength)); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or DecoderFallbackException) { return null; }
    }

    public static RageTextureDictionary? InspectEmbeddedDictionary(ReadOnlySpan<byte> system, int graphicsLength, int dictionaryOffset)
    {
        try { return new(system.Length, graphicsLength, Parse(system, graphicsLength, dictionaryOffset)); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or DecoderFallbackException) { return null; }
    }

    public static long DdsFileSize(RageTextureInfo texture)
    {
        long total = 148; int element = ElementBytes(texture.DxgiFormat), block = IsBc(texture.DxgiFormat) ? 4 : 1;
        for (int mip = 0; mip < texture.MipCount; mip++)
        {
            long width = Math.Max(1, texture.Width >> mip), height = Math.Max(1, texture.Height >> mip);
            long slices = texture.Dimension == 3 ? Math.Max(1, texture.Depth >> mip) : texture.Depth;
            total = checked(total + ((width + block - 1) / block) * ((height + block - 1) / block) * element * slices);
        }
        return total;
    }
    public const int MaxSystemBytes = 16 * 1024 * 1024;
    public const int MaxGraphicsBytes = 256 * 1024 * 1024;
    private const int MaxPreviewPixels = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly int[] Pattern8 = [0, 0, 0, 1, -1, -2, 2, 3, -3, 4, -4, 5];
    private static readonly int[] Pattern16 = [0, 0, 0, 0, -1, -2, 1, 2, -3, 3, -4, 4];
    private static readonly int[] Pattern1 = [1, 2, 3, 4, -1, -2, -3, -4, -5, 5, -6, 6];
    private static readonly int[] Pattern2 = [0, 1, 2, 3, -1, -2, -3, 4, -4, 5, -5, 6];
    private static readonly int[] Pattern4 = [0, 0, 1, 2, -1, -2, -3, 3, -4, 4, -5, 5];

    private static readonly int[][] VolumePatterns = [
        [1, 2, 101, -1, 102, -2, 3, 103, -3, 4, 104, -4],
        [0, 1, 101, -1, 102, -2, 2, 103, -3, 3, 104, -4],
        [0, 0, 1, -1, 101, -2, 2, 102, -3, 3, 103, -4],
        [0, 0, 0, 1, 101, -1, 2, 102, -2, 3, 103, -3],
        [0, 0, 0, 0, 101, -1, 1, 102, -2, 2, 103, -3]];
    private sealed record Mip(int Width, int Height, int PaddedWidth, int Offset, int TailX, int TailY);
    private sealed record Layout(int Size, int BlockWidth, int BlockHeight, IReadOnlyList<Mip> Mips)
    { public int BlockDepth { get; init; } = 1; public int ZBlockStride { get; init; } public bool Volume { get; init; } }


    public static RageTextureDictionary? Inspect(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics)
    {
        try
        {
            var textures = Parse(system, graphics.Length);
            return new(system.Length, graphics.Length, textures);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or DecoderFallbackException)
        { return null; }
    }


    public static RageTexturePixels DecodeTexturePixels(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics, int index, int sliceIndex = 0)
        => DecodeTexturePixelsAtMip(system, graphics, index, Select(system, graphics, index).PreviewMipLevel, sliceIndex);

    public static RageTexturePixels DecodeTexturePixelsAtMip(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics, int index, int level, int sliceIndex = 0)
    {
        var info = Select(system, graphics, index);
        return DecodeSelectedTexturePixels(info, graphics, level, sliceIndex);
    }

    public static RageTexturePixels DecodeEmbeddedTexturePixels(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics,
        int dictionaryOffset, int index, int level = 0, int sliceIndex = 0)
    {
        var textures = Parse(system, graphics.Length, dictionaryOffset);
        if ((uint)index >= textures.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return DecodeSelectedTexturePixels(textures[index], graphics, level, sliceIndex);
    }

    private static RageTexturePixels DecodeSelectedTexturePixels(RageTextureInfo info, ReadOnlySpan<byte> graphics, int level, int sliceIndex)
    {
        if ((uint)level >= info.MipCount) throw new ArgumentOutOfRangeException(nameof(level));
        int width = Math.Max(1, info.Width >> level), height = Math.Max(1, info.Height >> level);
        if ((long)width * height > MaxPreviewPixels)
            throw new NotSupportedException("This texture has no available mip within the 16-megapixel preview limit. DDS export remains available.");
        if ((uint)sliceIndex >= info.Depth) throw new ArgumentOutOfRangeException(nameof(sliceIndex));
        int eb = ElementBytes(info.DxgiFormat);
        var layout = TextureLayout(info);
        var allocation = graphics.Slice(info.GraphicsOffset, info.AllocationBytes);
        if (info.Dimension != 3) allocation = allocation.Slice(checked(sliceIndex * layout.Size), layout.Size);
        int sourceSlice = info.Dimension == 3 ? Math.Min(Math.Max(1, info.Depth >> level) - 1, sliceIndex >> level) : 0;
        byte[] linear = Detile(allocation, layout, eb, level, sourceSlice);
        return DecodeLinearTexturePixels(linear, width, height, info.DxgiFormat);
    }

    internal static int GetTextureElementBytes(int format) => ElementBytes(format);
    internal static bool IsBlockCompressed(int format) => IsBc(format);
    internal static string GetTextureFormatName(int format) => FormatName(format);
    internal static RageTexturePixels DecodeLinearTexturePixels(byte[] linear, int width, int height, int dxgiFormat)
    {
        if (width < 1 || height < 1 || (long)width * height > MaxPreviewPixels)
            throw new NotSupportedException("The preview exceeds the 16-megapixel output limit.");
        int blockPixels = IsBc(dxgiFormat) ? 4 : 1;
        long required = (long)((width + blockPixels - 1) / blockPixels) * ((height + blockPixels - 1) / blockPixels) * ElementBytes(dxgiFormat);
        if (linear.Length != required) throw new InvalidDataException("Linear texture data length disagrees with its descriptor.");
        if (!IsBc(dxgiFormat)) return new(width, height, DecodeRawPixels(linear, dxgiFormat, width * height));
        if (dxgiFormat is 95 or 96)
        {
            var hdr = new BcDecoder().DecodeRawHdr(linear, width, height,
                dxgiFormat == 95 ? CompressionFormat.Bc6U : CompressionFormat.Bc6S);
            byte[] mapped = new byte[checked(width * height * 4)];
            for (int i = 0; i < hdr.Length; i++)
            { mapped[i * 4] = Quantize(hdr[i].b); mapped[i * 4 + 1] = Quantize(hdr[i].g); mapped[i * 4 + 2] = Quantize(hdr[i].r); mapped[i * 4 + 3] = 255; }
            return new(width, height, mapped);
        }
        CompressionFormat format = dxgiFormat switch
        {
            71 or 72 => CompressionFormat.Bc1, 74 or 75 => CompressionFormat.Bc2,
            77 or 78 => CompressionFormat.Bc3, 80 => CompressionFormat.Bc4,
            83 => CompressionFormat.Bc5, 98 or 99 => CompressionFormat.Bc7,
            _ => throw new NotSupportedException("This signed or HDR BC format supports DDS export but not this 8-bit preview.")
        };
        var decoder = new BcDecoder();
        var pixels = decoder.DecodeRaw(linear, width, height, format);
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int i = 0; i < pixels.Length; i++)
        {
            bgra[i * 4] = pixels[i].b; bgra[i * 4 + 1] = pixels[i].g;
            bgra[i * 4 + 2] = pixels[i].r; bgra[i * 4 + 3] = pixels[i].a;
        }
        return new(width, height, bgra);
    }


    public static byte[] CreateDds(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics, int index)
    {
        var info = Select(system, graphics, index);
        int eb = ElementBytes(info.DxgiFormat);
        var layout = TextureLayout(info);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        bool volume = info.Dimension == 3, cube = info.Dimension == 2;
        writer.Write("DDS "u8); writer.Write(124); writer.Write((IsBc(info.DxgiFormat) ? 0x000A1007 : 0x0002100F) | (volume ? 0x800000 : 0));
        writer.Write(info.Height); writer.Write(info.Width);
        writer.Write(IsBc(info.DxgiFormat) ? layout.Mips[0].Width * layout.Mips[0].Height * eb : info.Width * eb);
        writer.Write(volume ? info.Depth : 0); writer.Write(info.MipCount); writer.Write(new byte[44]);
        writer.Write(32); writer.Write(4); writer.Write(0x30315844); writer.Write(new byte[20]);
        writer.Write((info.MipCount > 1 ? 0x401008 : 0x1000) | (volume || cube || info.Depth > 1 ? 8 : 0));
        writer.Write(volume ? 0x200000 : cube ? 0xFE00 : 0); writer.Write(new byte[12]);
        writer.Write(info.DxgiFormat); writer.Write(volume ? 4 : 3); writer.Write(cube ? 4 : 0);
        writer.Write(volume ? 1 : cube ? info.Depth / 6 : info.Depth); writer.Write(0);
        var allocation = graphics.Slice(info.GraphicsOffset, info.AllocationBytes);
        if (volume)
        {
            for (int mip = 0; mip < info.MipCount; mip++)
                for (int z = 0; z < Math.Max(1, info.Depth >> mip); z++) writer.Write(Detile(allocation, layout, eb, mip, z));
        }
        else for (int slice = 0; slice < info.Depth; slice++)
            for (int mip = 0; mip < info.MipCount; mip++) writer.Write(Detile(allocation.Slice(slice * layout.Size, layout.Size), layout, eb, mip));
        return output.ToArray();
    }

    internal static byte[] RetileReplacement(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics,
        int index, byte[] replacementDds, CancellationToken cancellationToken = default)
    {
        var info = Select(system, graphics, index); var dds = DdsTextureReader.Inspect(replacementDds);
        if (dds.Width != info.Width || dds.Height != info.Height || dds.Depth != info.Depth ||
            dds.Dimension != info.Dimension || dds.Mips != info.MipCount || dds.DxgiFormat != info.DxgiFormat ||
            dds.ResourceDimension != (info.Dimension == 3 ? 4 : 3))
            throw new InvalidDataException("Replacement DDS must exactly match the native texture dimensions, format, mip count and array/cube/volume shape.");
        if (dds.AlphaMode is not (0 or 1))
            throw new NotSupportedException("Replacement alpha mode must be unknown or straight; native alpha semantics cannot be converted implicitly.");
        var layout = TextureLayout(info); int eb = ElementBytes(info.DxgiFormat);
        byte[] staged = graphics.ToArray();
        for (int mip = 0; mip < info.MipCount; mip++)
        {
            int slices = info.Dimension == 3 ? Math.Max(1, info.Depth >> mip) : info.Depth;
            for (int slice = 0; slice < slices; slice++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var linear = DdsTextureReader.GetMipPlane(replacementDds, dds, mip, slice); var m = layout.Mips[mip];
                int baseOffset = checked(info.GraphicsOffset + (info.Dimension == 3 ? 0 : slice * layout.Size));
                for (int y = 0; y < m.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int x = 0; x < m.Width; x++)
                    {
                        int at = TiledOffset(layout, m, eb, x, y, info.Dimension == 3 ? slice : 0);
                        Check(at, eb, info.Dimension == 3 ? info.AllocationBytes : layout.Size);
                        linear.Slice((y * m.Width + x) * eb, eb).CopyTo(staged.AsSpan(baseOffset + at, eb));
                    }
                }
            }
        }

        byte[] check = CreateDds(system, staged, index);
        if (!check.AsSpan(148).SequenceEqual(replacementDds.AsSpan(dds.DataOffset)))
            throw new InvalidDataException("Native retile roundtrip did not reproduce the imported DDS payload.");
        return staged;
    }

    internal static bool LooksLikeDictionary(ReadOnlySpan<byte> system)
    {
        if (system.Length < 64) return false;
        int count = U16(system, 0x38);
        return count is > 0 and <= 4096 && U16(system, 0x3A) >= count &&
            U16(system, 0x28) == count && IsSystemPointer(U64(system, 0x20)) &&
            IsSystemPointer(U64(system, 0x30));
    }

    internal static int ChoosePreviewMip(int width, int height, int mips)
    {
        int level = 0;
        while ((long)Math.Max(1, width >> level) * Math.Max(1, height >> level) > MaxPreviewPixels && level + 1 < mips) level++;
        return level;
    }

    internal static bool TryGetResourcePageCounts(ReadOnlySpan<byte> firstSystemPage, out int systemPages, out int graphicsPages)
    {
        systemPages = graphicsPages = 0;
        try
        {
            if (!LooksLikeDictionary(firstSystemPage)) return false;
            int pageInfo = Pointer(firstSystemPage, 8); Check(pageInfo, 16, firstSystemPage.Length);
            systemPages = firstSystemPage[pageInfo + 8]; graphicsPages = firstSystemPage[pageInfo + 9];
            return systemPages is >= 1 and <= 32 && graphicsPages is >= 1 and <= 32;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) { return false; }
    }

    internal static bool TryGetRequiredGraphicsBytes(ReadOnlySpan<byte> system, out int required)
    {
        required = 0;
        try
        {
            var textures = Parse(system, MaxGraphicsBytes);
            required = textures.Max(t => checked(t.GraphicsOffset + t.AllocationBytes));
            return required > 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or DecoderFallbackException)
        { return false; }
    }

    internal static bool TryGetMinimumSystemBytes(ReadOnlySpan<byte> partial, out int required)
    {
        required = 64;
        try
        {
            if (!LooksLikeDictionary(partial)) return false;
            int count = U16(partial, 0x38);
            int page = VirtualSystemOffset(U64(partial, 8)), hashes = VirtualSystemOffset(U64(partial, 0x20)),
                pointers = VirtualSystemOffset(U64(partial, 0x30));
            required = Math.Max(page + 16, Math.Max(hashes + count * 4, pointers + count * 8));
            if (required > MaxSystemBytes) return false;
            if (required > partial.Length) return true;
            for (int i = 0; i < count; i++)
            {
                int t = VirtualSystemOffset(U64(partial, pointers + i * 8));
                required = Math.Max(required, checked(t + 0x70));
                if (required > MaxSystemBytes) return false;
                if (t + 0x70 > partial.Length) continue;
                int format = partial[t + 0x3F];
                if (partial[t + 0x3E] is < 1 or > 3 || partial[t + 0x40] != 5 ||
                    U16(partial, t + 0x0C) != ElementBytes(format) || U64(partial, t + 0x28) != 0x50000000UL + (ulong)t + 0x50) return false;
                int name = checked(VirtualSystemOffset(U64(partial, t + 0x30)) + 8);
                required = Math.Max(required, name + 1);
                if (name < partial.Length)
                {
                    var text = partial.Slice(name, Math.Min(1024, partial.Length - name));
                    if (text.IndexOf((byte)0) < 0) required = Math.Max(required, name + 1024);
                }
            }
            return required <= MaxSystemBytes;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) { return false; }
    }

    private static RageTextureInfo Select(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics, int index)
    {
        var textures = Parse(system, graphics.Length);
        if ((uint)index >= textures.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return textures[index];
    }

    private static IReadOnlyList<RageTextureInfo> Parse(ReadOnlySpan<byte> system, int graphicsLength, int dictionaryOffset = 0)
    {
        int systemLimit = dictionaryOffset == 0 ? MaxSystemBytes : RageModelDecoder.MaxSystemBytes;
        if (system.Length > systemLimit || graphicsLength > MaxGraphicsBytes || graphicsLength < 0 ||
            dictionaryOffset < 0 || dictionaryOffset > system.Length - 64 || !LooksLikeDictionary(system[dictionaryOffset..]))
            throw new InvalidDataException("Not a supported bounded texture dictionary.");
        int count = U16(system, dictionaryOffset + 0x38);
        int hashes = Pointer(system, dictionaryOffset + 0x20), pointers = Pointer(system, dictionaryOffset + 0x30);
        Check(hashes, checked(count * 4), system.Length); Check(pointers, checked(count * 8), system.Length);
        var result = new List<RageTextureInfo>(count);
        var ranges = new List<(int Start, int End)>();
        for (int i = 0; i < count; i++)
        {
            int t = Pointer(system, pointers + i * 8); Check(t, 0x70, system.Length);
            int descriptor = Pointer(system, t + 0x30); Check(descriptor, 8, system.Length);
            ulong? assetKey = null; string name;
            if (U64(system, descriptor) == 0)
            {
                int nameStart = checked(descriptor + 8); Check(nameStart, 1, system.Length);
                var nameSpan = system.Slice(nameStart, Math.Min(1024, system.Length - nameStart));
                int end = nameSpan.IndexOf((byte)0);
                if (end <= 0) throw new InvalidDataException("Invalid texture name.");
                name = StrictUtf8.GetString(nameSpan[..end]);
                if (name.Any(char.IsControl)) throw new InvalidDataException("Invalid texture name characters.");
            }
            else
            {
                Check(descriptor, 16, system.Length);
                ulong marker = U64(system, descriptor), key = U64(system, descriptor + 8);
                if (descriptor < 16 || marker != 0x50000000UL + (ulong)descriptor - 11 || key == 0 ||
                    U32(system, descriptor - 16) != 6 || U32(system, descriptor - 12) != 0 || U32(system, descriptor - 4) != 0)
                    throw new InvalidDataException("Unsupported keyed texture descriptor.");
                assetKey = key; name = $"texture_{key:X16}";
            }
            int width = U16(system, t + 0x38), height = U16(system, t + 0x3A);
            int format = system[t + 0x3F], eb = ElementBytes(format), mips = system[t + 0x42];
            int depth = U16(system, t + 0x3C), dimension = system[t + 0x3E];
            if (width is < 1 or > 16384 || height is < 1 or > 16384 || depth is < 1 or > 2048 ||
                dimension is < 1 or > 3 || (dimension == 2 && depth % 6 != 0) ||
                system[t + 0x40] != 5 || U16(system, t + 0x0C) != eb ||
                U64(system, t + 0x28) != 0x50000000UL + (ulong)t + 0x50)
                throw new InvalidDataException("Unsupported texture descriptor.");
            ulong gfxPointer = U64(system, t + 0x18);
            if (gfxPointer < 0x60000000UL || gfxPointer - 0x60000000UL > int.MaxValue)
                throw new InvalidDataException("Invalid graphics pointer.");
            int gfx = (int)(gfxPointer - 0x60000000UL), length = checked((int)U32(system, t + 8) * eb);
            Check(gfx, length, graphicsLength);
            var info = new RageTextureInfo(i, name, width, height, format, FormatName(format), mips, gfx, length, 5)
            { Depth = depth, Dimension = dimension, NameHash = U32(system, hashes + i * 4), AssetKey = assetKey };
            var layout = TextureLayout(info);
            if (checked(layout.Size * (dimension == 3 ? 1 : depth)) != length) throw new InvalidDataException("Descriptor allocation disagrees with AMD mip layout.");
            if (ranges.Any(r => gfx < r.End && r.Start < gfx + length))
                throw new InvalidDataException("Overlapping texture allocations.");
            ranges.Add((gfx, gfx + length));
            result.Add(info);
        }
        return result;
    }

    private static Layout TextureLayout(RageTextureInfo info) => ComputeLayout(info.Width, info.Height,
        ElementBytes(info.DxgiFormat), info.MipCount, IsBc(info.DxgiFormat) ? 4 : 1, info.Depth, info.Dimension == 3);

    private static Layout ComputeLayout(int w, int h, int eb, int mips, int pixelsPerElement = 4, int depth = 1, bool volume = false)
    {
        int maxMips = 1; for (int size = Math.Max(Math.Max(w, h), volume ? depth : 1); size > 1; size >>= 1) maxMips++;
        if (mips < 1 || mips > maxMips) throw new InvalidDataException("Invalid mip count.");
        int log = ElementLog2(eb);
        (int bw, int bh, int bd) = volume ? log switch { 0 => (16, 16, 16), 1 => (8, 16, 16), 2 => (8, 16, 8), 3 => (8, 8, 8), _ => (4, 8, 8) }
            : log switch { 0 => (64, 64, 1), 1 => (64, 32, 1), 2 => (32, 32, 1), 3 => (32, 16, 1), _ => (16, 16, 1) };
        (int mw, int mh) = volume ? log switch { 0 => (8, 4), 1 or 2 => (4, 4), 3 => (4, 2), _ => (2, 2) }
            : log switch { 0 => (16, 16), 1 => (16, 8), 2 => (8, 8), 3 => (8, 4), _ => (4, 4) };
        int ew = (w + pixelsPerElement - 1) / pixelsPerElement, eh = (h + pixelsPerElement - 1) / pixelsPerElement, tail = mips;
        int tailW = volume ? bw : bw / 2, tailH = volume ? bh / 2 : bh, maxTail = volume ? 5 : 8;
        var layouts = new List<Mip>(); var sizes = new List<int>();
        for (int m = 0; m < mips; m++)
        {
            int wc = Math.Max(1, (ew + (1 << m) - 1) >> m), hc = Math.Max(1, (eh + (1 << m) - 1) >> m);
            if (mips > 1 && wc <= tailW && hc <= tailH && mips - m <= maxTail) { tail = m; break; }
            int pw = Align(wc, bw), ph = Align(hc, bh);
            sizes.Add(checked(pw * ph * eb));
            layouts.Add(new(Elements(w, m, pixelsPerElement), Elements(h, m, pixelsPerElement), pw, 0, 0, 0));
        }
        int off = tail < mips ? 4096 : 0;
        for (int m = tail - 1; m >= 0; m--) { layouts[m] = layouts[m] with { Offset = off }; off = checked(off + sizes[m] * bd); }
        for (int m = tail; m < mips; m++)
        {
            int entry = maxTail - 1 - (m - tail), moff = entry > 6 ? 16 << entry : entry << 8, mx = 0, my = 0;
            for (int bit = 0; bit < 6; bit++) { mx |= ((moff >> (9 + 2 * bit)) & 1) << bit; my |= ((moff >> (8 + 2 * bit)) & 1) << bit; }
            layouts.Add(new(Elements(w, m, pixelsPerElement), Elements(h, m, pixelsPerElement), bw, 0, mx * mw, my * mh));
        }
        int sizeBytes = volume ? checked(off / bd * Align(depth, bd)) : off;
        if (sizeBytes > MaxGraphicsBytes) throw new InvalidDataException("Texture layout exceeds the output limit.");
        return new(sizeBytes, bw, bh, layouts) { Volume = volume, BlockDepth = bd, ZBlockStride = volume ? off : 0 };
    }

    private static byte[] Detile(ReadOnlySpan<byte> allocation, Layout layout, int eb, int level, int slice = 0)
    {
        var mip = layout.Mips[level]; byte[] linear = new byte[checked(mip.Width * mip.Height * eb)];
        for (int y = 0; y < mip.Height; y++) for (int x = 0; x < mip.Width; x++)
        {
            int offset = TiledOffset(layout, mip, eb, x, y, slice); Check(offset, eb, allocation.Length);
            allocation.Slice(offset, eb).CopyTo(linear.AsSpan((y * mip.Width + x) * eb, eb));
        }
        return linear;
    }

    private static int TiledOffset(Layout layout, Mip mip, int eb, int x, int y, int slice)
    {
        int[] pattern = layout.Volume ? VolumePatterns[ElementLog2(eb)] : eb switch { 1 => Pattern1, 2 => Pattern2, 4 => Pattern4, 8 => Pattern8, _ => Pattern16 };
        int block = (y / layout.BlockHeight) * (mip.PaddedWidth / layout.BlockWidth) + x / layout.BlockWidth;
        int address = 0, tx = x + mip.TailX, ty = y + mip.TailY;
        for (int bit = 0; bit < pattern.Length; bit++)
        {
            int s = pattern[bit];
            if (s >= 101) address |= ((slice >> (s - 101)) & 1) << bit;
            else if (s > 0) address |= ((tx >> (s - 1)) & 1) << bit;
            else if (s < 0) address |= ((ty >> (-s - 1)) & 1) << bit;
        }
        return checked((slice / layout.BlockDepth) * layout.ZBlockStride + mip.Offset + block * 4096 + address);
    }

    private static int Elements(int pixels, int mip, int blockPixels = 4) => (Math.Max(1, pixels >> mip) + blockPixels - 1) / blockPixels;
    private static int ElementLog2(int eb) => eb switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, 16 => 4, _ => throw new InvalidDataException("Invalid element size.") };
    private static bool IsBc(int format) => format is >= 70 and <= 84 or >= 94 and <= 99;
    private static int Align(int value, int boundary) => checked((value + boundary - 1) / boundary * boundary);
    private static int ElementBytes(int format) => format switch
    { 61 or 65 => 1, 49 or 54 or 56 => 2, 24 or 28 or 29 or 34 or 41 or 87 or 88 or 91 or 93 => 4,
        10 or 11 or 71 or 72 or 80 or 81 => 8, 74 or 75 or 77 or 78 or 83 or 84 or 95 or 96 or 98 or 99 => 16,
        _ => throw new InvalidDataException("Unsupported BC format.") };
    private static string FormatName(int format) => format switch
    { 10 => "R16G16B16A16_FLOAT", 11 => "R16G16B16A16_UNORM", 24 => "R10G10B10A2_UNORM", 28 => "R8G8B8A8_UNORM", 29 => "R8G8B8A8_UNORM_SRGB",
        34 => "R16G16_FLOAT", 41 => "R32_FLOAT", 49 => "R8G8_UNORM", 54 => "R16_FLOAT", 56 => "R16_UNORM",
        61 => "R8_UNORM", 65 => "A8_UNORM", 87 => "B8G8R8A8_UNORM", 88 => "B8G8R8X8_UNORM", 91 => "B8G8R8A8_UNORM_SRGB", 93 => "B8G8R8X8_UNORM_SRGB",
        71 => "BC1_UNORM", 72 => "BC1_UNORM_SRGB", 74 => "BC2_UNORM", 75 => "BC2_UNORM_SRGB",
        77 => "BC3_UNORM", 78 => "BC3_UNORM_SRGB", 80 => "BC4_UNORM", 81 => "BC4_SNORM",
        83 => "BC5_UNORM", 84 => "BC5_SNORM", 95 => "BC6H_UF16", 96 => "BC6H_SF16",
        98 => "BC7_UNORM", 99 => "BC7_UNORM_SRGB", _ => "Unknown" };

    private static byte[] DecodeRawPixels(ReadOnlySpan<byte> data, int format, int count)
    {
        int eb = ElementBytes(format); byte[] output = new byte[checked(count * 4)];
        for (int i = 0; i < count; i++)
        {
            int p = i * eb; byte r = 0, g = 0, b = 0, a = 255;
            switch (format)
            {
                case 28: case 29: r = data[p]; g = data[p + 1]; b = data[p + 2]; a = data[p + 3]; break;
                case 87: case 91: b = data[p]; g = data[p + 1]; r = data[p + 2]; a = data[p + 3]; break;
                case 88: case 93: b = data[p]; g = data[p + 1]; r = data[p + 2]; break;
                case 61: case 65: r = g = b = data[p]; break;
                case 49: r = data[p]; g = data[p + 1]; break;
                case 56: r = g = b = Quantize(U16(data, p) / 65535.0); break;
                case 54: r = g = b = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p))); break;
                case 41: r = g = b = Quantize(BitConverter.Int32BitsToSingle((int)U32(data, p))); break;
                case 34: r = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p))); g = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p + 2))); break;
                case 10:
                    r = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p))); g = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p + 2)));
                    b = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p + 4))); a = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p + 6))); break;
                case 11:
                    r = Quantize(U16(data, p) / 65535.0);
                    g = Quantize(U16(data, p + 2) / 65535.0);
                    b = Quantize(U16(data, p + 4) / 65535.0);
                    a = Quantize(U16(data, p + 6) / 65535.0);
                    break;
                case 24:
                    uint packed = U32(data, p); r = Quantize((packed & 1023) / 1023.0); g = Quantize(((packed >> 10) & 1023) / 1023.0);
                    b = Quantize(((packed >> 20) & 1023) / 1023.0); a = (byte)((packed >> 30) * 85); break;
                default: throw new NotSupportedException("This raw format has no pixel decoder.");
            }
            output[i * 4] = b; output[i * 4 + 1] = g; output[i * 4 + 2] = r; output[i * 4 + 3] = a;
        }
        return output;
    }
    private static byte Quantize(double value) => double.IsNaN(value) ? (byte)0 : (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private static bool IsSystemPointer(ulong value) => value >= 0x50000000UL && value < 0x51000000UL;
    private static int Pointer(ReadOnlySpan<byte> data, int at)
    {
        int offset = VirtualSystemOffset(U64(data, at)); Check(offset, 1, data.Length); return offset;
    }
    private static int VirtualSystemOffset(ulong value)
    { if (!IsSystemPointer(value)) throw new InvalidDataException("Invalid system pointer."); return checked((int)(value - 0x50000000UL)); }
    private static void Check(int offset, int count, int length)
    { if (offset < 0 || count < 0 || offset > length || count > length - offset) throw new InvalidDataException("Resource range exceeds its section."); }
    private static ushort U16(ReadOnlySpan<byte> data, int at) { Check(at, 2, data.Length); return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2)); }
    private static uint U32(ReadOnlySpan<byte> data, int at) { Check(at, 4, data.Length); return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4)); }
    private static ulong U64(ReadOnlySpan<byte> data, int at) { Check(at, 8, data.Length); return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at, 8)); }
}
