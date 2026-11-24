using System.Security.Cryptography;
using System.Text;

namespace RpfPreview.Core;

public static class WorkspaceAssetExporter
{

    public static async Task ExtractAsync(IEnumerable<WorkspaceAsset> selected, string destination,
        IProgress<ExtractionProgress>? progress = null, CancellationToken token = default)
    {
        var assets = selected.DistinctBy(a => (a.SourcePath, a.Entry.Path, a.Texture?.Index ?? -1)).ToArray();
        if (assets.Any(a => !a.Available)) throw new NotSupportedException("The selection contains unavailable assets.");
        destination = Path.GetFullPath(destination); GuardDirectory(destination); Directory.CreateDirectory(destination);
        int completed = 0; long bytesCopied = 0;
        foreach (var source in assets.GroupBy(a => a.Document))
        {
            string name = SafeName(Path.GetFileNameWithoutExtension(source.Key.SourcePath));
            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Key.SourcePath.ToUpperInvariant())))[..8];
            string sourceFolder = Path.Combine(destination, name + "_" + id); GuardDirectory(sourceFolder); Directory.CreateDirectory(sourceFolder);
            foreach (var entryGroup in source.GroupBy(a => a.Entry))
            {
                byte[]? resource = null; int split = 0;
                foreach (var asset in entryGroup)
                {
                    token.ThrowIfCancellationRequested();
                    if (asset.Entry.Compression == "Recovered model")
                    {
                        resource ??= ArchiveReader.ReadResourceData(asset.Document, asset.Entry, token);
                        split = asset.Entry.ResourceSystemBytes ?? checked((int)asset.Entry.Segments![0].Size);
                        var model = RageModelDecoder.Decode(resource.AsSpan(0, split), resource.AsSpan(split), asset.Entry.ResourceDrawableOffset)
                            ?? throw new InvalidDataException("The selected model no longer matches its validated source.");
                        byte[] obj = Encoding.UTF8.GetBytes(RageModelDecoder.CreateObj(model));
                        string output = ArchiveReader.SafeDestination(sourceFolder, Path.ChangeExtension(asset.Entry.Path, ".obj"));
                        await WriteExportAsync(output, obj, token);
                        bytesCopied += obj.Length;
                    }
                    else if (asset.Texture == null)
                    {
                        await asset.Document.ExtractAsync(new[] { asset.Entry }, sourceFolder, cancellationToken: token);
                        bytesCopied += asset.Size;
                    }
                    else
                    {
                        if (resource == null) { resource = ArchiveReader.ReadResourceData(asset.Document, asset.Entry, token); split = asset.Entry.ResourceSystemBytes ?? checked((int)asset.Entry.Segments![0].Size); }
                        byte[] dds = RageTextureDecoder.CreateDds(resource.AsSpan(0, split), resource.AsSpan(split), asset.Texture.Index);
                        string relative = asset.Entry.Path + "_images/texture_" + asset.Texture.Index.ToString("D4") + "_" + SafeName(asset.Name) + ".dds";
                        string output = ArchiveReader.SafeDestination(sourceFolder, relative);
                        GuardDirectory(Path.GetDirectoryName(output)!); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
                        try
                        {
                            if (File.Exists(output)) throw new IOException("An export already exists: " + output);
                            await File.WriteAllBytesAsync(temporary, dds, token); token.ThrowIfCancellationRequested();
                            GuardDirectory(Path.GetDirectoryName(output)!); File.Move(temporary, output, false);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        bytesCopied += dds.Length;
                    }
                    progress?.Report(new(asset.Name, ++completed, assets.Length, bytesCopied));
                }
            }
        }
    }
    private static async Task WriteExportAsync(string output, byte[] content, CancellationToken token)
    {
        GuardDirectory(Path.GetDirectoryName(output)!); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            if (File.Exists(output)) throw new IOException("An export already exists: " + output);
            await File.WriteAllBytesAsync(temporary, content, token); token.ThrowIfCancellationRequested();
            GuardDirectory(Path.GetDirectoryName(output)!); File.Move(temporary, output, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string SafeName(string name)
    {
        var chars = name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        string value = new string(chars).Trim(' ', '.'); return value.Length == 0 ? "asset" : value[..Math.Min(140, value.Length)];
    }
    private static void GuardDirectory(string path)
    {
        for (DirectoryInfo? dir = new(Path.GetFullPath(path)); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Extraction through links or junctions is unsupported.");
    }
}
