using RpfPreview.Core;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RpfPreview.App;

public partial class MainWindow
{
    private StreamingTextureCatalog? _streamingCatalog;
    private string? _streamingPoolDirectory;
    private readonly Dictionary<ulong, List<StreamingTextureLocation>> _loadedTextureLocations = new();
    private readonly Dictionary<ulong, List<ResidentTextureLocation>> _loadedResidentTextures = new();
    private readonly Dictionary<string, ImageSource> _modelImageCache = new();
    private long _modelImageCacheBytes;
    private string _modelTextureDetails = "";
    private int _modelLayerPreviewMaterials, _modelMissingTextureMaterials, _modelOpaquePreviewMaterials;
    private sealed record ResidentTextureLocation(ArchiveDocument Document, ArchiveEntry Entry, RageTextureInfo Texture);
    private sealed record ResolvedModelImage(ImageSource? Image, string Detail);

    private void BuildLoadedTextureLookup()
    {
        _loadedTextureLocations.Clear(); _loadedResidentTextures.Clear(); _loadedResidentNames.Clear();
        foreach (var source in _documents.Values)
        {
            if (source.TexturePool == null) continue;
            long write = File.GetLastWriteTimeUtc(source.SourcePath).Ticks;
            foreach (var texture in source.TexturePool.Textures.Where(t => t.AssetKey != 0))
            {
                if (!_loadedTextureLocations.TryGetValue(texture.AssetKey, out var locations)) _loadedTextureLocations[texture.AssetKey] = locations = [];
                locations.Add(new(source.SourcePath, texture) { SourceLength = source.FileSize, SourceWriteTicks = write });
            }
        }
        if (_assetIndex != null)
        {
            foreach (var asset in _assetIndex.Assets.Where(a => a.Texture?.AssetKey is > 0))
            {
                ulong key = asset.Texture!.AssetKey!.Value;
                if (!_loadedResidentTextures.TryGetValue(key, out var locations)) _loadedResidentTextures[key] = locations = [];
                locations.Add(new(asset.Document, asset.Entry, asset.Texture));
            }
            foreach (var asset in _assetIndex.Assets.Where(a => a.Texture?.NameHash is > 0))
            {
                uint hash = asset.Texture!.NameHash;
                if (!_loadedResidentNames.TryGetValue(hash, out var locations)) _loadedResidentNames[hash] = locations = [];
                locations.Add(new(asset.Document, asset.Entry, asset.Texture));
            }
        }
    }

    private async Task<IReadOnlyDictionary<int, ImageSource>> ResolveModelTexturesAsync(ArchiveDocument document, ArchiveEntry modelEntry,
        RageModel model, ReadOnlyMemory<byte> system, ReadOnlyMemory<byte> graphics, CancellationToken token)
    {
        var result = new Dictionary<int, ImageSource>(); var notes = new List<string>();
        _modelTextureDetails = ""; _modelLayerPreviewMaterials = _modelMissingTextureMaterials = _modelOpaquePreviewMaterials = 0;
        if (model.Materials == null) { _modelTextureDetails = "No supported native material table was decoded."; return result; }
        var used = model.Meshes.Where(m => m.ShaderIndex != null).Select(m => m.ShaderIndex!.Value).ToHashSet();
        int materialCount = Math.Max(1, used.Count), maximumDimension = materialCount > 128 ? 256 : materialCount > 32 ? 512 : 1024;
        var resolved = new Dictionary<ulong, ResolvedModelImage>();
        long modelSourceStamp = File.GetLastWriteTimeUtc(document.SourcePath).Ticks;
        var previewImages = new HashSet<ImageSource>(ReferenceEqualityComparer.Instance);
        var opaqueImages = new Dictionary<ImageSource, ImageSource>(ReferenceEqualityComparer.Instance);
        long previewBytes = 0; bool catalogChecked = false;
        string? residentResourceKey = null; byte[]? residentResource = null; int residentSplit = 0;
        foreach (var material in model.Materials.Where(m => used.Contains(m.ShaderIndex)))
        {
            token.ThrowIfCancellationRequested();
            if (!model.Meshes.Any(m => m.ShaderIndex == material.ShaderIndex && m.TexCoords != null))
            {
                if (model.Meshes.Any(m => m.ShaderIndex == material.ShaderIndex && m.VertexColors != null))
                    notes.Add($"Material {material.ShaderIndex}: native vertex-color data; texture coordinates are absent and palette/tint effects are not simulated.");
                else { notes.Add($"Material {material.ShaderIndex}: no validated texture coordinates or vertex colors."); _modelMissingTextureMaterials++; }
                continue;
            }
            var colorTextures = ModelTextureBindings.ColorTextures(material);
            if (colorTextures.Count == 0)
            {
                notes.Add($"Material {material.ShaderIndex}: no recognized color sampler; hashes {string.Join(", ", material.Textures.Select(t => t.ParameterHash.ToString("X8")))}.");
                _modelMissingTextureMaterials++; continue;
            }
            var unavailable = new List<string>();
            foreach (var candidate in colorTextures)
            {
                token.ThrowIfCancellationRequested(); var reference = candidate.Reference;
                if (!resolved.TryGetValue(reference.OpaqueId64, out var found))
                {
                    found = await ResolveImageAsync(reference.OpaqueId64, reference.NameHash); token.ThrowIfCancellationRequested();
                    resolved[reference.OpaqueId64] = found;
                }
                if (found.Image == null) { unavailable.Add(candidate.ParameterName + ": " + found.Detail); continue; }
                ImageSource shown = found.Image;
                if (IsOpaqueMaterial(material) && found.Image is BitmapSource color)
                {
                    if (!opaqueImages.TryGetValue(found.Image, out shown!)) opaqueImages[found.Image] = shown = OpaqueModelImage(color);
                    _modelOpaquePreviewMaterials++;
                }
                if (previewImages.Add(shown) && shown is BitmapSource bitmap) previewBytes += (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
                if (previewBytes > 128L * 1024 * 1024) { unavailable.Add("Preview image budget exceeded."); break; }
                result[material.ShaderIndex] = shown;
                string mode = candidate.Layered ? " | base-layer preview; layer blending is not applied" : candidate.Secondary ? " | secondary color preview" : "";
                if (candidate.NameInferred) mode += " | parameter name inferred from matching hash";
                if (IsOpaqueMaterial(material)) mode += " | native opaque bucket";
                if (candidate.Layered || candidate.Secondary) _modelLayerPreviewMaterials++;
                notes.Add($"Material {material.ShaderIndex}: {candidate.ParameterName} {reference.OpaqueId64:X16} -> {found.Detail}{mode}");
                break;
            }
            if (!result.ContainsKey(material.ShaderIndex))
            { _modelMissingTextureMaterials++; notes.Add($"Material {material.ShaderIndex}: {string.Join("; ", unavailable.Distinct().Take(8))}"); }
        }
        _modelTextureDetails = string.Join(Environment.NewLine, notes); return result;

        async Task<ResolvedModelImage> ResolveImageAsync(ulong key, uint nameHash)
        {
            if (key == 0) return new(null, "No native texture key.");
            string keyText = key.ToString("X16"); var failures = new List<string>();
            var staged = _sourceProjects.Values.SelectMany(p => p.Entries.Where(r => r.Target.Kind == "texture"
                && string.Equals(r.Target.AssetKey, keyText, StringComparison.OrdinalIgnoreCase)).Select(r => (Project: p, Record: r))).FirstOrDefault();
            if (staged.Record != null)
            {
                try
                {
                    string cacheKey = "staged:" + staged.Record.Sha256 + ":" + maximumDimension;
                    if (!_modelImageCache.TryGetValue(cacheKey, out var image))
                    {
                        string path = staged.Project.AssetPath(staged.Record);
                        image = await Task.Run<ImageSource>(() => {
                            byte[] bytes = File.ReadAllBytes(path);
                            BitmapSource bitmap = staged.Record.Format == "DDS" ? PixelBitmap(DdsTextureReader.DecodeTexturePixels(bytes)) : DecodeImage(bytes);
                            return ModelSizedImage(bitmap, maximumDimension);
                        }, token);
                        RememberModelImage(cacheKey, image);
                    }
                    return new(image, "staged " + staged.Record.OriginalFileName);
                }
                catch (Exception ex) when (IsTextureReadFailure(ex)) { failures.Add("staged image: " + ex.Message); }
            }
            var embedded = model.EmbeddedTextures?.FirstOrDefault(t => t.AssetKey == key);
            if (embedded != null && model.EmbeddedTextureDictionaryOffset is int dictionaryOffset)
            {
                try
                {
                    string cacheKey = $"embedded:{document.SourcePath}:{document.FileSize}:{modelSourceStamp}:{modelEntry.Offset}:{embedded.Index}:{maximumDimension}";
                    if (!_modelImageCache.TryGetValue(cacheKey, out var image))
                    {
                        int mip = ModelMip(embedded, maximumDimension);
                        var pixels = await Task.Run(() => RageTextureDecoder.DecodeEmbeddedTexturePixels(system.Span, graphics.Span, dictionaryOffset, embedded.Index, mip), token);
                        image = ModelSizedImage(PixelBitmap(pixels), maximumDimension); RememberModelImage(cacheKey, image);
                    }
                    return new(image, $"embedded texture {embedded.Index}, {embedded.Width} x {embedded.Height}");
                }
                catch (Exception ex) when (IsTextureReadFailure(ex)) { failures.Add("embedded texture: " + ex.Message); }
            }
            if (_loadedResidentTextures.TryGetValue(key, out var residents))
            foreach (var location in residents)
            {
                try
                {
                    string cacheKey = $"resident:{location.Document.SourcePath}:{location.Document.FileSize}:{File.GetLastWriteTimeUtc(location.Document.SourcePath).Ticks}:{location.Entry.Offset}:{location.Texture.Index}:{maximumDimension}";
                    if (!_modelImageCache.TryGetValue(cacheKey, out var image))
                    {
                        string resourceKey = location.Document.SourcePath + ":" + location.Entry.Offset;
                        if (residentResourceKey != resourceKey)
                        {
                            residentResource = await Task.Run(() => ArchiveReader.ReadResourceData(location.Document, location.Entry, token), token);
                            residentSplit = location.Entry.ResourceSystemBytes ?? checked((int)location.Entry.Segments![0].Size); residentResourceKey = resourceKey;
                        }
                        int mip = ModelMip(location.Texture, maximumDimension); byte[] resource = residentResource!; int split = residentSplit;
                        var pixels = await Task.Run(() => RageTextureDecoder.DecodeTexturePixelsAtMip(resource.AsSpan(0, split), resource.AsSpan(split), location.Texture.Index, mip), token);
                        image = ModelSizedImage(PixelBitmap(pixels), maximumDimension); RememberModelImage(cacheKey, image);
                    }
                    return new(image, $"{Path.GetFileName(location.Document.SourcePath)} / resident texture {location.Texture.Index}");
                }
                catch (Exception ex) when (IsTextureReadFailure(ex)) { failures.Add("resident texture: " + ex.Message); }
            }
            var attempted = new HashSet<(string, int, long, long)>();
            if (_loadedTextureLocations.TryGetValue(key, out var loaded))
            foreach (var location in loaded.OrderByDescending(l => l.Texture.CanPreview).ThenBy(l => l.Texture.BaseMipLevel))
            {
                var image = await PoolImageAsync(location, attempted, failures); if (image != null) return image;
            }
            string? directory = StreamingTextureCatalog.FindContentDirectory(document.SourcePath);
            if (directory != null)
            {
                if (!catalogChecked)
                {
                    catalogChecked = true;
                    if (_streamingCatalog == null || _streamingPoolDirectory != directory || !_streamingCatalog.IsCurrent)
                    {
                        PreviewFootnoteText.Text = "Finding model textures in sibling texture pools…";
                        _streamingCatalog = await Task.Run(() => StreamingTextureCatalog.Build(directory, token), token); _streamingPoolDirectory = directory;
                    }
                }
                if (_streamingPoolDirectory == directory && _streamingCatalog != null)
                    foreach (var location in _streamingCatalog.Find(key))
                    { var image = await PoolImageAsync(location, attempted, failures); if (image != null) return image; }
            }
            var residentDefault = await FindResidentDefaultAsync(document, key, nameHash, token);
            if (residentDefault != null) return residentDefault;
            return new(null, failures.Count > 0 ? string.Join(" / ", failures.Distinct().Take(3)) : "native key not found in loaded or sibling texture pools");
        }

        async Task<ResolvedModelImage?> PoolImageAsync(StreamingTextureLocation location, HashSet<(string, int, long, long)> attempted, List<string> failures)
        {
            if (!attempted.Add((location.SourcePath, location.Texture.Index, location.SourceLength, location.SourceWriteTicks))) return null;
            if (!location.Texture.CanPreview) { failures.Add(location.Texture.Limitation ?? "No supported preview mip."); return null; }
            try
            {
                var file = new FileInfo(location.SourcePath);
                if (!file.Exists || file.Length != location.SourceLength || file.LastWriteTimeUtc.Ticks != location.SourceWriteTicks) { failures.Add("Texture pool changed."); return null; }
                string cacheKey = location.SourcePath + ":" + location.Texture.Index + ":" + location.SourceWriteTicks + ":" + maximumDimension;
                if (!_modelImageCache.TryGetValue(cacheKey, out var image))
                {
                    var pixels = await Task.Run(() => TtpfReader.DecodeTexturePreview(location.SourcePath, location.Texture.Index, maximumDimension, token), token);
                    image = ModelSizedImage(PixelBitmap(pixels), maximumDimension); RememberModelImage(cacheKey, image);
                }
                string detail = $"{Path.GetFileName(location.SourcePath)} / texture {location.Texture.Index}";
                if (location.Texture.BaseMipLevel > 0) detail += $" | lower mip {location.Texture.BaseMipLevel}, {location.Texture.ExportWidth} x {location.Texture.ExportHeight}; original {location.Texture.Width} x {location.Texture.Height}";
                return new(image, detail);
            }
            catch (Exception ex) when (IsTextureReadFailure(ex)) { failures.Add(ex.Message); return null; }
        }
    }

    private static bool IsTextureReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException;
    private static bool IsOpaqueMaterial(RageMaterial? material)
        => material?.RenderBucket is byte bucket && (bucket & 0x7F) == 0 && material.RenderBucketMask == 0xFF01;
    private static BitmapSource OpaqueModelImage(BitmapSource source)
    {
        BitmapSource straight = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(straight.PixelWidth * 4); byte[] pixels = new byte[checked(stride * straight.PixelHeight)]; straight.CopyPixels(pixels, stride, 0);
        bool changed = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 255) { pixels[i] = 255; changed = true; }
        if (!changed) return source;
        var image = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride); image.Freeze(); return image;
    }
    private static int ModelMip(RageTextureInfo texture, int maximumDimension)
    {
        int mip = 0; while (mip + 1 < texture.MipCount && (Math.Max(1, texture.Width >> mip) > maximumDimension || Math.Max(1, texture.Height >> mip) > maximumDimension)) mip++;
        return mip;
    }
    private static BitmapSource ModelSizedImage(BitmapSource bitmap, int maximumDimension = 1024)
    {
        if (bitmap.PixelWidth <= maximumDimension && bitmap.PixelHeight <= maximumDimension) return bitmap;
        double scale = Math.Min((double)maximumDimension / bitmap.PixelWidth, (double)maximumDimension / bitmap.PixelHeight);
        var sized = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)); sized.Freeze(); return sized;
    }
    private void RememberModelImage(string key, ImageSource image)
    {
        if (_modelImageCacheBytes > 128L * 1024 * 1024) { _modelImageCache.Clear(); _modelImageCacheBytes = 0; }
        _modelImageCache[key] = image;
        if (image is BitmapSource bitmap) _modelImageCacheBytes += (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
    }
}
