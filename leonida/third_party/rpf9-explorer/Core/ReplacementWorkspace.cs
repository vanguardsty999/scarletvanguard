using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RpfPreview.Core;

public sealed record ReplacementTarget(string EntryPath, long EntryOffset, string Kind,
    int SubresourceIndex = -1, string? AssetKey = null);

public sealed record ReplacementRecord(ReplacementTarget Target, string AssetFile, string OriginalFileName,
    string KindLabel, string Format, long Length, string Sha256, DateTimeOffset ImportedUtc)
{
    public bool Staged => true;
    public bool ArchiveApplied => false;
}





public sealed class ReplacementWorkspace
{
    public const long MaxAssetBytes = 512L * 1024 * 1024;
    private const int MaxEntries = 10000, MaxManifestBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 32 };
    private sealed record SourceIdentity(string Path, long Length, long LastWriteUtcTicks, string HeaderSha256);
    private sealed record Manifest(int SchemaVersion, bool Staged, bool ArchiveApplied,
        SourceIdentity Source, ReplacementRecord[] Entries);
    private readonly SourceIdentity _source;
    private readonly long? _textureVirtualSize;
    private readonly List<ReplacementRecord> _entries;
    private readonly object _gate = new();
    private string _manifestSha256;
    public string ProjectPath { get; }
    public string SourcePath => _source.Path;
    public string AssetsDirectory => ProjectPath + ".assets";
    public bool Staged => true;
    public bool ArchiveApplied => false;
    public IReadOnlyList<ReplacementRecord> Entries { get { lock (_gate) return new ReadOnlyCollection<ReplacementRecord>(_entries.ToArray()); } }

    private ReplacementWorkspace(string project, SourceIdentity source, IEnumerable<ReplacementRecord> entries, string manifestHash)
    { ProjectPath = project; _source = source; _textureVirtualSize = TextureVirtualSize(source.Path); _entries = entries.ToList(); _manifestSha256 = manifestHash; }

    public static ReplacementWorkspace Create(string projectPath, string sourcePath)
    {
        string project = ProjectFilePath(projectPath);
        string source = Path.GetFullPath(sourcePath);
        if (string.Equals(project, source, StringComparison.OrdinalIgnoreCase)) throw Invalid("The project must be separate from its source archive.");
        EnsureNoReparse(project); EnsureNoReparse(project + ".assets");
        if (File.Exists(project) || Directory.Exists(project + ".assets") || File.Exists(project + ".assets"))
            throw new IOException("A project or managed assets folder already exists at this location. Open the existing project or choose a new name.");
        var identity = Fingerprint(source);
        var workspace = new ReplacementWorkspace(project, identity, Array.Empty<ReplacementRecord>(), "");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        EnsureNoReparse(project);
        Directory.CreateDirectory(project + ".assets");
        try { workspace.Save(true); return workspace; }
        catch
        {
            EnsureNoReparse(project + ".assets");
            if (!Directory.EnumerateFileSystemEntries(project + ".assets").Any()) Directory.Delete(project + ".assets");
            throw;
        }
    }

    public static ReplacementWorkspace Load(string projectPath)
    {
        string project = ProjectFilePath(projectPath);
        EnsureNoReparse(project); EnsureNoReparse(project + ".assets");
        var file = new FileInfo(project);
        if (!file.Exists || file.Length is < 2 or > MaxManifestBytes) throw Invalid("The replacement project is missing or exceeds the manifest limit.");
        byte[] bytes = File.ReadAllBytes(project);
        Manifest? manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("The replacement project JSON is invalid.", ex); }
        if (manifest is null || manifest.SchemaVersion != 1 || !manifest.Staged || manifest.ArchiveApplied ||
            manifest.Source is null || manifest.Entries is null || manifest.Entries.Length > MaxEntries)
            throw Invalid("This is not a supported staging project. No archive-applied edits are accepted.");
        ValidateIdentity(manifest.Source);
        if (!Directory.Exists(project + ".assets")) throw Invalid("The project's managed assets folder is missing.");
        var workspace = new ReplacementWorkspace(project, manifest.Source, Array.Empty<ReplacementRecord>(), Hex(SHA256.HashData(bytes)));
        var targets = new HashSet<ReplacementTarget>();
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in manifest.Entries)
        {
            if (record is null || record.Target is null) throw Invalid("The project contains an invalid replacement record.");
            var target = workspace.Normalize(record.Target);
            if (target != record.Target || !targets.Add(target) || !assets.Add(record.AssetFile ?? ""))
                throw Invalid("The project contains noncanonical or duplicate replacement targets/assets.");
            workspace.ValidateRecord(record);
            workspace._entries.Add(record);
        }
        return workspace;
    }

    public ReplacementRecord Import(ReplacementTarget target, string inputPath, string kindLabel)
    {
        lock (_gate)
        {
            VerifySource(); VerifyManifest();
            target = Normalize(target);
            int previousIndex = _entries.FindIndex(e => e.Target == target);
            if (previousIndex < 0 && _entries.Count >= MaxEntries) throw Invalid("The project has reached its replacement limit.");
            string input = Path.GetFullPath(inputPath);
            EnsureNoReparse(input); EnsureNoReparse(AssetsDirectory);
            if (!Directory.Exists(AssetsDirectory)) throw Invalid("The project's managed assets folder is missing.");
            string extension = SafeExtension(input);
            string temporary = Path.Combine(AssetsDirectory, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
            string managed = Guid.NewGuid().ToString("N") + extension;
            string final = ManagedPath(managed);
            bool copied = false, renamed = false;
            try
            {
                long length;
                string hash;
                using (var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    copied = true; length = source.Length;
                    if (length is <= 0 or > MaxAssetBytes) throw Invalid("Replacement assets must contain 1 byte to 512 MiB.");
                    byte[] buffer = new byte[65536]; long remaining = length;
                    while (remaining > 0)
                    {
                        int n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (n == 0) throw Invalid("The imported asset changed or was truncated during copying.");
                        output.Write(buffer, 0, n); digest.AppendData(buffer, 0, n); remaining -= n;
                    }
                    if (source.ReadByte() >= 0) throw Invalid("The imported asset changed during copying.");
                    output.Flush(true); hash = Hex(digest.GetHashAndReset());
                }
                string format = ValidateAsset(temporary, extension, target.Kind);
                var record = new ReplacementRecord(target, managed, SafeLabel(Path.GetFileName(input), 255),
                    SafeLabel(kindLabel, 128), format, length, hash, DateTimeOffset.UtcNow);
                EnsureNoReparse(AssetsDirectory); File.Move(temporary, final, false); renamed = true;
                var old = previousIndex >= 0 ? _entries[previousIndex] : null;
                if (previousIndex >= 0) _entries[previousIndex] = record; else _entries.Add(record);
                try { Save(false); }
                catch
                {
                    if (previousIndex >= 0) _entries[previousIndex] = old!; else _entries.RemoveAt(_entries.Count - 1);
                    throw;
                }
                if (old is not null) DeleteUnreferenced(old.AssetFile);
                return record;
            }
            catch
            {
                if (renamed) DeleteUnreferenced(managed);
                if (copied && File.Exists(temporary)) { EnsureNoReparse(temporary); File.Delete(temporary); }
                throw;
            }
        }
    }

    public ReplacementRecord? Find(ReplacementTarget target)
    { lock (_gate) { target = Normalize(target); return _entries.FirstOrDefault(e => e.Target == target); } }

    public bool Remove(ReplacementTarget target)
    {
        lock (_gate)
        {
            VerifySource(); VerifyManifest(); target = Normalize(target);
            int at = _entries.FindIndex(e => e.Target == target);
            if (at < 0) return false;
            var old = _entries[at]; _entries.RemoveAt(at);
            try { Save(false); } catch { _entries.Insert(at, old); throw; }
            DeleteUnreferenced(old.AssetFile); return true;
        }
    }

    public string AssetPath(ReplacementRecord record)
    {
        lock (_gate)
        {
            VerifySource();
            if (record is null || !_entries.Contains(record)) throw Invalid("The replacement does not belong to this project.");
            ValidateRecord(record); return ManagedPath(record.AssetFile);
        }
    }

    private ReplacementTarget Normalize(ReplacementTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string path = ArchiveReader.ValidateMemberPath(target.EntryPath ?? "");
        string kind = (target.Kind ?? "").Trim().ToLowerInvariant();
        kind = kind switch { "raw" or "native" => "binary", _ => kind };
        if (kind is not ("model" or "texture" or "audio" or "binary" or "text")) throw Invalid("Unknown replacement kind. Use model, texture, audio, binary or text.");



        long targetBound = kind == "texture" && _textureVirtualSize is { } virtualSize ? virtualSize : _source.Length;
        if (target.EntryOffset < 0 || target.EntryOffset >= targetBound || target.SubresourceIndex < -1)
            throw Invalid("The replacement target is outside its source archive/virtual pool or has an invalid subresource index.");
        string? key = string.IsNullOrWhiteSpace(target.AssetKey) ? null : target.AssetKey;
        if (key is not null && (key.Length > 512 || key.Any(char.IsControl))) throw Invalid("Invalid replacement subresource key.");
        return new(path, target.EntryOffset, kind, target.SubresourceIndex, key);
    }

    private void ValidateRecord(ReplacementRecord record)
    {
        if (record.Length is <= 0 or > MaxAssetBytes || !HashText(record.Sha256) ||
            record.OriginalFileName is null || record.OriginalFileName.Length > 255 || record.OriginalFileName.Any(char.IsControl) ||
            record.KindLabel is null || record.KindLabel.Length > 128 || record.KindLabel.Any(char.IsControl) ||
            string.IsNullOrEmpty(record.Format) || record.Format.Length > 32 || record.Format.Any(char.IsControl)) throw Invalid("Invalid replacement asset metadata.");
        string path = ManagedPath(record.AssetFile);
        EnsureNoReparse(path);
        using var asset = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (asset.Length != record.Length || !string.Equals(Hex(SHA256.HashData(asset)), record.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Invalid("A staged asset is missing, changed or does not match its SHA-256.");
        string actual = ValidateAsset(path, Path.GetExtension(record.AssetFile), record.Target.Kind);
        if (!string.Equals(actual, record.Format, StringComparison.Ordinal)) throw Invalid("The staged asset's format does not match the project.");
    }

    private string ManagedPath(string leaf)
    {
        if (string.IsNullOrEmpty(leaf) || leaf.Length > 64 || Path.GetFileName(leaf) != leaf || leaf.IndexOfAny(['/', '\\', ':']) >= 0)
            throw Invalid("A managed asset path may not contain directories or traversal.");
        string stem = Path.GetFileNameWithoutExtension(leaf), ext = Path.GetExtension(leaf);
        if (!Guid.TryParseExact(stem, "N", out _) || ext.Length is < 2 or > 17 || ext[1..].Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw Invalid("A managed asset name must be a generated identifier and safe extension.");
        string result = Path.GetFullPath(Path.Combine(AssetsDirectory, leaf));
        string prefix = Path.GetFullPath(AssetsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw Invalid("The staged asset escapes its workspace.");
        EnsureNoReparse(result); return result;
    }

    private void DeleteUnreferenced(string leaf)
    {
        if (_entries.Any(e => string.Equals(e.AssetFile, leaf, StringComparison.OrdinalIgnoreCase))) return;
        try { string path = ManagedPath(leaf); if (File.Exists(path)) File.Delete(path); }
        catch (IOException) {   }
        catch (UnauthorizedAccessException) { }
    }

    private void Save(bool create)
    {
        VerifySource();
        if (!create) VerifyManifest();
        EnsureNoReparse(ProjectPath); EnsureNoReparse(AssetsDirectory);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, true, false, _source, _entries.ToArray()), JsonOptions);
        if (json.Length > MaxManifestBytes) throw Invalid("The project exceeds its manifest size limit.");
        string temporary = ProjectPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(json); output.Flush(true); }
            EnsureNoReparse(ProjectPath);
            File.Move(temporary, ProjectPath, !create);
            _manifestSha256 = Hex(SHA256.HashData(json));
        }
        finally { if (File.Exists(temporary)) { EnsureNoReparse(temporary); File.Delete(temporary); } }
    }

    private void VerifyManifest()
    {
        EnsureNoReparse(ProjectPath);
        var file = new FileInfo(ProjectPath);
        if (!file.Exists || file.Length > MaxManifestBytes) throw Invalid("The project manifest was removed or changed.");
        using var input = File.OpenRead(ProjectPath);
        if (!string.Equals(Hex(SHA256.HashData(input)), _manifestSha256, StringComparison.Ordinal))
            throw new IOException("The project changed outside this window. Reopen it before saving another staged edit.");
    }
    private void VerifySource() => ValidateIdentity(_source);
    private static void ValidateIdentity(SourceIdentity expected)
    {
        if (string.IsNullOrEmpty(expected.Path) || !Path.IsPathFullyQualified(expected.Path) || expected.Length <= 0 || !HashText(expected.HeaderSha256))
            throw Invalid("Invalid project source identity.");
        SourceIdentity actual = Fingerprint(expected.Path);
        if (actual != expected) throw Invalid("The source archive no longer matches this project (path, length, modification time or header checksum changed).");
    }
    private static SourceIdentity Fingerprint(string path)
    {
        path = Path.GetFullPath(path); EnsureNoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
        if (stream.Length <= 0) throw Invalid("The source archive is empty.");
        byte[] header = new byte[(int)Math.Min(65536, stream.Length)]; stream.ReadExactly(header);
        return new(path, stream.Length, File.GetLastWriteTimeUtc(path).Ticks, Hex(SHA256.HashData(header)));
    }
    private static long? TextureVirtualSize(string path)
    {
        EnsureNoReparse(path);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        if (source.Length < 4) return null;
        byte[] magic = new byte[4]; source.ReadExactly(magic);
        if (!magic.AsSpan().SequenceEqual("RDS5"u8)) return null;
        var pool = TtpfReader.Inspect(path);
        if (pool.VirtualSize is < 4096 or > (1L << 34)) throw Invalid("RDS5 virtual size exceeds the validated workspace bound.");
        return pool.VirtualSize;
    }

    private static string ProjectFilePath(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".rpfproject", StringComparison.OrdinalIgnoreCase)) throw Invalid("Use the .rpfproject extension for replacement projects.");
        return path;
    }
    private static void EnsureNoReparse(string path)
    {
        for (string? at = Path.GetFullPath(path); at is not null; at = Path.GetDirectoryName(at))
        {
            try { if ((File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0) throw new IOException("Workspace access through a symbolic link or junction is not supported."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static string SafeExtension(string path)
    { string ext = Path.GetExtension(path).ToLowerInvariant(); return ext.Length is >= 2 and <= 17 && ext[1..].All(char.IsAsciiLetterOrDigit) ? ext : ".bin"; }
    private static string SafeLabel(string? text, int limit) => new((text ?? "").Where(c => !char.IsControl(c)).Take(limit).ToArray());
    private static bool HashText(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Hex(byte[] value) => Convert.ToHexString(value);
    private static InvalidDataException Invalid(string message) => new(message);

    private static string ValidateAsset(string path, string extension, string kind)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
        if (file.Length is <= 0 or > MaxAssetBytes) throw Invalid("Invalid replacement asset length.");
        extension = extension.ToLowerInvariant();
        if (kind == "binary") return "Opaque binary";
        if (kind == "model")
        {
            if (extension != ".obj" || file.Length > 32 * 1024 * 1024) throw Invalid("Model replacements require a bounded standard .obj file.");
            file.Dispose();
            _ = ObjModelReader.Parse(File.ReadAllText(path, StrictUtf8), Path.GetFileNameWithoutExtension(path)); return "OBJ";
        }
        if (kind == "text")
        {
            if (file.Length > 32 * 1024 * 1024) throw Invalid("Text replacements exceed the 32 MiB limit.");
            file.Dispose();
            try { _ = StrictUtf8.GetString(File.ReadAllBytes(path)); } catch (DecoderFallbackException ex) { throw new InvalidDataException("The text replacement is not valid UTF-8.", ex); }
            return "UTF-8 text";
        }
        if (kind == "audio") { if (extension != ".wav") throw Invalid("Audio replacements require a standard .wav file."); ValidateWave(file); return "WAV"; }
        if (kind == "texture")
        {
            if (extension == ".dds") { ValidateDds(file); return "DDS"; }
            if (extension == ".png") { ValidatePng(file); return "PNG"; }
            if (extension is ".jpg" or ".jpeg") { ValidateJpeg(file); return "JPEG"; }
            throw Invalid("Texture replacements require .dds, .png, .jpg or .jpeg files.");
        }
        throw Invalid("Unsupported replacement kind.");
    }

    private static void ValidateWave(FileStream file)
    {
        byte[] head = Read(file, 0, 12);
        if (!head.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !head.AsSpan(8, 4).SequenceEqual("WAVE"u8) || U32(head, 4) != file.Length - 8)
            throw Invalid("The WAV RIFF bounds are invalid.");
        bool format = false, data = false; int align = 0;
        for (long p = 12; p < file.Length;)
        {
            byte[] chunk = Read(file, p, 8); uint size = U32(chunk, 4); long start = checked(p + 8), end = checked(start + size);
            if (end > file.Length) throw Invalid("A WAV chunk is truncated.");
            if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (format || size is < 16 or > 65536) throw Invalid("Invalid or duplicate WAV format chunk.");
                byte[] fmt = Read(file, start, (int)size); int tag = U16(fmt, 0), channels = U16(fmt, 2), bits = U16(fmt, 14);
                uint rate = U32(fmt, 4), byteRate = U32(fmt, 8); align = U16(fmt, 12);
                if (tag == 0xFFFE)
                {
                    if (size < 40 || U16(fmt, 16) < 22 || U16(fmt, 18) > bits ||
                        !fmt.AsSpan(28, 12).SequenceEqual(new byte[] { 0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113 }))
                        throw Invalid("Invalid WAV extensible PCM/float format.");
                    tag = checked((int)U32(fmt, 24));
                }
                if (channels is < 1 or > 32 || rate is < 1000 or > 384000 || tag is not (1 or 3) ||
                    (tag == 1 && bits is not (8 or 16 or 24 or 32)) || (tag == 3 && bits is not (32 or 64)) ||
                    align != channels * (bits / 8) || byteRate != rate * align)
                    throw Invalid("WAV replacements support bounded PCM or IEEE float audio with consistent frame sizes.");
                format = true;
            }
            if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
            { if (!format || data || size == 0 || size % align != 0) throw Invalid("Invalid WAV sample data length."); data = true; }
            p = checked(end + (size & 1));
            if (p > file.Length) throw Invalid("Missing WAV chunk padding.");
        }
        if (!format || !data) throw Invalid("WAV format/sample data is missing.");
    }

    private static void ValidateDds(FileStream file)
    {
        if (file.Length > DdsTextureReader.MaxFileBytes) throw Invalid("The DDS exceeds its validated import limit.");
        file.Position = 0;
        byte[] data = new byte[(int)file.Length]; file.ReadExactly(data);
        _ = DdsTextureReader.Inspect(data);
    }

    private static void ValidatePng(FileStream file)
    {
        if (!Read(file, 0, 8).AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw Invalid("Invalid PNG signature.");
        bool header = false, data = false, ended = false; long p = 8; int chunks = 0; byte[] buffer = new byte[65536];
        while (p < file.Length && ++chunks <= 65536)
        {
            byte[] h = Read(file, p, 8); uint count = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0, 4));
            long end = checked(p + 12 + count); if (end > file.Length) throw Invalid("PNG chunk exceeds the file.");
            bool ihdr = h.AsSpan(4, 4).SequenceEqual("IHDR"u8), idat = h.AsSpan(4, 4).SequenceEqual("IDAT"u8), iend = h.AsSpan(4, 4).SequenceEqual("IEND"u8);
            if (!header && !ihdr) throw Invalid("PNG IHDR must be first.");
            if (ihdr)
            {
                if (header || count != 13) throw Invalid("Invalid PNG IHDR."); byte[] d = Read(file, p + 8, 13);
                Dimension(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(0, 4))); Dimension(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4, 4)));
                int bit = d[8], color = d[9]; bool bits = color switch { 0 => bit is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => bit is 8 or 16, 3 => bit is 1 or 2 or 4 or 8, _ => false };
                if (!bits || d[10] != 0 || d[11] != 0 || d[12] > 1) throw Invalid("Invalid PNG color/compression fields."); header = true;
            }
            uint crc = Crc(0xFFFFFFFF, h.AsSpan(4, 4)); file.Position = p + 8; long remaining = count;
            while (remaining > 0) { int n = (int)Math.Min(remaining, buffer.Length); file.ReadExactly(buffer.AsSpan(0, n)); crc = Crc(crc, buffer.AsSpan(0, n)); remaining -= n; }
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(Read(file, p + 8 + count, 4));
            if ((crc ^ 0xFFFFFFFF) != stored) throw Invalid("PNG chunk checksum does not match.");
            data |= idat && count > 0; p = end;
            if (iend) { if (count != 0 || !data || p != file.Length) throw Invalid("PNG end/sample data bounds are invalid."); ended = true; break; }
        }
        if (!header || !data || !ended) throw Invalid("PNG is incomplete or exceeds the chunk limit.");
    }
    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    { foreach (byte b in data) crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 255]; return crc; }
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int i = 0; i < 8; i++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1; table[n] = c; }
        return table;
    }

    private static void ValidateJpeg(FileStream file)
    {
        if (!Read(file, 0, 2).AsSpan().SequenceEqual(new byte[] { 255, 216 })) throw Invalid("Invalid JPEG signature.");
        bool frame = false, scan = false; int pending = -1, segments = 0; file.Position = 2;
        while ((pending >= 0 || file.Position < file.Length) && ++segments <= 65536)
        {
            int marker;
            if (pending >= 0) { marker = pending; pending = -1; }
            else { if (file.ReadByte() != 255) throw Invalid("Invalid JPEG marker."); do { marker = file.ReadByte(); } while (marker == 255); }
            if (marker == 217) { if (!frame || !scan || file.Position != file.Length) throw Invalid("JPEG is incomplete or has trailing bytes."); return; }
            if (marker < 0 || marker is 0 or 216 || marker is >= 208 and <= 215) throw Invalid("Unexpected JPEG marker.");
            byte[] n = new byte[2]; file.ReadExactly(n); int size = BinaryPrimitives.ReadUInt16BigEndian(n); long end = file.Position + size - 2;
            if (size < 2 || end > file.Length) throw Invalid("JPEG segment exceeds file bounds.");
            if (marker is 192 or 193 or 194)
            {
                if (frame || size < 11) throw Invalid("Invalid JPEG frame."); byte[] d = Read(file, file.Position, size - 2);
                if (d[0] != 8 || d[5] is not (1 or 3 or 4) || size != 8 + 3 * d[5]) throw Invalid("Unsupported JPEG sample layout.");
                Dimension(BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(1, 2))); Dimension(BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(3, 2))); frame = true;
            }
            file.Position = end;
            if (marker == 218)
            {
                if (!frame || size < 6) throw Invalid("Invalid JPEG scan."); scan = true;
                while (true)
                {
                    int b = file.ReadByte(); if (b < 0) throw Invalid("Truncated JPEG entropy data."); if (b != 255) continue;
                    int next; do { next = file.ReadByte(); } while (next == 255);
                    if (next < 0) throw Invalid("Truncated JPEG marker."); if (next == 0 || next is >= 208 and <= 215) continue;
                    pending = next; break;
                }
            }
        }
        throw Invalid("JPEG is incomplete or exceeds the segment limit.");
    }
    private static int Dimension(uint value) => value is > 0 and <= 32768 ? (int)value : throw Invalid("Image dimensions must be between 1 and 32768 pixels.");
    private static byte[] Read(FileStream file, long at, int count)
    {
        if (at < 0 || count < 0 || at > file.Length || count > file.Length - at) throw Invalid("The replacement file is truncated.");
        byte[] result = new byte[count]; file.Position = at; file.ReadExactly(result); return result;
    }
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
}
