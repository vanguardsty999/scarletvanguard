namespace RpfPreview.Core;

public sealed record StreamingTextureLocation(string SourcePath, TtpfTextureInfo Texture)
{
    public long SourceLength { get; init; }
    public long SourceWriteTicks { get; init; }
}


public sealed class StreamingTextureCatalog
{
    private sealed record SourceStamp(string Path, long Length, long WriteTicks);
    private readonly IReadOnlyDictionary<ulong, IReadOnlyList<StreamingTextureLocation>> _locations;
    private readonly IReadOnlyList<SourceStamp> _sources;
    private StreamingTextureCatalog(Dictionary<ulong, IReadOnlyList<StreamingTextureLocation>> locations,
        IReadOnlyList<SourceStamp> sources, int inspected, int skipped)
    { _locations = locations; _sources = sources; InspectedPools = inspected; SkippedPools = skipped; }

    public int InspectedPools { get; }
    public int SkippedPools { get; }
    public int SupportedKeys => _locations.Count(p => p.Value.Any(l => l.Texture.CanPreview));
    public int IndexedKeys => _locations.Count;
    public bool IsCurrent => _sources.All(s => File.Exists(s.Path) && new FileInfo(s.Path).Length == s.Length && File.GetLastWriteTimeUtc(s.Path).Ticks == s.WriteTicks);
    public IReadOnlyList<StreamingTextureLocation> Find(ulong assetKey)
        => _locations.TryGetValue(assetKey, out var locations) ? locations : Array.Empty<StreamingTextureLocation>();

    public static string? FindContentDirectory(string archivePath)
    {
        var parent = new FileInfo(Path.GetFullPath(archivePath)).Directory;
        string? contentRoot = null;
        for (int i = 0; parent != null && i < 6; i++, parent = parent.Parent)
        {
            string candidate = Path.Combine(parent.FullName, "data", "ttdb");
            if (Directory.Exists(candidate)) contentRoot = parent.FullName;
        }

        return contentRoot;
    }

    public static StreamingTextureCatalog Build(string poolDirectory, CancellationToken token = default)
    {
        var files = Directory.EnumerateFiles(poolDirectory, "*.ttpf", new EnumerationOptions {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(257).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 256) throw new NotSupportedException("This texture catalog exceeds the 256-pool preview limit.");
        var locations = new Dictionary<ulong, List<StreamingTextureLocation>>();
        var sources = new List<SourceStamp>(); int inspected = 0, skipped = 0;
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(path); sources.Add(new(path, file.Length, file.LastWriteTimeUtc.Ticks));
            try
            {
                var pool = TtpfReader.Inspect(path, token); inspected++;
                foreach (var texture in pool.Textures.Where(t => t.AssetKey != 0))
                {
                    if (!locations.TryGetValue(texture.AssetKey, out var list)) locations[texture.AssetKey] = list = [];
                    list.Add(new(path, texture) { SourceLength = file.Length, SourceWriteTicks = file.LastWriteTimeUtc.Ticks });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
            { skipped++; }
        }
        return new(locations.ToDictionary(p => p.Key,
            p => (IReadOnlyList<StreamingTextureLocation>)p.Value.OrderByDescending(x => x.Texture.CanPreview)
                .ThenBy(x => x.Texture.BaseMipLevel).ThenByDescending(x => x.Texture.Mips).ToArray()), sources, inspected, skipped);
    }
}
