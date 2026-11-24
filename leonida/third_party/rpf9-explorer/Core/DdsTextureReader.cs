using System.Buffers.Binary;

namespace RpfPreview.Core;

public sealed record DdsSubresourceInfo(int Slice, int Mip, int Width, int Height, int Depth,
    int Offset, int Size, int RowBytes, int SliceBytes);

public sealed record DdsTextureInfo(int Width, int Height, int Depth, int Dimension, int ResourceDimension,
    int ArraySize, int SliceCount, int Mips, int DxgiFormat, string Format, int DataOffset, int DataSize,
    bool HasDx10Header, int AlphaMode, IReadOnlyList<DdsSubresourceInfo> Subresources)
{
    public int PreviewMipLevel => RageTextureDecoder.ChoosePreviewMip(Width, Height, Mips);
    public int PreviewWidth => Math.Max(1, Width >> PreviewMipLevel);
    public int PreviewHeight => Math.Max(1, Height >> PreviewMipLevel);
    public string Shape => Dimension == 3 ? $"3D volume ({Depth} slices)" : Dimension == 2 ? $"Cube array ({ArraySize} cubes)" : SliceCount > 1 ? $"2D array ({SliceCount} slices)" : ResourceDimension == 2 ? "1D" : "2D";
    public string? PreviewNote
    {
        get
        {
            string note = PreviewMipLevel > 0 ? $"Preview uses mip {PreviewMipLevel}: {PreviewWidth} × {PreviewHeight}; source is {Width} × {Height}. " : "";
            if (DxgiFormat is 10 or 34 or 41 or 54 or 95 or 96) note += "Floating-point channels clip to 0–1 for display; DDS source values are preserved. ";
            if (DxgiFormat == 65) note += "Alpha mask displayed as grayscale. ";
            if (AlphaMode == 2) note += "Premultiplied alpha is converted for display. ";
            return note.Length == 0 ? null : note.Trim();
        }
    }
}








public static class DdsTextureReader
{
    public const int MaxFileBytes = 256 * 1024 * 1024;
    private const int MaxSlices = 2048;

    public static DdsTextureInfo Inspect(byte[] data)
    {
        try { return InspectCore(data); }
        catch (OverflowException e) { throw new InvalidDataException("DDS dimensions or subresource sizes overflow the supported limits.", e); }
    }

    private static DdsTextureInfo InspectCore(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 128 || data.Length > MaxFileBytes || !data.AsSpan(0, 4).SequenceEqual("DDS "u8) || U32(data, 4) != 124 || U32(data, 76) != 32)
            throw new InvalidDataException("Invalid, truncated or oversized DDS header.");
        int width = Dimension(U32(data, 16)), height = Dimension(U32(data, 12));
        int mips = checked((int)U32(data, 28)); if (mips == 0) mips = 1;
        uint flags = U32(data, 80), fourCC = U32(data, 84), caps2 = U32(data, 112);
        bool dx10 = (flags & 4) != 0 && fourCC == 0x30315844;
        int format, array = 1, slices = 1, depth = 1, dimension = 1, resourceDimension = 3, alphaMode = 0, start = 128;
        if (dx10)
        {
            if (data.Length < 148) throw new InvalidDataException("Truncated DDS DX10 extension.");
            format = checked((int)U32(data, 128)); resourceDimension = checked((int)U32(data, 132));
            uint misc = U32(data, 136); array = checked((int)U32(data, 140)); alphaMode = (int)(U32(data, 144) & 7);
            if (array < 1 || array > MaxSlices || alphaMode > 4) throw new InvalidDataException("Invalid DDS array or alpha mode.");
            if (resourceDimension == 2)
            {
                if (height != 1 || (misc & 4) != 0) throw new InvalidDataException("DDS 1D texture shape is inconsistent.");
                slices = array; depth = array;
            }
            else if (resourceDimension == 3)
            {
                bool cube = (misc & 4) != 0; dimension = cube ? 2 : 1;
                if (cube && width != height) throw new InvalidDataException("Cube faces must be square.");
                slices = checked(array * (cube ? 6 : 1)); depth = slices;
            }
            else if (resourceDimension == 4)
            {
                if (array != 1 || (misc & 4) != 0) throw new InvalidDataException("DDS volume cannot be an array or cube.");
                dimension = 3; depth = Dimension(U32(data, 24)); slices = depth;
            }
            else throw new NotSupportedException("Unsupported DDS resource dimension.");
            start = 148;
        }
        else
        {
            (format, alphaMode) = LegacyFormat(data, flags, fourCC);
            bool volume = (caps2 & 0x200000) != 0, cube = (caps2 & 0x200) != 0;
            if (volume && cube) throw new InvalidDataException("DDS cannot be both a cube and a volume.");
            if (volume) { dimension = 3; resourceDimension = 4; depth = Dimension(U32(data, 24)); slices = depth; }
            else if (cube)
            {
                if ((caps2 & 0xFC00) != 0xFC00 || width != height) throw new NotSupportedException("Incomplete or nonsquare legacy cube maps are not supported.");
                dimension = 2; depth = slices = 6;
            }
        }
        if (slices > MaxSlices) throw new NotSupportedException("DDS has too many preview slices.");
        int maxMips = 1; for (int size = Math.Max(Math.Max(width, height), dimension == 3 ? depth : 1); size > 1; size >>= 1) maxMips++;
        if (mips < 1 || mips > maxMips) throw new InvalidDataException("DDS mip count exceeds its dimensions.");
        int eb = RageTextureDecoder.GetTextureElementBytes(format), px = RageTextureDecoder.IsBlockCompressed(format) ? 4 : 1;
        var subresources = new List<DdsSubresourceInfo>(); long offset = start;
        for (int slice = 0; slice < (dimension == 3 ? 1 : slices); slice++) for (int mip = 0; mip < mips; mip++)
        {
            int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip), d = dimension == 3 ? Math.Max(1, depth >> mip) : 1;
            int row = checked(((w + px - 1) / px) * eb), plane = checked(row * ((h + px - 1) / px));
            long size = checked((long)plane * d);
            if (size > MaxFileBytes || offset > data.Length - size) throw new InvalidDataException("DDS mip/slice data is truncated or exceeds the output limit.");
            subresources.Add(new(slice, mip, w, h, d, (int)offset, (int)size, row, plane)); offset += size;
        }
        if (offset != data.Length) throw new InvalidDataException("DDS has trailing bytes or inconsistent declared mip/slice data.");
        return new(width, height, depth, dimension, resourceDimension, array, slices, mips, format,
            RageTextureDecoder.GetTextureFormatName(format), start, data.Length - start, dx10, alphaMode, subresources);
    }

    public static RageTexturePixels DecodeTexturePixels(byte[] data, int slice = 0)
    {
        var info = Inspect(data);
        if ((uint)slice >= info.SliceCount) throw new ArgumentOutOfRangeException(nameof(slice));
        int mip = info.PreviewMipLevel;
        int z = info.Dimension == 3 ? Math.Min(Math.Max(1, info.Depth >> mip) - 1, slice >> mip) : slice;
        byte[] plane = GetMipPlane(data, info, mip, z).ToArray();
        var pixels = RageTextureDecoder.DecodeLinearTexturePixels(plane, info.PreviewWidth, info.PreviewHeight, info.DxgiFormat);
        if (info.AlphaMode == 2)
            for (int p = 0; p < pixels.Bgra32.Length; p += 4)
            {
                int a = pixels.Bgra32[p + 3];
                for (int c = 0; c < 3; c++) pixels.Bgra32[p + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, (pixels.Bgra32[p + c] * 255 + a / 2) / a);
            }
        return pixels;
    }

    internal static ReadOnlySpan<byte> GetMipPlane(byte[] data, DdsTextureInfo info, int mip, int slice)
    {
        if ((uint)mip >= info.Mips) throw new ArgumentOutOfRangeException(nameof(mip));
        if (info.Dimension == 3)
        {
            var sub = info.Subresources[mip];
            if ((uint)slice >= sub.Depth) throw new ArgumentOutOfRangeException(nameof(slice));
            return data.AsSpan(checked(sub.Offset + slice * sub.SliceBytes), sub.SliceBytes);
        }
        if ((uint)slice >= info.SliceCount) throw new ArgumentOutOfRangeException(nameof(slice));
        var item = info.Subresources[checked(slice * info.Mips + mip)]; return data.AsSpan(item.Offset, item.Size);
    }

    private static (int Format, int Alpha) LegacyFormat(byte[] data, uint flags, uint fourCC)
    {
        if ((flags & 4) != 0)
            return fourCC switch
            {
                0x31545844 => (71, 0),
                0x32545844 => (74, 2),
                0x33545844 => (74, 0), 0x34545844 => (77, 2), 0x35545844 => (77, 0),
                0x31495441 or 0x55344342 => (80, 0), 0x53344342 => (81, 0),
                0x32495441 or 0x55354342 => (83, 0), 0x53354342 => (84, 0),
                111 => (54, 0), 112 => (34, 0), 113 => (10, 0), 114 => (41, 0),
                _ => throw new NotSupportedException("Unsupported legacy DDS FOURCC. Use a supported DX10 DDS format.")
            };
        uint bits = U32(data, 88), r = U32(data, 92), g = U32(data, 96), b = U32(data, 100), a = U32(data, 104);
        if ((flags & 0x40) != 0 && bits == 32)
        {
            if (r == 0xFF && g == 0xFF00 && b == 0xFF0000 && a == 0xFF000000) return (28, 1);
            if (r == 0xFF0000 && g == 0xFF00 && b == 0xFF && a == 0xFF000000) return (87, 1);
            if (r == 0xFF0000 && g == 0xFF00 && b == 0xFF && a == 0) return (88, 3);
            if (r == 0x3FF && g == 0xFFC00 && b == 0x3FF00000 && a == 0xC0000000) return (24, 1);
        }
        if ((flags & 0x20000) != 0 && g == 0 && b == 0 && a == 0)
        { if (bits == 8 && r == 0xFF) return (61, 0); if (bits == 16 && r == 0xFFFF) return (56, 0); }
        if ((flags & 2) != 0 && bits == 8 && a == 0xFF) return (65, 0);
        throw new NotSupportedException("Unsupported legacy DDS pixel masks.");
    }

    private static int Dimension(uint value) => value is > 0 and <= 16384 ? (int)value : throw new InvalidDataException("DDS dimension is invalid or exceeds 16384.");
    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4));
}
