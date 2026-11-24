using System.Text.Json.Serialization;

namespace RpfPreview.Core;

public sealed record ArchiveEntry(string Path, long StoredSize, long Size, long Offset,
    bool IsDirectory, bool IsEncrypted, bool IsResource, string Compression, int Index)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/'));
    public string Folder => System.IO.Path.GetDirectoryName(Path.Replace('/', System.IO.Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public string Kind => IsDirectory ? "Folder" : IsEncrypted ? "Encrypted" : IsResource ? "RAGE resource" : Extension == ".bk2" ? "Bink2 video" : Extension == ".psin" ? "PSIN metadata" : Extension.TrimStart('.').ToUpperInvariant() is { Length: > 0 } x ? x + " file" : "Binary file";
    public string? Limitation { get; init; }
    public IReadOnlyList<ArchiveSegment>? Segments { get; init; }
    public int? ResourceSystemBytes { get; init; }
    public int? ResourceDrawableOffset { get; init; }
    public bool CanExtract => !IsDirectory && !IsEncrypted && Limitation is null;
}

public sealed record ExtractionProgress(string CurrentFile, int CompletedFiles, int TotalFiles, long BytesCopied);
public sealed record VideoInfo(int Width, int Height, double FramesPerSecond, double DurationSeconds, int Frames, int AudioTracks);
public sealed record ArchiveSegment(long Offset, long StoredSize, long Size)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsRaw { get; init; }
}

public sealed class ArchiveDocument
{
    internal ArchiveDocument(string sourcePath, string format, long fileSize,
        IReadOnlyList<ArchiveEntry> entries, IReadOnlyDictionary<string,string> metadata,
        string? limitation, byte[] headerBytes, bool supportsMembers)
    {
        SourcePath = sourcePath; Format = format; FileSize = fileSize; Entries = entries;
        Metadata = metadata; Limitation = limitation; HeaderBytes = headerBytes; SupportsMembers = supportsMembers;
    }
    public string SourcePath { get; }
    public string Format { get; }
    public long FileSize { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public IReadOnlyDictionary<string,string> Metadata { get; }
    public string? Limitation { get; }
    public byte[] HeaderBytes { get; }
    public bool SupportsMembers { get; }
    public TtpfInfo? TexturePool { get; internal init; }
    public byte[] ReadPreview(ArchiveEntry entry, int maxBytes = 2 * 1024 * 1024, CancellationToken cancellationToken = default)
        => ArchiveReader.ReadPreview(this, entry, maxBytes, cancellationToken);
    public Task ExtractAsync(IEnumerable<ArchiveEntry> entries, string destination,
        IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
        => ArchiveReader.ExtractAsync(this, entries, destination, progress, cancellationToken);
}
