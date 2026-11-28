using System.Globalization;

namespace RpfPreview.Core;

public sealed class WorkspaceAsset(ArchiveDocument Document, ArchiveEntry Entry,
    RageTextureInfo? Texture = null, TtpfTextureInfo? PoolTexture = null)
{
    public ArchiveDocument Document { get; } = Document;
    public ArchiveEntry Entry { get; } = Entry;
    public RageTextureInfo? Texture { get; } = Texture;
    public TtpfTextureInfo? PoolTexture { get; } = PoolTexture;
    public string SourcePath => Document.SourcePath;
    public string SourceName => Path.GetFileName(SourcePath);
    public string Name => Texture?.Name ?? Entry.Name;
    public bool IsTextureChild => Texture != null;
    public bool IsImage => Texture != null || PoolTexture != null || Entry.Extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".dds";
    public string Kind => IsImage ? "image" : Entry.Compression switch { "Recovered model" => "model", "Recovered AWC" => "audio", "Recovered raw deflate shader" => "shader", "Recovered texture dictionary" => "dictionary", _ => "file" };
    public long Size => Texture is { } t ? RageTextureDecoder.DdsFileSize(t) : Entry.Size;
    public int Width => Texture?.Width ?? PoolTexture?.Width ?? 0;
    public int Height => Texture?.Height ?? PoolTexture?.Height ?? 0;
    public string Format => Texture?.Format ?? PoolTexture?.Format ?? Entry.Extension;
    public int Mips => Texture?.MipCount ?? PoolTexture?.SourceMips ?? 0;
    public int SubresourceIndex => Texture?.Index ?? PoolTexture?.Index ?? -1;
    public bool Available => Entry.CanExtract;
    public string LogicalPath => Texture == null ? Entry.Path : Entry.Path + "/" + Texture.Name;
    public ulong? AssetKey => PoolTexture?.AssetKey ?? Texture?.AssetKey;
    public string KeyText => AssetKey?.ToString("X16", CultureInfo.InvariantCulture) ?? Texture?.NameHash.ToString("X8", CultureInfo.InvariantCulture) ?? "";
    public string ComponentHint => Kind != "model" ? "" : Name.StartsWith("hand_", StringComparison.OrdinalIgnoreCase)
        ? "upper body skin arms hands" : Name.StartsWith("feet_", StringComparison.OrdinalIgnoreCase)
        ? "lower body skin legs feet" : "";
    private string? _search;
    public string SearchText => _search ??= string.Join(' ', Name, LogicalPath, SourceName, Kind, Format, Width.ToString(CultureInfo.InvariantCulture), Height.ToString(CultureInfo.InvariantCulture), KeyText, ComponentHint);
}

public sealed record WorkspaceIndexProgress(int Completed, int Total, string Source);

public sealed class AssetWorkspaceIndex
{
    private readonly Dictionary<(string Source, string Entry), IReadOnlyList<WorkspaceAsset>> _children;
    private AssetWorkspaceIndex(IReadOnlyList<WorkspaceAsset> assets, Dictionary<(string, string), IReadOnlyList<WorkspaceAsset>> children,
        IReadOnlyList<string> warnings)
    { Assets = assets; _children = children; Warnings = warnings; }
    public IReadOnlyList<WorkspaceAsset> Assets { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<WorkspaceAsset> Children(ArchiveDocument document, ArchiveEntry entry)
        => _children.TryGetValue((document.SourcePath, entry.Path), out var children) ? children : Array.Empty<WorkspaceAsset>();
    public bool HasChildren(WorkspaceAsset asset) => _children.ContainsKey((asset.SourcePath, asset.Entry.Path)) && !asset.IsTextureChild;

    public static AssetWorkspaceIndex Build(IEnumerable<ArchiveDocument> documents, IProgress<WorkspaceIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sources = documents.ToArray(); var assets = new List<WorkspaceAsset>();
        var children = new Dictionary<(string, string), IReadOnlyList<WorkspaceAsset>>(); var warnings = new List<string>();
        int total = sources.Sum(d => d.Entries.Count), completed = 0;
        foreach (var document in sources)
        foreach (var entry in document.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested(); completed++;
            if (entry.IsDirectory) continue;
            var pool = document.TexturePool?.Textures.ElementAtOrDefault(entry.Index);
            assets.Add(new(document, entry, PoolTexture: pool));
            if (entry.Compression == "Recovered texture dictionary")
            {
                try
                {
                    int split = entry.ResourceSystemBytes ?? checked((int)entry.Segments![0].Size);
                    byte[] system = document.ReadPreview(entry, split, cancellationToken);
                    var dictionary = RageTextureDecoder.InspectMetadata(system, checked((int)(entry.Size - split)))
                        ?? throw new InvalidDataException("Texture metadata is not a supported dictionary.");
                    var list = dictionary.Textures.Select(t => new WorkspaceAsset(document, entry, t)).ToArray();
                    children[(document.SourcePath, entry.Path)] = list; assets.AddRange(list);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
                { warnings.Add($"{document.SourcePath} · {entry.Path}: {ex.Message}"); }
            }
            if (completed % 250 == 0 || entry.Compression == "Recovered texture dictionary") progress?.Report(new(completed, total, document.SourcePath));
        }
        progress?.Report(new(total, total, ""));
        return new(assets, children, warnings);
    }
}
