using RpfPreview.Core;
using System.IO;

namespace RpfPreview.App;

public partial class MainWindow
{
    private readonly Dictionary<uint, List<ResidentTextureLocation>> _loadedResidentNames = new();
    private Dictionary<uint, List<ResidentTextureLocation>> _defaultResidentNames = new();
    private Dictionary<string, (long Length, long Write)> _defaultResidentSources = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultResidentRoot;

    private async Task<ResolvedModelImage?> FindResidentDefaultAsync(ArchiveDocument modelDocument, ulong key, uint nameHash, CancellationToken token)
    {
        var profile = ResidentTextureDefaults.Find(key, nameHash);
        if (profile == null) return null;
        var candidates = _loadedResidentNames.TryGetValue(nameHash, out var loaded) ? loaded.ToList() : [];
        string root = StreamingTextureCatalog.FindContentDirectory(modelDocument.SourcePath) ?? Path.GetDirectoryName(modelDocument.SourcePath)!;
        string[] paths = new[] { Path.Combine(Path.GetDirectoryName(modelDocument.SourcePath)!, "textures.rpf"), Path.Combine(root, "textures.rpf"),
            Path.Combine(root, "pack", "core_restofworld", "core_restofworld.rpf") }.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool current = _defaultResidentRoot == root && paths.Length == _defaultResidentSources.Count && paths.All(p =>
            _defaultResidentSources.TryGetValue(p, out var stamp) && stamp.Length == new FileInfo(p).Length && stamp.Write == File.GetLastWriteTimeUtc(p).Ticks);
        if (!current)
        {
            var found = await Task.Run(() => {
                var names = new Dictionary<uint, List<ResidentTextureLocation>>();
                var sources = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    token.ThrowIfCancellationRequested(); var source = new FileInfo(path);
                    sources[path] = (source.Length, source.LastWriteTimeUtc.Ticks);
                    try
                    {
                        var document = ArchiveReader.Open(path); var index = AssetWorkspaceIndex.Build(new[] { document }, cancellationToken: token);
                        foreach (var asset in index.Assets.Where(a => a.Texture != null && ResidentTextureDefaults.Profiles.Any(p => ResidentTextureDefaults.MatchesMetadata(p, a.Texture))))
                        {
                            uint hash = asset.Texture!.NameHash;
                            if (!names.TryGetValue(hash, out var list)) names[hash] = list = [];
                            list.Add(new(document, asset.Entry, asset.Texture));
                        }
                    }
                    catch (Exception ex) when (IsTextureReadFailure(ex)) { }
                }
                return (Names: names, Sources: sources);
            }, token);
            token.ThrowIfCancellationRequested(); _defaultResidentNames = found.Names; _defaultResidentSources = found.Sources; _defaultResidentRoot = root;
        }
        if (_defaultResidentNames.TryGetValue(nameHash, out var fallback)) candidates.AddRange(fallback);
        foreach (var location in candidates.DistinctBy(c => (c.Document.SourcePath, c.Entry.Offset, c.Texture.Index)))
        {
            if (!ResidentTextureDefaults.MatchesMetadata(profile, location.Texture)) continue;
            try
            {
                string cacheKey = "resident-default:" + profile.AssetKey + ":" + profile.PixelSha256;
                if (!_modelImageCache.TryGetValue(cacheKey, out var image))
                {
                    var pixels = await Task.Run(() => {
                        byte[] resource = ArchiveReader.ReadResourceData(location.Document, location.Entry, token);
                        int split = location.Entry.ResourceSystemBytes ?? checked((int)location.Entry.Segments![0].Size);
                        return RageTextureDecoder.DecodeTexturePixels(resource.AsSpan(0, split), resource.AsSpan(split), location.Texture.Index);
                    }, token);
                    if (!ResidentTextureDefaults.MatchesPixels(profile, pixels)) continue;
                    image = PixelBitmap(pixels); RememberModelImage(cacheKey, image);
                }
                return new(image, $"resident name {profile.Name} in {Path.GetFileName(location.Document.SourcePath)} | validated key/name-hash pair and pixel fingerprint");
            }
            catch (Exception ex) when (IsTextureReadFailure(ex)) { }
        }
        return null;
    }
}
