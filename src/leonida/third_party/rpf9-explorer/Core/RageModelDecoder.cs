using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace RpfPreview.Core;

public sealed record RageMesh(string Name, IReadOnlyList<Vector3> Positions, IReadOnlyList<int> Indices)
{
    public IReadOnlyList<Vector2>? TexCoords { get; init; }
    public IReadOnlyList<uint>? VertexColors { get; init; }
    public int? ShaderIndex { get; init; }
    public IReadOnlyList<uint>? BonePaletteHashes { get; init; }
}

public sealed record RageTextureReference(uint ParameterHash, int TextureIndex, int SystemOffset,
    int AssetDescriptorOffset, ulong OpaqueId64, uint OpaqueId32, uint Tag)
{
    public uint NameHash => OpaqueId32;
}
public sealed record RageMaterial(int ShaderIndex, uint ShaderHash, uint PresetHash,
    IReadOnlyList<RageTextureReference> Textures)
{
    public byte? RenderBucket { get; init; }
    public uint? RenderBucketMask { get; init; }
}
public sealed record RageModel(string Name, IReadOnlyList<RageMesh> Meshes, Vector3 BoundsMin, Vector3 BoundsMax)
{
    public bool IsFragment { get; init; }
    public bool IsDictionaryChild { get; init; }
    public Vector3? DeclaredBoundsMin { get; init; }
    public Vector3? DeclaredBoundsMax { get; init; }
    public bool HasBoneBindings { get; init; }
    public int DrawableOffset { get; init; }
    public string? PresentationNote { get; init; }
    public IReadOnlyList<RageMaterial>? Materials { get; init; }
    public IReadOnlyList<RageTextureInfo>? EmbeddedTextures { get; init; }
    public int? EmbeddedTextureDictionaryOffset { get; init; }
}

public sealed record RageModelExtent(int RequiredSystemBytes, int RequiredGraphicsBytes,
    bool MetadataComplete, int DrawableOffset, bool IsFragment)
{
    public bool IsDrawableDictionary { get; init; }
}

public sealed record RageDrawableDictionaryEntry(int DrawableOffset, string Name, uint NameHash);






public static class RageModelDecoder
{
    public const int MaxSystemBytes = 32 * 1024 * 1024;
    public const int MaxGraphicsBytes = 224 * 1024 * 1024;
    private const int MaxMeshes = 1024;
    private const int MaxTotalVertices = 2_000_000;
    private const int MaxTotalTriangles = 2_000_000;
    private const int MaxDictionaryChildren = 4096;

    public static bool LooksLikeDrawable(ReadOnlySpan<byte> prefix) => LooksLikeDrawableHeader(prefix, false);


    public static bool LooksLikeFragment(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 176 || BinaryPrimitives.ReadUInt64LittleEndian(prefix) != 0) return false;

        foreach (int at in new[] { 8, 24, 168 })
            if (!SystemPointer(BinaryPrimitives.ReadUInt64LittleEndian(prefix[at..]))) return false;

        foreach (int at in new[] { 16, 32, 40 })
        {
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(prefix[at..]);
            if (pointer != 0 && !SystemPointer(pointer)) return false;
        }


        ushort a = BinaryPrimitives.ReadUInt16LittleEndian(prefix[48..]);
        ushort b = BinaryPrimitives.ReadUInt16LittleEndian(prefix[50..]);
        ushort c = BinaryPrimitives.ReadUInt16LittleEndian(prefix[52..]);
        ushort d = BinaryPrimitives.ReadUInt16LittleEndian(prefix[54..]);
        return a is > 0 and <= 4096 && b <= 4096 && c <= d && d <= 4096;
    }




    public static bool LooksLikeDrawableDictionary(ReadOnlySpan<byte> prefix)
    {
        int needed = DrawableDictionaryCheckBytes(prefix);
        if (needed < 0) return false;
        if (needed > prefix.Length) return true;
        return LooksLikeDrawableHeader(prefix[(needed - 176)..], true);
    }




    public static int DrawableDictionaryCheckBytes(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 64 || BinaryPrimitives.ReadUInt64LittleEndian(prefix) != 0 ||
            !SystemPointer(BinaryPrimitives.ReadUInt64LittleEndian(prefix[8..])) ||
            !SystemPointer(BinaryPrimitives.ReadUInt64LittleEndian(prefix[32..])) ||
            !SystemPointer(BinaryPrimitives.ReadUInt64LittleEndian(prefix[48..]))) return -1;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(prefix[56..]);
        if (!(count is > 0 and <= MaxDictionaryChildren &&
            count == BinaryPrimitives.ReadUInt16LittleEndian(prefix[58..]) &&
            count == BinaryPrimitives.ReadUInt16LittleEndian(prefix[40..]) &&
            count == BinaryPrimitives.ReadUInt16LittleEndian(prefix[42..]))) return -1;
        int values = (int)(BinaryPrimitives.ReadUInt64LittleEndian(prefix[48..]) - 0x50000000);
        if (values > prefix.Length - 8) return values + 8;
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(prefix[values..]);
        if (!SystemPointer(first)) return -1;
        return (int)(first - 0x50000000) + 176;
    }

    public static bool LooksLikeResource(ReadOnlySpan<byte> prefix) =>
        LooksLikeDrawable(prefix) || LooksLikeFragment(prefix) || LooksLikeDrawableDictionary(prefix);






    public static RageModelExtent? ProbeRequiredExtents(ReadOnlySpan<byte> system)
    {
        if (system.Length > MaxSystemBytes) return null;
        if (system.Length < 176)
            return system.Length >= 16 && BinaryPrimitives.ReadUInt64LittleEndian(system) == 0 &&
                SystemPointer(BinaryPrimitives.ReadUInt64LittleEndian(system[8..])) ? new(176, 0, false, 0, false) : null;
        if (!LooksLikeResource(system)) return null;
        var reader = new ExtentReader(system);
        try { return reader.Probe(); }
        catch (NeedSystemBytes ex) { return new(Math.Max(ex.Required, reader.SystemEnd), reader.GraphicsEnd, false, reader.Drawable, reader.Fragment)
            { IsDrawableDictionary = reader.Dictionary }; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException) { return null; }
    }

    private static bool LooksLikeDrawableHeader(ReadOnlySpan<byte> prefix, bool embedded)
    {
        if (prefix.Length < 176 || BinaryPrimitives.ReadUInt64LittleEndian(prefix) != 0) return false;
        ulong pages = BinaryPrimitives.ReadUInt64LittleEndian(prefix[8..]);
        ulong shader = BinaryPrimitives.ReadUInt64LittleEndian(prefix[16..]);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(prefix[80..]);
        ulong name = BinaryPrimitives.ReadUInt64LittleEndian(prefix[168..]);
        if ((embedded ? pages != 0 : !SystemPointer(pages)) || !SystemPointer(shader) || !SystemPointer(high) ||
            !(SystemPointer(name) || (embedded && name == 0))) return false;
        Vector3 min = ReadVector(prefix, 48), max = ReadVector(prefix, 64);
        float radius = BinaryPrimitives.ReadSingleLittleEndian(prefix[44..]);
        return ValidBounds(min, max) && float.IsFinite(radius) && radius is >= 0 and < 1_000_000;
    }

    public static RageModel? Inspect(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics) => Decode(system, graphics);

    public static IReadOnlyList<RageDrawableDictionaryEntry>? InspectDrawableDictionary(ReadOnlySpan<byte> system)
    {
        if (system.Length > MaxSystemBytes || !LooksLikeDrawableDictionary(system)) return null;
        try
        {
            var reader = new ModelReader(system, ReadOnlySpan<byte>.Empty);
            return reader.ReadDictionary();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        { return null; }
    }

    public static RageModel? Decode(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics) => Decode(system, graphics, null);

    public static RageModel? Decode(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics, int? drawableOffset)
    {
        if (system.Length > MaxSystemBytes || graphics.Length is < 1 or > MaxGraphicsBytes || !LooksLikeResource(system)) return null;
        try
        {
            if (LooksLikeDrawableDictionary(system))
            {
                var children = InspectDrawableDictionary(system);
                if (children is null) return null;
                foreach (var child in children)
                {
                    if (drawableOffset is not null && drawableOffset != child.DrawableOffset) continue;
                    try
                    {
                        var reader = new ModelReader(system, graphics);
                        return reader.Decode(child.DrawableOffset, true);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
                    { if (drawableOffset is not null) return null; }
                }
                return null;
            }
            var standalone = new ModelReader(system, graphics);
            return standalone.Decode(drawableOffset);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        { return null; }
    }

    public static IReadOnlyList<RageModel> DecodeModels(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics)
    {
        if (!LooksLikeDrawableDictionary(system))
        {
            var model = Decode(system, graphics);
            return model is null ? [] : [model];
        }
        var children = InspectDrawableDictionary(system);
        if (children is null || graphics.Length is < 1 or > MaxGraphicsBytes) return [];
        var result = new List<RageModel>();
        long vertices = 0, triangles = 0, meshes = 0;
        foreach (var child in children)
        {
            RageModel? model;
            try
            {
                var reader = new ModelReader(system, graphics);
                model = reader.Decode(child.DrawableOffset, true);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
            { continue; }
            if (model is null) continue;
            meshes += model.Meshes.Count;
            foreach (var mesh in model.Meshes) { vertices += mesh.Positions.Count; triangles += mesh.Indices.Count / 3; }
            if (meshes > MaxMeshes || vertices > MaxTotalVertices || triangles > MaxTotalTriangles) return [];
            result.Add(model);
        }
        return result;
    }

    public static string CreateObj(RageModel model)
    {
        var text = new StringBuilder("# Recovered native drawable geometry. Materials and textures are omitted.\n");
        if (model.PresentationNote is not null) text.Append("# ").Append(model.PresentationNote).Append('\n');
        int first = 1, firstTexture = 1;
        foreach (var mesh in model.Meshes)
        {
            text.Append("o ").Append(mesh.Name.Replace('\r', '_').Replace('\n', '_')).Append('\n');
            foreach (Vector3 p in mesh.Positions)
                text.Append("v ").Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(p.Z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            bool hasUv = mesh.TexCoords is not null && mesh.TexCoords.Count == mesh.Positions.Count;
            if (hasUv)
                foreach (Vector2 uv in mesh.TexCoords!)
                    text.Append("vt ").Append(uv.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                        .Append(uv.Y.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < mesh.Indices.Count; i += 3)
            {
                text.Append('f');
                for (int corner = 0; corner < 3; corner++)
                {
                    int index = mesh.Indices[i + corner]; text.Append(' ').Append(first + index);
                    if (hasUv) text.Append('/').Append(firstTexture + index);
                }
                text.Append('\n');
            }
            first = checked(first + mesh.Positions.Count);
            if (hasUv) firstTexture = checked(firstTexture + mesh.TexCoords!.Count);
        }
        return text.ToString();
    }

    private static bool SystemPointer(ulong p) => p >= 0x50000000 && p < 0x50000000UL + MaxSystemBytes;
    private static Vector3 ReadVector(ReadOnlySpan<byte> bytes, int at) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[(at + 4)..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[(at + 8)..]));
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) &&
        Math.Abs(p.X) < 10_000_000 && Math.Abs(p.Y) < 10_000_000 && Math.Abs(p.Z) < 10_000_000;
    private static bool ValidBounds(Vector3 min, Vector3 max) => Finite(min) && Finite(max) &&
        min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;

    private sealed class NeedSystemBytes(int required) : Exception
    {
        public int Required { get; } = required;
    }

    private ref struct ExtentReader(ReadOnlySpan<byte> system)
    {
        private readonly ReadOnlySpan<byte> sys = system;
        public int SystemEnd { get; private set; }
        public int GraphicsEnd { get; private set; }
        public int Drawable { get; private set; }
        public bool Fragment { get; private set; }
        public bool Dictionary { get; private set; }
        private int totalMeshes;
        private static void Check(bool condition) { if (!condition) throw new InvalidDataException("Unsupported model ownership layout."); }
        private void Cover(int at, int size)
        {
            Check(at >= 0 && size >= 0 && (long)at + size <= MaxSystemBytes);
            SystemEnd = Math.Max(SystemEnd, checked(at + size));
            if (at > sys.Length || size > sys.Length - at) throw new NeedSystemBytes(at + size);
        }
        private ushort U16(int at) { Cover(at, 2); return BinaryPrimitives.ReadUInt16LittleEndian(sys[at..]); }
        private uint U32(int at) { Cover(at, 4); return BinaryPrimitives.ReadUInt32LittleEndian(sys[at..]); }
        private ulong U64(int at) { Cover(at, 8); return BinaryPrimitives.ReadUInt64LittleEndian(sys[at..]); }
        private int S(int pointerAt, int size)
        {
            ulong p = U64(pointerAt); Check(p >= 0x50000000 && p - 0x50000000 <= MaxSystemBytes);
            int at = checked((int)(p - 0x50000000)); Cover(at, size); return at;
        }
        private void G(int pointerAt, int size)
        {
            ulong p = U64(pointerAt); Check(p >= 0x60000000 && p - 0x60000000 <= MaxGraphicsBytes && size > 0 &&
                (ulong)size <= MaxGraphicsBytes - (p - 0x60000000));
            GraphicsEnd = Math.Max(GraphicsEnd, checked((int)(p - 0x60000000) + size));
        }
        private string Name(int pointerAt, int skip = 0)
        {
            int at = checked(S(pointerAt, skip + 1) + skip);
            int available = Math.Min(256, sys.Length - at), end = sys.Slice(at, available).IndexOf((byte)0);
            if (end < 0 && available < 256) { Cover(at, available + 1); throw new InvalidDataException(); }
            Check(end is > 0 and < 256); Cover(at, end + 1);
            string value = new UTF8Encoding(false, true).GetString(sys.Slice(at, end));
            foreach (char c in value) Check(!char.IsControl(c));
            return value;
        }
        private void Buffer(int at)
        {
            Cover(at, 64);
            int bytes = checked((int)U32(at + 8) * U16(at + 12));
            Check(U16(at + 12) is > 0 and <= 32 && bytes > 0);
            G(at + 24, bytes);
            if (U64(at + 40) != 0) S(at + 40, 32);
        }

        public RageModelExtent Probe()
        {
            Dictionary = LooksLikeDrawableDictionary(sys);
            Cover(0, Dictionary ? 64 : 176);
            int pages = S(8, 16), systemPages = sys[pages + 8], graphicsPages = sys[pages + 9];
            Check(systemPages is > 0 and <= 32 && graphicsPages is > 0 and <= 32);
            Cover(pages, checked(16 + 8 * (systemPages + graphicsPages)));
            if (Dictionary)
            {
                int count = U16(56), hashes = S(32, checked(count * 4)), pointers = S(48, checked(count * 8));
                Check(count is > 0 and <= MaxDictionaryChildren && hashes >= 64 && pointers >= 64);
                var children = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    int child = S(pointers + i * 8, 192);
                    Check(child >= 64 && children.Add(child) && LooksLikeDrawableHeader(sys[child..], true));
                    if (i == 0) Drawable = child;
                    ProbeDrawable(child);
                }
            }
            else
            {
                Fragment = !LooksLikeDrawable(sys);
                if (Fragment)
                {
                    Check(LooksLikeFragment(sys));
                    Name(168); Drawable = S(24, 192);
                    Check(Drawable != 0 && LooksLikeDrawableHeader(sys[Drawable..], true));
                }
                ProbeDrawable(Drawable);
            }
            Check(GraphicsEnd > 0);
            return new(SystemEnd, GraphicsEnd, true, Drawable, Fragment) { IsDrawableDictionary = Dictionary };
        }

        private void ProbeDrawable(int drawable)
        {
            Cover(drawable, 192);
            if (U64(drawable + 168) != 0) Name(drawable + 168);
            var seenLods = new HashSet<int>();
            for (int lodField = 80; lodField <= 104; lodField += 8)
            {
                if (U64(drawable + lodField) == 0) { Check(lodField != 80); continue; }
                int lod = S(drawable + lodField, 16), models = U16(lod + 8);
                if (!seenLods.Add(lod)) continue;
                Check(models is > 0 and <= 256 && models == U16(lod + 10));
                int pointers = S(lod, models * 8);
                var seenModels = new HashSet<int>();
                var seenGeometry = new HashSet<int>();
                for (int mi = 0; mi < models; mi++)
                {
                    int model = S(pointers + mi * 8, 56), count = U16(model + 16);
                    Check(seenModels.Add(model) && count is > 0 and <= MaxMeshes && count == U16(model + 18) && count == U16(model + 54));
                    int boneCount = U16(model + 20); Check(boneCount <= 4096);
                    int geometries = S(model + 8, count * 8);
                    S(model + 24, checked((count + (count > 1 ? 1 : 0)) * 32));
                    if (U64(model + 40) != 0) S(model + 40, boneCount > 0 ? checked(boneCount * 4) : 1);
                    for (int i = 0; i < count; i++)
                    {
                        Check(++totalMeshes <= MaxMeshes);
                        int geometry = S(geometries + i * 8, 160); Check(seenGeometry.Add(geometry));
                        int vb = S(geometry + 8, 80), fvf = S(vb + 64, 320);
                        uint flags = U32(fvf + 0x138); int vertices = checked((int)(flags >> 10));
                        int size = sys[fvf + 0xD0], format = sys[fvf + 0x104];
                        Check((flags & 1) != 0 && vertices is > 0 and <= 1_000_000 &&
                            ((size == 12 && format == 6) || (size == 8 && format == 16)));
                        int bytes = checked((int)U32(vb + 8) * U16(vb + 12)), offset = checked((int)U32(fvf));
                        Check(offset >= 96 && offset <= bytes && (long)vertices * size <= bytes - offset);
                        Buffer(vb);
                        int descriptor = S(geometry + 64, 16), ib = S(descriptor, 64);
                        Check(U16(ib + 12) == 4); Buffer(ib);
                    }
                }
            }
            int shader = S(drawable + 16, 64);
            if (U64(shader + 48) != 0)
            {
                int descriptor = S(shader + 48, 32);
                Buffer(S(descriptor, 64));
            }
            if (U64(shader + 8) != 0) TextureDictionary(S(shader + 8, 64));
            ProbeMaterials(shader);
            if (U64(drawable + 176) != 0)
            {
                int renderer = S(drawable + 176, 80);
                for (int i = 0; i < 5; i++)
                {
                    int list = renderer + i * 16, count = U16(list + 8);

                    Check(count == U16(list + 10) && (i == 4 || count <= 4096));
                    if (count > 0) S(list, checked(count * (i < 4 ? 4 : 80)));
                }
            }
        }

        private void ProbeMaterials(int group)
        {
            int count = U16(group + 24);
            if (count == 0) return;
            Check(count <= 512 && count == U16(group + 26));
            int pointers = S(group + 16, count * 8);
            for (int i = 0; i < count; i++)
            {
                int shader = S(pointers + i * 8, 64);
                if (U32(shader + 4) != 0x6D657461) continue;
                int info = S(shader + 32, 8), parameters = sys[info + 4], textures = sys[info + 1];
                Cover(info, checked(8 + parameters * 8));
                if (textures == 0) continue;
                int textureArray = S(shader + 16, textures * 8);
                for (int j = 0; j < textures; j++)
                {
                    if (U64(textureArray + j * 8) == 0) continue;
                    int texture = S(textureArray + j * 8, 80);
                    if (U64(texture + 48) == 0) continue;
                    int asset = S(texture + 48, 16);
                    if (asset >= 16 && U32(asset - 16) == 6) Cover(asset - 16, 32);
                }
            }
        }

        private void TextureDictionary(int dictionary)
        {
            int count = U16(dictionary + 56);
            Check(count <= 4096 && count == U16(dictionary + 58));
            if (count == 0) return;
            Check(dictionary >= 64 && U16(dictionary + 40) == count && U16(dictionary + 42) == count);
            int hashes = S(dictionary + 32, checked(count * 4)); Check(hashes >= 64);
            int array = S(dictionary + 48, checked(count * 8)); Check(array >= 64);
            for (int i = 0; i < count; i++)
            {
                int texture = S(array + i * 8, 112); Check(texture >= 64);
                if (U64(texture + 40) != 0) Check(S(texture + 40, 32) >= 64);
                int name = S(texture + 48, 9); Check(name >= 64);
                if (U64(name) == 0) Name(texture + 48, 8);
                else
                {
                    Check(name >= 16);
                    Cover(name - 16, 32);
                }
            }


            byte[] temporary = sys.ToArray(); sys.Slice(dictionary, 64).CopyTo(temporary);
            if (RageTextureDecoder.TryGetRequiredGraphicsBytes(temporary, out int required))
            {
                Check(required is > 0 and <= MaxGraphicsBytes);
                GraphicsEnd = Math.Max(GraphicsEnd, required);
            }
        }
    }

    private ref struct ModelReader(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics)
    {
        private readonly ReadOnlySpan<byte> sys = system;
        private readonly ReadOnlySpan<byte> gfx = graphics;
        private int totalVertices, totalTriangles;
        private bool hasBoneBindings;

        private static void Require(bool condition) { if (!condition) throw new InvalidDataException("Unsupported or inconsistent drawable layout."); }
        private static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(p, 2));
        private static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(p, 4));
        private int S(int at, int size = 1) => Pointer(sys, at, 0x50000000, sys.Length, size);
        private int G(int at, int size) => Pointer(sys, at, 0x60000000, gfx.Length, size);
        private static int Pointer(ReadOnlySpan<byte> source, int at, ulong basis, int length, int size)
        {
            ulong p = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(at, 8));
            Require(p >= basis && p - basis <= (ulong)length && size >= 0 && (ulong)size <= (ulong)length - (p - basis));
            return checked((int)(p - basis));
        }

        public IReadOnlyList<RageDrawableDictionaryEntry> ReadDictionary()
        {
            Require(LooksLikeDrawableDictionary(sys));
            int count = U16(sys, 56), pointers = S(48, checked(count * 8)), hashes = S(32, checked(count * 4));
            Require(pointers >= 64 && hashes >= 64);
            S(8, 16);
            var result = new List<RageDrawableDictionaryEntry>(count);
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int child = S(pointers + i * 8, 192);
                Require(child >= 64 && seen.Add(child) && LooksLikeDrawableHeader(sys[child..], true));
                string name = ReadName(child + 168);
                result.Add(new(child, name, U32(sys, hashes + i * 4)));
            }
            return result;
        }

        public RageModel Decode(int? selectedOffset = null, bool dictionaryOwnershipValidated = false)
        {
            int pageInfo = S(8, 16);
            Require(sys[pageInfo + 8] is > 0 and <= 32 && sys[pageInfo + 9] is > 0 and <= 32);
            bool dictionary = LooksLikeDrawableDictionary(sys);
            bool fragment = !dictionary && !LooksLikeDrawable(sys);
            int drawable = 0;
            string? parentName = null;
            if (dictionary)
            {
                if (dictionaryOwnershipValidated)
                {
                    drawable = selectedOffset ?? throw new InvalidDataException("A dictionary child offset is required.");
                }
                else
                {
                    var children = ReadDictionary();
                    drawable = selectedOffset ?? children[0].DrawableOffset;
                    Require(children.Any(child => child.DrawableOffset == drawable));
                }
                Require(drawable >= 64 && drawable <= sys.Length - 192 && LooksLikeDrawableHeader(sys[drawable..], true));
            }
            else if (fragment)
            {
                Require(LooksLikeFragment(sys));
                parentName = ReadName(168);
                drawable = S(24, 176);
                Require(drawable != 0 && LooksLikeDrawableHeader(sys[drawable..], true));
            }
            if (!dictionary && selectedOffset is not null) Require(selectedOffset == drawable);
            ulong namePointer = BinaryPrimitives.ReadUInt64LittleEndian(sys.Slice(drawable + 168, 8));
            string name = namePointer == 0 && parentName is not null ? parentName : ReadName(drawable + 168);
            if (fragment && namePointer != 0)
                Require(name.EndsWith(".#ft", StringComparison.Ordinal) &&
                    string.Equals(name[..^4], parentName, StringComparison.Ordinal));
            Vector3 min = ReadVector(sys, drawable + 48), max = ReadVector(sys, drawable + 64);
            int lod = S(drawable + 80, 16), modelCount = U16(sys, lod + 8), capacity = U16(sys, lod + 10);
            Require(modelCount is > 0 and <= 256 && modelCount == capacity);
            int list = S(lod, checked(modelCount * 8));
            var meshes = new List<RageMesh>();
            var materials = ReadMaterials(drawable);
            var embedded = ReadEmbeddedTextures(drawable);
            var seenModels = new HashSet<int>();
            var seenGeometry = new HashSet<int>();
            for (int mi = 0; mi < modelCount; mi++)
            {
                int model = S(list + mi * 8, 56);
                Require(seenModels.Add(model));
                int count = U16(sys, model + 16);
                Require(count is > 0 and <= MaxMeshes && count == U16(sys, model + 18) && count == U16(sys, model + 54));
                int boneCount = U16(sys, model + 20);
                Require(boneCount <= 4096);
                hasBoneBindings |= boneCount > 0;
                var paletteHashes = boneCount > 0 ? RageSkeleton.ReadModelPalette(sys, model) : null;
                int geometries = S(model + 8, checked(count * 8));
                int? shaderMapping = ReadShaderMapping(model, count, materials?.Count);
                int bounds = S(model + 24, checked((count + (count > 1 ? 1 : 0)) * 32));
                for (int gi = 0; gi < count; gi++)
                {
                    Require(meshes.Count < MaxMeshes);
                    int geometry = S(geometries + gi * 8, 160);
                    Require(seenGeometry.Add(geometry));
                    int bound = bounds + (gi + (count > 1 ? 1 : 0)) * 32;
                    Vector3 partMin = ReadVector(sys, bound), partMax = ReadVector(sys, bound + 16);
                    Require(ValidBounds(partMin, partMax));
                    var mesh = ReadMesh(geometry, $"model_{mi}_geometry_{gi}", partMin, partMax, boneCount);
                    if (paletteHashes is not null) mesh = mesh with { BonePaletteHashes = paletteHashes };
                    if (shaderMapping is int mapping)
                        mesh = mesh with { ShaderIndex = U16(sys, mapping + gi * 2) };
                    meshes.Add(mesh);
                }
            }
            Require(meshes.Count > 0);
            Vector3 previewMin = min, previewMax = max;
            if (dictionary || hasBoneBindings || min == max)
            {
                previewMin = new(float.PositiveInfinity); previewMax = new(float.NegativeInfinity);
                foreach (var mesh in meshes)
                    foreach (int index in mesh.Indices)
                    { previewMin = Vector3.Min(previewMin, mesh.Positions[index]); previewMax = Vector3.Max(previewMax, mesh.Positions[index]); }
                Require(ValidBounds(previewMin, previewMax));
            }
            return new(name, meshes, previewMin, previewMax)
            {
                IsFragment = fragment, HasBoneBindings = hasBoneBindings, DrawableOffset = drawable,
                IsDictionaryChild = dictionary, DeclaredBoundsMin = min, DeclaredBoundsMax = max,
                Materials = materials,
                EmbeddedTextures = embedded.Textures,
                EmbeddedTextureDictionaryOffset = embedded.Offset,
                PresentationNote = dictionary
                    ? "Native drawable dictionary child in raw bind space; skeleton, face morphs, animation and full character assembly are not applied."
                    : fragment
                    ? "Raw bind-pose geometry; bone transforms, physics and fragment assembly are not applied."
                    : hasBoneBindings ? "Raw bind-pose geometry; bone transforms are not applied." : null
            };
        }

        private string ReadName(int pointerAt)
        {
            int at = S(pointerAt), available = Math.Min(256, sys.Length - at);
            int terminator = sys.Slice(at, available).IndexOf((byte)0);
            Require(terminator is > 0 and < 256);
            string name = new UTF8Encoding(false, true).GetString(sys.Slice(at, terminator));
            foreach (char c in name) Require(!char.IsControl(c));
            return name;
        }

        private int? ReadShaderMapping(int model, int count, int? materialCount)
        {
            if (materialCount is null) return null;
            try
            {
                int mapping = S(model + 32, checked(count * 2));
                for (int i = 0; i < count; i++) Require(U16(sys, mapping + i * 2) < materialCount);
                return mapping;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) { return null; }
        }

        private IReadOnlyList<RageMaterial>? ReadMaterials(int drawable)
        {
            try
            {
                int group = S(drawable + 16, 64), count = U16(sys, group + 24);
                Require(count is > 0 and <= 512 && count == U16(sys, group + 26));
                int pointers = S(group + 16, count * 8); var result = new List<RageMaterial>(count);
                for (int i = 0; i < count; i++)
                {
                    int shader = S(pointers + i * 8, 64);
                    uint hash = U32(sys, shader), preset = U32(sys, shader + 4);


                    Require(preset == 0x6D657461);
                    int info = S(shader + 32, 8), textures = sys[info + 1], parameters = sys[info + 4];
                    Require(sys[info + 5] == 0 && sys[info + 6] == 0 && sys[info + 7] is > 0 and <= 16);
                    Require(info <= sys.Length - checked(8 + parameters * 8));
                    var refs = new List<RageTextureReference>();
                    int textureArray = textures > 0 ? S(shader + 16, textures * 8) : 0;
                    var seen = new HashSet<int>();
                    for (int p = 0; p < parameters; p++)
                    {
                        int at = info + 8 + p * 8; uint data = U32(sys, at + 4);
                        if ((data & 3) != 0) continue;
                        int index = (int)((data >> 2) & 255);
                        Require(index < textures && seen.Add(index));
                        if (BinaryPrimitives.ReadUInt64LittleEndian(sys[(textureArray + index * 8)..]) == 0) continue;
                        int texture = S(textureArray + index * 8, 80);



                        if (U32(sys, texture + 16) != 0x980 || U32(sys, texture + 20) != 0 ||
                            BinaryPrimitives.ReadUInt64LittleEndian(sys[(texture + 24)..]) != 0 ||
                            BinaryPrimitives.ReadUInt64LittleEndian(sys[(texture + 40)..]) != 0 ||
                            U32(sys, texture + 56) != 0 || U16(sys, texture + 60) != 1 ||
                            U16(sys, texture + 62) != 1 || sys[texture + 64] != 255) continue;
                        if (BinaryPrimitives.ReadUInt64LittleEndian(sys[(texture + 48)..]) == 0) continue;
                        int asset = S(texture + 48, 16);
                        if (asset < 16 || U32(sys, asset - 16) != 6 || U32(sys, asset - 12) != 0 || U32(sys, asset - 4) != 0 ||
                            BinaryPrimitives.ReadUInt64LittleEndian(sys[asset..]) != 0x50000000UL + (ulong)asset - 11 ||
                            BinaryPrimitives.ReadUInt64LittleEndian(sys[(asset + 8)..]) == 0) continue;
                        refs.Add(new(U32(sys, at), index, texture, asset,
                            BinaryPrimitives.ReadUInt64LittleEndian(sys[(asset + 8)..]), U32(sys, asset - 8), 6));
                    }
                    result.Add(new(i, hash, preset, refs)
                    {
                        RenderBucket = sys[shader + 57], RenderBucketMask = U32(sys, shader + 60)
                    });
                }
                return result;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) { return null; }
        }

        private (IReadOnlyList<RageTextureInfo>? Textures, int? Offset) ReadEmbeddedTextures(int drawable)
        {
            try
            {
                int group = S(drawable + 16, 64);
                if (BinaryPrimitives.ReadUInt64LittleEndian(sys[(group + 8)..]) == 0) return (null, null);
                int dictionary = S(group + 8, 64);
                var textures = RageTextureDecoder.InspectEmbeddedDictionary(sys, gfx.Length, dictionary);
                return textures == null ? (null, null) : (textures.Textures, dictionary);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) { return (null, null); }
        }

        private IReadOnlyList<uint>? ReadVertexColors(int fvf, int vertexCount, ReadOnlySpan<byte> vertex)
        {
            const int semantic = 24;
            if (sys[fvf + 0xD0 + semantic] != 4 || sys[fvf + 0x104 + semantic] != 28 || vertex.Length < 8) return null;
            uint offset = U32(sys, fvf + semantic * 4), native = U32(vertex, 4);
            if (offset > vertex.Length || (long)vertexCount * 4 > vertex.Length - offset ||
                ((native >> 7) & 0x1FFFFFC) != offset || ((native >> 3) & 60) != 4 || (native & 15) != 6) return null;
            var colors = new uint[vertexCount];
            for (int i = 0; i < vertexCount; i++) colors[i] = U32(vertex, checked((int)offset + i * 4));
            return colors;
        }

        private IReadOnlyList<Vector2>? ReadTexCoords(int fvf, int vertexCount, ReadOnlySpan<byte> vertex)
        {
            const int semantic = 28;
            int size = sys[fvf + 0xD0 + semantic], format = sys[fvf + 0x104 + semantic];
            bool floating = size == 8 && format == 16, normalized = size == 4 && format == 38;
            if (!floating && !normalized) return null;
            uint offset = U32(sys, fvf + semantic * 4);
            if (offset > vertex.Length || (long)vertexCount * size > vertex.Length - offset || vertex.Length < 16) return null;



            uint native = U32(vertex, 12);
            if (((native >> 7) & 0x1FFFFFC) != offset || ((native >> 3) & 60) != size ||
                (native & 15) != (floating ? 2U : 8U)) return null;
            float scale = 1;
            if (normalized)
            {
                if (vertex.Length < 128) return null;
                scale = BinaryPrimitives.ReadSingleLittleEndian(vertex[124..]);
                if (!float.IsFinite(scale) || scale <= 0 || scale > 1_000_000) return null;
            }
            var result = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int at = checked((int)offset + i * size);



                float u = floating ? BinaryPrimitives.ReadSingleLittleEndian(vertex[at..]) :
                    Math.Max(BinaryPrimitives.ReadInt16LittleEndian(vertex[at..]) / 32767f, -1f) * scale;
                float v = floating ? BinaryPrimitives.ReadSingleLittleEndian(vertex[(at + 4)..]) :
                    Math.Max(BinaryPrimitives.ReadInt16LittleEndian(vertex[(at + 2)..]) / 32767f, -1f) * scale;
                if (!float.IsFinite(u) || !float.IsFinite(v) || Math.Abs(u) > 1_000_000 || Math.Abs(v) > 1_000_000) return null;
                result[i] = new(u, v);
            }
            return result;
        }

        private RageMesh ReadMesh(int geometry, string name, Vector3 boundMin, Vector3 boundMax, int boneCount)
        {
            int vb = S(geometry + 8, 80), fvf = S(vb + 64, 320);
            uint flags = U32(sys, fvf + 0x138);
            int vertexCount = checked((int)(flags >> 10));
            Require((flags & 1) != 0 && vertexCount is > 0 and <= 1_000_000);
            totalVertices = checked(totalVertices + vertexCount); Require(totalVertices <= MaxTotalVertices);
            int positionOffset = checked((int)U32(sys, fvf));
            int positionSize = sys[fvf + 0xD0], format = sys[fvf + 0x104];
            int bufferLength = checked((int)U32(sys, vb + 8) * U16(sys, vb + 12));
            Require(bufferLength > 0 && positionOffset >= 96 && positionOffset <= bufferLength &&
                    (long)positionSize * vertexCount <= bufferLength - positionOffset);
            int data = G(vb + 24, bufferLength);
            ReadOnlySpan<byte> vertex = gfx.Slice(data, bufferLength);
            var positions = new Vector3[vertexCount];
            (Vector3 Min, Vector3 Max)? quantizedBounds = null;
            if (positionSize == 12 && format == 6)
            {
                Require((U32(vertex, 0) & 127) == 0x63);
                for (int i = 0; i < vertexCount; i++) positions[i] = ReadVector(vertex, positionOffset + i * 12);
            }
            else if (positionSize == 8 && format == 16)
            {
                Require(positionOffset >= 128 && (U32(vertex, 0) & 127) == 0x42);
                Vector3 bias = ReadVector(vertex, 96), scale = ReadVector(vertex, 112);
                uint axis = U32(vertex, 108);
                Require(axis <= 2 && Finite(bias) && Finite(scale) && scale.X >= 0 && scale.Y >= 0 && scale.Z >= 0);
                var end = bias + scale * new Vector3(16777215, 16777215, 65535);
                quantizedBounds = (ReorderPosition(bias, axis), ReorderPosition(end, axis));
                for (int i = 0; i < vertexCount; i++)
                {
                    ulong q = BinaryPrimitives.ReadUInt64LittleEndian(vertex.Slice(positionOffset + i * 8, 8));



                    var p = new Vector3((float)(bias.X + (double)scale.X * ((q >> 8) & 0xFFFFFF)),
                        (float)(bias.Y + (double)scale.Y * ((((q >> 32) & 0xFFFF) << 8) | (q & 0xFF))),
                        (float)(bias.Z + (double)scale.Z * (q >> 48)));
                    positions[i] = ReorderPosition(p, axis);
                }
            }
            else throw new InvalidDataException("Unsupported position encoding.");
            foreach (Vector3 p in positions) Require(Finite(p));

            int descriptor = S(geometry + 64, 16), ib = S(descriptor, 64);
            int indexLength = checked((int)U32(sys, ib + 8) * U16(sys, ib + 12));
            Require(U16(sys, ib + 12) == 4 && indexLength >= 32);
            int indexAt = G(ib + 24, indexLength);
            ReadOnlySpan<byte> index = gfx.Slice(indexAt, indexLength);
            uint countFlags = U32(index, 0);
            bool bonePalette = (countFlags & 0x40000000) != 0;


            int clusterCount = checked((int)(countFlags & (bonePalette ? 0xFFFFU : 0x7FFFFFFFU)));
            int triangles = checked((int)U32(index, 4)), refs = checked((int)U32(index, 8));
            uint binding = U32(sys, descriptor + 12);
            bool paletteWithoutMarker = (binding & 255) == 0 && (countFlags & 0xFFFF0000) == 0xC0000000U;
            Require(clusterCount is > 0 and <= 2048 && triangles is > 0 and <= 65535 && refs is > 0 and <= 65535 &&
                clusterCount == U16(sys, descriptor + 8) && triangles == U16(sys, descriptor + 10) &&
                refs == ((binding >> 8) & 0x7FFFFF) && ((binding & 0x80000000) != 0) == bonePalette &&
                (!bonePalette || (binding & 255) == 0x80U || paletteWithoutMarker));
            hasBoneBindings |= bonePalette;
            totalTriangles = checked(totalTriangles + triangles); Require(totalTriangles <= MaxTotalTriangles);
            int refSize = (countFlags & 0x80000000) != 0 ? 2 : 4;
            int triangleAt = checked(12 + clusterCount * 16), refsAt = checked(triangleAt + triangles * 4);
            Require(refsAt <= indexLength && (long)refs * refSize <= indexLength - refsAt);
            int paletteAt = checked(refsAt + refs * refSize);
            int meaningfulEnd = checked(paletteAt + (bonePalette ? clusterCount * 2 : 0));
            Require(meaningfulEnd <= indexLength);
            if (bonePalette)
            {
                uint hint = (countFlags >> 16) & 0x1FFF;
                bool constantBinding = (countFlags & 0x20000000) != 0;


                const int BoneIndexSlot = 20, R16Uint = 57;
                bool wideIndices = sys[fvf + 0xD0 + BoneIndexSlot] == 2 && sys[fvf + 0x104 + BoneIndexSlot] == R16Uint;
                Require(constantBinding ? (hint & 1) == 0 && (boneCount == 0 || hint / 2 < boneCount) :
                    hint == 0 || (hint == 2 && wideIndices));
                for (int i = 0; i < clusterCount; i++)
                {
                    ushort bone = U16(index, paletteAt + i * 2);
                    Require(bone == ushort.MaxValue || boneCount == 0 || bone < boneCount);
                    if (constantBinding) Require(bone == hint / 2);
                }
            }
            var references = new int[refs];
            for (int i = 0; i < refs; i++)
            {
                references[i] = refSize == 2 ? U16(index, refsAt + i * 2) : checked((int)U32(index, refsAt + i * 4));
                Require(references[i] < vertexCount);
            }
            var indices = new int[checked(triangles * 3)];
            int accumulatedRefs = 0, accumulatedTriangles = 0;
            Vector3 actualMin = new(float.PositiveInfinity), actualMax = new(float.NegativeInfinity);
            for (int cluster = 0; cluster < clusterCount; cluster++)
            {
                int at = 12 + cluster * 16;
                uint packed = U32(index, at), offsets = U32(index, at + 4);
                int vertices = (int)(packed & 127) + 1, count = (int)((packed >> 7) & 255) + 1;
                int vertexStart = (int)(offsets & 65535), triangleStart = (int)(offsets >> 16);
                Require(vertexStart == accumulatedRefs && triangleStart == accumulatedTriangles &&
                    vertices <= refs - vertexStart && count <= triangles - triangleStart);
                for (int i = triangleStart; i < triangleStart + count; i++)
                {
                    uint word = U32(index, triangleAt + i * 4);
                    Require(word >> 21 == cluster);
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int local = (int)((word >> (corner * 7)) & 127); Require(local < vertices);
                        int vertexIndex = references[vertexStart + local]; indices[i * 3 + corner] = vertexIndex;
                        actualMin = Vector3.Min(actualMin, positions[vertexIndex]); actualMax = Vector3.Max(actualMax, positions[vertexIndex]);
                    }
                }
                accumulatedRefs += vertices; accumulatedTriangles += count;
            }
            Require(accumulatedRefs == refs && accumulatedTriangles == triangles);
            float tolerance = Tolerance(boundMin, boundMax);
            float fitTolerance = quantizedBounds.HasValue ? Math.Max(tolerance, 0.01001f) : tolerance;
            bool tightBounds = Close(actualMin, boundMin, fitTolerance) && Close(actualMax, boundMax, fitTolerance);
            bool conservativeBounds = quantizedBounds is { } range && Close(range.Min, boundMin, tolerance) &&
                Close(range.Max, boundMax, tolerance) && Contains(boundMin, boundMax, actualMin, tolerance) &&
                Contains(boundMin, boundMax, actualMax, tolerance);
            Require(tightBounds || conservativeBounds);
            return new(name, positions, indices) { TexCoords = ReadTexCoords(fvf, vertexCount, vertex), VertexColors = ReadVertexColors(fvf, vertexCount, vertex) };
        }

        private static Vector3 ReorderPosition(Vector3 position, uint axis) =>
            axis switch { 0 => new(position.Z, position.Y, position.X), 1 => new(position.Y, position.Z, position.X), _ => position };

        private static float Tolerance(Vector3 min, Vector3 max) => 0.002f + Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)) * 0.00001f;
        private static bool Close(Vector3 a, Vector3 b, float tolerance)
        {
            Vector3 delta = Vector3.Abs(a - b);
            return delta.X <= tolerance && delta.Y <= tolerance && delta.Z <= tolerance;
        }
        private static bool Contains(Vector3 min, Vector3 max, Vector3 p, float tolerance) =>
            p.X >= min.X - tolerance && p.X <= max.X + tolerance && p.Y >= min.Y - tolerance && p.Y <= max.Y + tolerance &&
            p.Z >= min.Z - tolerance && p.Z <= max.Z + tolerance;
    }
}
