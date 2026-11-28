using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RpfPreview.Core;

public sealed record WorkspaceViewState(string Search = "", string Type = "all", bool Thumbnails = false,
    bool AvailableOnly = false, string? ScopeSource = null, string? Folder = null, string? Container = null,
    string? SelectedSource = null, string? SelectedEntry = null, int SelectedTexture = -1, bool SearchAllSources = true);


public sealed class AssetWorkspaceProject
{
    private sealed record Source(string Path, long Length, long WriteTicks, string HeaderSha256, string? ProjectFile);
    private sealed record Manifest(int Version, string Mode, Source[] Sources, WorkspaceViewState View);
    private readonly List<Source> _sources;
    private readonly Dictionary<string, ReplacementWorkspace> _projects = new(StringComparer.OrdinalIgnoreCase);
    private string _savedHash;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 24 };
    public string ProjectPath { get; }
    public string AssetsDirectory => ProjectPath + ".assets";
    public IReadOnlyList<string> SourcePaths => _sources.Select(s => s.Path).ToArray();
    public WorkspaceViewState View { get; private set; }
    public bool ArchiveApplied => false;
    private AssetWorkspaceProject(string path, List<Source> sources, WorkspaceViewState view, string hash)
    { ProjectPath = path; _sources = sources; View = view; _savedHash = hash; }

    public static AssetWorkspaceProject Create(string path, IEnumerable<string> sourcePaths)
    {
        path = ProjectFile(path); GuardLinks(path); GuardLinks(path + ".assets");
        if (File.Exists(path) || Directory.Exists(path + ".assets") || File.Exists(path + ".assets")) throw new IOException("Choose a new workspace project filename.");
        var sources = NormalizeSources(sourcePaths).Select(Fingerprint).ToList();
        if (sources.Count == 0) throw new InvalidDataException("Open at least one source before saving a workspace.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); Directory.CreateDirectory(path + ".assets");
        var project = new AssetWorkspaceProject(path, sources, new(), "");
        try { project.Write(true); return project; }
        catch { if (!Directory.EnumerateFileSystemEntries(project.AssetsDirectory).Any()) Directory.Delete(project.AssetsDirectory); throw; }
    }

    public static AssetWorkspaceProject Load(string path)
    {
        path = ProjectFile(path); GuardLinks(path); GuardLinks(path + ".assets");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 2 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid or oversized workspace manifest.");
        byte[] bytes = File.ReadAllBytes(path); Manifest manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(bytes, Options) ?? throw new InvalidDataException("Empty workspace manifest."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid workspace project JSON.", ex); }
        if (manifest.Version != 1 || manifest.Mode != "staged-workspace" || manifest.Sources is not { Length: > 0 and <= 512 } || manifest.View == null)
            throw new InvalidDataException("Unsupported workspace project.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in manifest.Sources)
        {
            if (source == null || !Path.IsPathFullyQualified(source.Path) || !paths.Add(Path.GetFullPath(source.Path)) || Fingerprint(source.Path) != source with { ProjectFile = null })
                throw new InvalidDataException("A workspace source changed or is missing. Restore the original source before opening its edits.");
            if (source.ProjectFile != null && !Regex.IsMatch(source.ProjectFile, "\\A[a-f0-9]{32}\\.rpfproject\\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Unsafe source-project reference.");
        }
        if (!Directory.Exists(path + ".assets")) throw new InvalidDataException("The workspace assets folder is missing.");
        var project = new AssetWorkspaceProject(path, manifest.Sources.ToList(), manifest.View, Hash(bytes));
        foreach (var source in project._sources.Where(s => s.ProjectFile != null)) _ = project.ForSource(source.Path, false);
        return project;
    }

    public ReplacementWorkspace? ForSource(string path, bool create)
    {
        path = Path.GetFullPath(path);
        if (_projects.TryGetValue(path, out var loaded)) return loaded;
        int index = _sources.FindIndex(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidDataException("The source does not belong to this workspace.");
        var source = _sources[index];
        if (source.ProjectFile != null)
        {
            string child = Path.Combine(AssetsDirectory, source.ProjectFile); GuardLinks(child);
            var workspace = ReplacementWorkspace.Load(child);
            if (!string.Equals(workspace.SourcePath, source.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source-project binding mismatch.");
            _projects[path] = workspace; return workspace;
        }
        if (!create) return null;
        VerifySaved();
        string name = Guid.NewGuid().ToString("N") + ".rpfproject";
        var added = ReplacementWorkspace.Create(Path.Combine(AssetsDirectory, name), path);
        _sources[index] = source with { ProjectFile = name };
        try { Write(false); }
        catch { _sources[index] = source; throw; }
        _projects[path] = added; return added;
    }

    public void AddSources(IEnumerable<string> paths)
    {
        VerifySaved(); var additions = NormalizeSources(paths).Where(p => !_sources.Any(s => string.Equals(s.Path, p, StringComparison.OrdinalIgnoreCase))).Select(Fingerprint).ToArray();
        if (_sources.Count + additions.Length > 512) throw new InvalidDataException("Workspace source limit is 512.");
        _sources.AddRange(additions);
        try { Write(false); } catch { if (additions.Length > 0) _sources.RemoveRange(_sources.Count - additions.Length, additions.Length); throw; }
    }

    public void ImportSourceProject(ReplacementWorkspace source)
    {
        var target = ForSource(source.SourcePath, true)!;
        if (string.Equals(target.ProjectPath, source.ProjectPath, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var replacement in source.Entries) target.Import(replacement.Target, source.AssetPath(replacement), replacement.KindLabel);
    }
    public void Save(WorkspaceViewState? view = null)
    {
        VerifySaved(); var previous = View; if (view != null) View = view;
        try { Write(false); } catch { View = previous; throw; }
    }

    private void Write(bool create)
    {
        GuardLinks(ProjectPath); GuardLinks(AssetsDirectory);
        if (!create) VerifySaved();
        foreach (var source in _sources)
            if (Fingerprint(source.Path) != source with { ProjectFile = null }) throw new InvalidDataException("A source changed; the saved workspace was retained.");
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, "staged-workspace", _sources.ToArray(), View), Options);
        string temp = ProjectPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(true); }
            File.Move(temp, ProjectPath, !create); _savedHash = Hash(data);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void VerifySaved()
    {
        GuardLinks(ProjectPath);
        if (!File.Exists(ProjectPath) || new FileInfo(ProjectPath).Length > 4 * 1024 * 1024 || Hash(File.ReadAllBytes(ProjectPath)) != _savedHash)
            throw new IOException("The workspace project changed outside this window. Reopen it before saving.");
    }
    private static string ProjectFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!Path.GetExtension(path).Equals(".rpfworkspace", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Workspace projects use .rpfworkspace.");
        return path;
    }
    private static IEnumerable<string> NormalizeSources(IEnumerable<string> paths)
    {
        var values = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(513).ToArray();
        if (values.Length > 512) throw new InvalidDataException("Workspace source limit is 512.");
        return values;
    }
    private static Source Fingerprint(string path)
    {
        path = Path.GetFullPath(path); GuardLinks(path);
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] header = new byte[(int)Math.Min(65536, file.Length)]; file.ReadExactly(header);
        return new(path, file.Length, File.GetLastWriteTimeUtc(path).Ticks, Hash(header), null);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void GuardLinks(string path)
    {
        for (string? at = Path.GetFullPath(path); at != null; at = Path.GetDirectoryName(at))
        {
            if ((File.Exists(at) || Directory.Exists(at)) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Workspace projects cannot use links or junctions.");
        }
    }
}
