using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RpfPreview.Core;

public static class ArchiveReader
{
    private const int MaxIndexBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 250_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static VideoInfo? GetVideoInfo(ArchiveDocument document, ArchiveEntry entry)
    {
        ValidateEntry(document, entry);
        if (!entry.Compression.StartsWith("Recovered Bink", StringComparison.Ordinal)) return null;
        var info = Rpf9MediaRecovery.Inspect(document.SourcePath, entry);
        return info is null ? null : new(info.Width, info.Height, info.FramesPerSecond, info.DurationSeconds, info.Frames, info.AudioTracks);
    }

    public static ArchiveDocument Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 4) throw new InvalidDataException("The file is too short to contain an archive header.");
        var header = new byte[(int)Math.Min(stream.Length, 4096)];
        stream.ReadExactly(header);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if ((magic & 0xffff) == 0x4b50) return OpenZip(path, stream.Length, header);
        if (magic == 0x35534452) return OpenTtpf(path, stream.Length, header);
        if (magic == 0x52504637) return OpenRpf7(path, stream, header);
        if (magic == 0x52504639 || magic == 0x39465052)
            return OpenRpf9(path, stream.Length, header,
                "RPF9 recognized, but this build cannot decode its file directory. No complete Bink movies, XML documents or PSIN metadata were recovered within the search limits. " +
                "This does not mean that the archive is empty or corrupt. Only header inspection is available for this archive. " +
                "Additional asset types may require a verified format decoder and any applicable keys; opaque bytes alone do not establish the cipher or codec.");
        if (magic == 0x37465052)
            return OpaqueRpf(path, stream.Length, header, "RPF7 (console byte order)",
                "This RPF7 byte order is not supported. Full browsing and extraction require a compatible console-format decoder.");
        if (header[0] is >= (byte)'0' and <= (byte)'8' && header[1] == 'F' && header[2] == 'P' && header[3] == 'R')
            return OpaqueRpf(path, stream.Length, header, "RPF" + (char)header[0],
                "This RPF version is not supported by the member decoder in this build. Header inspection is available.");
        if (header[0] == 'R' && header[1] == 'P' && header[2] == 'F' && header[3] is >= (byte)'0' and <= (byte)'8')
            return OpaqueRpf(path, stream.Length, header, "RPF" + (char)header[3],
                "This RPF version is not supported by the member decoder in this build. Header inspection is available.");
        throw new InvalidDataException("Unrecognized archive signature. Supported readers: ZIP, open little-endian RPF7, and supported RDS5 texture pools. RPF9 supports partial recovery of specific embedded formats.");
    }

    private static ArchiveDocument OpenTtpf(string path, long length, byte[] header)
    {
        var pool = TtpfReader.Inspect(path);
        var entries = pool.Textures.Select(t => new ArchiveEntry(t.Name, t.StoredSize, t.DdsSize,
            t.VirtualOffset, false, false, false, "RDS5 texture", t.Index) { Limitation = t.Limitation }).ToArray();
        var metadata = BaseMetadata(header, length);
        foreach (var pair in pool.Metadata) metadata[pair.Key] = pair.Value;
        metadata["Offset column"] = "Virtual pool offsets; stored bytes may share compressed source chunks";
        return new(path, "RDS5", length, entries, metadata,
            "Native texture catalog; generated names. Supported rows export standard DDS files (including BCPack BC1/BC3/BC4/BC5). Unmapped shard records, unsupported storage layouts and unsupported BCPack codecs (BC6H/BC7) remain unavailable. Opaque catalog IDs are not decoded filenames.", header, true) { TexturePool = pool };
    }

    private static ArchiveDocument OpenRpf9(string path, long length, byte[] header, string unavailable)
    {

        var media = Rpf9MediaRecovery.Recover(path, length, header);
        var structured = Rpf9StructuredRecovery.Recover(path, length, header, media.Count);
        int nextIndex = media.Count + structured.Count;
        var cachedTextures = RecoveryIndexCache.Load("textures", path, length, header, nextIndex);
        var textures = cachedTextures?.Entries ?? Rpf9TextureRecovery.Recover(path, length, header, nextIndex);
        nextIndex += textures.Count;
        var cachedAudio = RecoveryIndexCache.Load("audio", path, length, header, nextIndex);
        IReadOnlyList<ArchiveEntry> audio = cachedAudio?.Entries
            ?? (Path.GetFileName(path).Equals("audio.rpf", StringComparison.OrdinalIgnoreCase)
                ? AwcRecovery.Recover(path, length, header, nextIndex) : Array.Empty<ArchiveEntry>());
        nextIndex += audio.Count;
        var cachedModels = RecoveryIndexCache.Load("models", path, length, header, nextIndex)
            ?? RecoveryIndexCache.LoadLegacyModels(path, length, header, nextIndex);
        var models = cachedModels?.Entries ?? Rpf9ModelRecovery.Recover(path, length, header, nextIndex);
        nextIndex += models.Count;
        var cachedShaders = RecoveryIndexCache.Load("shaders", path, length, header, nextIndex);
        var shaders = cachedShaders?.Entries ?? Rpf9ShaderRecovery.Recover(path, length, header, nextIndex);
        var recovered = media.Concat(structured).Concat(textures).Concat(audio).Concat(models).Concat(shaders).ToArray();
        if (recovered.Length == 0) return OpaqueRpf(path, length, header, "RPF9", unavailable);
        var metadata = BaseMetadata(header, length);
        metadata["Recovery mode"] = "Validated embedded streams; original RPF9 index remains undecoded";
        metadata["Original names and folders"] = "Unknown — generated offset-based names are used";
        metadata["Recovered Bink videos"] = media.Count.ToString("N0");
        metadata["Recovered complete XML documents"] = structured.Count(e => e.Extension == ".xml").ToString("N0");
        metadata["Recovered complete PSIN metadata"] = structured.Count(e => e.Extension == ".psin").ToString("N0");
        metadata["Recovered texture dictionaries"] = textures.Count.ToString("N0");
        metadata["Recovered audio banks"] = audio.Count.ToString("N0");
        metadata["Recovered models"] = models.Count.ToString("N0");
        metadata["Recovered native shader binaries"] = shaders.Count.ToString("N0");
        metadata["Shader search coverage"] = cachedShaders?.Coverage ?? "First 16 MiB; exact named raw-DEFLATE wrappers and DXBC bounds; no shader execution";
        metadata["Texture search coverage"] = cachedTextures?.Coverage ?? "First 16 MiB; at most 32 dictionaries; supported resource and tile layouts only";
        metadata["Audio search coverage"] = cachedAudio?.Coverage ?? (AwcRecovery.HasCompleteCache(path) ? "Full byte-signature scan cached; AWC header/range validation, not original directory decoding" : "First 128 MiB and last 16 MiB of audio.rpf; validated AWC signatures only");
        metadata["Model search coverage"] = cachedModels?.Coverage ?? "First 16 MiB; supported drawable and meshlet layouts only";
        metadata["Joined XML documents"] = structured.Count(e => e.Extension == ".xml" && e.Segments is { Count: > 1 }).ToString("N0");
        metadata["Joined PSIN metadata"] = structured.Count(e => e.Extension == ".psin" && e.Segments is { Count: > 1 }).ToString("N0");
        metadata["Zlib search boundary"] = "First 16 MiB of the payload area, within validation and output limits; not an exhaustive archive scan";
        metadata["Bink validation"] = "Complete frame-index, file-length, dimensions and alignment checks";
        metadata["XML validation"] = "Adler-32-verified zlib blocks, complete XML parsing, and strictly zero-padded 16-byte alignment for joined 64 KiB blocks";
        metadata["PSIN validation"] = "Adler-32-verified zlib blocks; seven complete sections and matching CHKS total length. Internal CHKS checksum algorithm remains unverified.";
        return new(path, "RPF9", length, recovered, metadata,
            "Partial RPF9 recovery: the original member index is still undecoded. The listed entries are validated embedded streams, " +
            "with generated names based on their offsets. Original filenames/folders and other archive contents remain unavailable. " +
            "This is a recovery view, not a complete RPF9 directory listing.", header, true);
    }

    private static ArchiveDocument OpaqueRpf(string path, long length, byte[] header, string format, string limitation)
    {
        var metadata = BaseMetadata(header, length);
        metadata["Member decoding"] = "Unavailable";
        metadata["Member count"] = "Unknown — index not decoded";
        for (int i = 4; i + 4 <= Math.Min(header.Length, 64); i += 4)
            metadata[$"Raw header word @ 0x{i:X2}"] = $"0x{BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(i, 4)):X8}";
        metadata["Header word interpretation"] = "Raw values only; fields have not been mapped to verified RPF9 meanings.";
        return new(path, format, length, Array.Empty<ArchiveEntry>(), metadata, limitation, header, false);
    }

    private static Dictionary<string,string> BaseMetadata(byte[] header, long length) => new()
    {
        ["Signature bytes"] = Convert.ToHexString(header.AsSpan(0, 4)),
        ["Signature text"] = new(header.Take(4).Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray()),
        ["Archive size"] = $"{length:N0} bytes",
        ["Source access"] = "Read-only",
        ["Header preview"] = $"First {header.Length:N0} bytes only"
    };

    private static ArchiveDocument OpenZip(string path, long length, byte[] header)
    {
        CheckZipIndexBounds(path);
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("Archive contains too many entries for this viewer.");
        var entries = new List<ArchiveEntry>(zip.Entries.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < zip.Entries.Count; i++)
        {
            var z = zip.Entries[i];
            bool directory = z.FullName.EndsWith('/') || z.FullName.EndsWith('\\');
            string memberPath = ValidateMemberPath(z.FullName, directory);
            if (!paths.Add(memberPath)) throw new InvalidDataException("Duplicate or case-colliding member paths are not supported.");

            if (((z.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("This archive contains a symbolic link. Link extraction is not supported.");
            entries.Add(new(memberPath, z.CompressedLength, z.Length, 0, directory, false, false,
                z.CompressedLength == z.Length ? "ZIP" : "ZIP compressed", i));
        }
        var metadata = BaseMetadata(header, length);
        metadata["Member decoding"] = "Supported";
        metadata["Member count"] = entries.Count.ToString("N0");
        metadata["Extraction"] = "Selected files or all files; sources remain unchanged";
        return new(path, "ZIP", length, entries, metadata, null, header, true);
    }

    private static void CheckZipIndexBounds(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 22) throw new InvalidDataException("Truncated ZIP end record.");
        byte[] tail = new byte[(int)Math.Min(file.Length, 65557)];
        file.Position = file.Length - tail.Length; file.ReadExactly(tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2)) == tail.Length)
            { end = i; break; }
        if (end < 0) throw new InvalidDataException("ZIP end record not found.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4, 2)) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 6, 2)) != 0)
            throw new NotSupportedException("Multi-volume ZIP archives are not supported.");
        ulong count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10, 2));
        ulong size = U32(tail, end + 12), offset = U32(tail, end + 16);
        long endPosition = file.Length - tail.Length + end;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            if (endPosition < 20) throw new InvalidDataException("ZIP64 locator is missing.");
            file.Position = endPosition - 20; byte[] locator = new byte[20]; file.ReadExactly(locator);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1)
                throw new InvalidDataException("Invalid or multi-volume ZIP64 locator.");
            ulong recordOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8, 8));
            if (recordOffset > (ulong)Math.Max(0, file.Length - 56)) throw new InvalidDataException("ZIP64 record points outside the archive.");
            file.Position = (long)recordOffset; byte[] record = new byte[56]; file.ReadExactly(record);
            if (U32(record, 0) != 0x06064b50 || U32(record, 16) != 0 || U32(record, 20) != 0)
                throw new InvalidDataException("Invalid ZIP64 end record.");
            count = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32, 8));
            size = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40, 8));
            offset = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48, 8));
        }
        if (count > MaxEntries || size > MaxIndexBytes || offset > (ulong)endPosition || size > (ulong)endPosition - offset)
            throw new InvalidDataException("ZIP index sizes are invalid or exceed the viewer's limits.");
    }

    private sealed record RpfRecord(string Name, uint A, uint B, uint C, uint D, ulong Packed, bool Directory, bool Resource);

    private static ArchiveDocument OpenRpf7(string path, FileStream stream, byte[] header)
    {
        if (header.Length < 16) throw new InvalidDataException("Truncated RPF7 header.");
        uint count = U32(header, 4), namesLength = U32(header, 8), encryption = U32(header, 12);
        long tableLength = (long)count * 16;
        long indexEnd = checked(16 + tableLength + namesLength);
        if (count == 0 || count > MaxEntries || tableLength + namesLength > MaxIndexBytes || indexEnd > stream.Length)
            throw new InvalidDataException("RPF7 index sizes are invalid or exceed the viewer's limits.");
        var metadata = BaseMetadata(header, stream.Length);
        metadata["Index records"] = count.ToString("N0");
        metadata["Name table size"] = $"{namesLength:N0} bytes";
        metadata["Encryption tag"] = $"0x{encryption:X8}";
        if (encryption is not 0 and not 0x4e45504f)
        {
            metadata["Member decoding"] = "Unavailable — protected index";
            string scheme = encryption == 0x0ffffff9 ? "AES" : encryption == 0x0fefffff ? "NG" : "unknown";
            return new(path, "RPF7", stream.Length, Array.Empty<ArchiveEntry>(), metadata,
                $"This RPF7 index uses {scheme} protection. This build does not include game keys or a decoder for protected indexes. Header inspection is available.", header, false);
        }
        stream.Position = 16;
        byte[] table = new byte[checked((int)tableLength)], names = new byte[checked((int)namesLength)];
        stream.ReadExactly(table); stream.ReadExactly(names);
        var records = new RpfRecord[count];
        for (int i = 0; i < records.Length; i++)
        {
            int p = i * 16;
            uint a = U32(table, p), b = U32(table, p + 4), c = U32(table, p + 8), d = U32(table, p + 12);
            bool dir = b == 0x7fffff00;
            uint nameOffset = dir ? a : a & 0xffff;
            if (nameOffset >= names.Length) throw new InvalidDataException("RPF7 name offset is outside the name table.");
            int end = Array.IndexOf(names, (byte)0, (int)nameOffset);
            if (end < 0 || end - nameOffset > 1024) throw new InvalidDataException("Unterminated or excessively long RPF7 name.");
            string name = StrictUtf8.GetString(names, (int)nameOffset, end - (int)nameOffset);
            records[i] = new(name, a, b, c, d, BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(p, 8)), dir, !dir && (b & 0x80000000) != 0);
        }
        if (!records[0].Directory) throw new InvalidDataException("RPF7 root is not a directory.");
        var entries = new List<ArchiveEntry>(records.Length);
        var visited = new HashSet<int> { 0 };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(int Index, string Path, int Depth)>();
        pending.Push((0, "", 0));
        while (pending.Count > 0)
        {
            var (parentIndex, parentPath, depth) = pending.Pop();
            if (depth > 64) throw new InvalidDataException("Archive directory nesting is too deep.");
            var parent = records[parentIndex];
            long last = (long)parent.C + parent.D;
            if (last > records.Length) throw new InvalidDataException("RPF7 child range is outside the index.");
            for (long child = parent.C; child < last; child++)
            {
                int i = (int)child;
                if (!visited.Add(i)) throw new InvalidDataException("RPF7 directory index has a cycle or shared child.");
                var record = records[i];
                if (record.Name.Contains('/') || record.Name.Contains('\\')) throw new InvalidDataException("RPF7 entry name contains a path separator.");
                string memberPath = ValidateMemberPath(parentPath + record.Name, record.Directory);
                if (!paths.Add(memberPath)) throw new InvalidDataException("Duplicate or case-colliding RPF7 paths.");
                if (record.Directory)
                {
                    entries.Add(new(memberPath, 0, 0, 0, true, false, false, "Folder", i));
                    pending.Push((i, memberPath + "/", depth + 1));
                    continue;
                }
                long packedSize = (long)((record.Packed >> 16) & 0xffffff);
                long offset = (long)((record.Packed >> 40) & (record.Resource ? 0x7fffffu : 0xffffffu)) * 512;
                long storedSize = record.Resource ? packedSize : packedSize == 0 ? record.C : packedSize;
                long size = record.Resource ? storedSize : record.C;
                bool encrypted = !record.Resource && record.D == 1;
                if (!record.Resource && record.D > 1) throw new InvalidDataException("Unknown RPF7 member protection flag.");
                string? unavailable = null;
                if (record.Resource && packedSize == 0xffffff)
                {
                    ValidateRange(offset, 16, stream.Length, indexEnd);
                    stream.Position = offset;
                    byte[] resourceHeader = new byte[16]; stream.ReadExactly(resourceHeader);
                    storedSize = (long)resourceHeader[7] | ((long)resourceHeader[14] << 8) | ((long)resourceHeader[5] << 16) | ((long)resourceHeader[2] << 24);
                    size = storedSize;
                }
                if (record.Resource && storedSize < 16)
                    unavailable = "Resource without a supported stored length; member extraction is unavailable.";
                if (record.Resource && record.Name.EndsWith(".ysc", StringComparison.OrdinalIgnoreCase))
                    unavailable = "Script resources may carry separate protection. This build does not decode them.";
                ValidateRange(offset, storedSize, stream.Length, indexEnd);
                entries.Add(new(memberPath, storedSize, size, offset, false, encrypted, record.Resource,
                    record.Resource ? "Native resource" : packedSize > 0 ? "Deflate" : "Stored", i) { Limitation = unavailable });
            }
        }
        if (visited.Count != records.Length) throw new InvalidDataException("RPF7 index contains entries not reachable from its root.");
        metadata["Member decoding"] = "Supported (open RPF7)";
        metadata["File count"] = entries.Count(e => !e.IsDirectory).ToString("N0");
        metadata["Resource extraction"] = "Copies the native stored resource, including its resource header";
        string? limitation = entries.Any(e => !e.IsDirectory && !e.CanExtract)
            ? "Some members use protection or an unsupported resource layout. These members cannot be previewed or extracted; other members remain available." : null;
        return new(path, "RPF7", stream.Length, entries, metadata, limitation, header, true);
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static void ValidateRange(long offset, long length, long fileLength, long indexEnd)
    {
        if (length < 0 || offset < 0 || offset > fileLength || length > fileLength - offset || (length > 0 && offset < indexEnd))
            throw new InvalidDataException("An RPF7 member points outside the archive or overlaps its index.");
    }

    public static string ValidateMemberPath(string path, bool directory = false)
    {
        path = path.Replace('\\', '/');
        if (directory) path = path.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Length > 4096)
            throw new InvalidDataException("Archive contains an invalid or absolute member path.");
        var segments = path.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Length > 255 || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new InvalidDataException("Archive contains an unsafe member path: " + path);
            string device = segment.Split('.')[0].ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" || (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '0' and <= '9'))
                throw new InvalidDataException("Archive contains a reserved Windows device name.");
        }
        return string.Join('/', segments);
    }

    internal static byte[] ReadPreview(ArchiveDocument doc, ArchiveEntry entry, int maxBytes, CancellationToken token)
    {
        ValidateEntry(doc, entry);
        if (maxBytes < 1 || maxBytes > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var source = OpenSource(doc, entry, token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[Math.Min(maxBytes, 64 * 1024)];
        long remaining = Math.Min(entry.Size, maxBytes);
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int read = source.Content.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new InvalidDataException("The member is truncated or its decoded length does not match its index.");
            output.Write(buffer, 0, read); remaining -= read;
        }
        return output.ToArray();
    }


    public static byte[] ReadResourceData(ArchiveDocument doc, ArchiveEntry entry, CancellationToken token = default)
    {
        ValidateEntry(doc, entry);
        if (entry.Compression is not ("Recovered texture dictionary" or "Recovered model")
            || entry.Segments is not { Count: > 0 } || entry.Size < 1 || entry.Size > 256L * 1024 * 1024)
            throw new InvalidDataException("The resource is not a validated supported layout or exceeds the 256 MiB viewer limit.");
        byte[] data = new byte[checked((int)entry.Size)];
        using var source = OpenSource(doc, entry, token);
        int written = 0;
        while (written < data.Length)
        {
            token.ThrowIfCancellationRequested();
            int read = source.Content.Read(data, written, Math.Min(65536, data.Length - written));
            if (read == 0) throw new InvalidDataException("The recovered resource is truncated.");
            written += read;
        }
        if (source.Content.ReadByte() != -1) throw new InvalidDataException("The resource exceeds its validated size.");
        return data;
    }

    internal static async Task ExtractAsync(ArchiveDocument doc, IEnumerable<ArchiveEntry> selected, string destination,
        IProgress<ExtractionProgress>? progress, CancellationToken token)
    {
        if (!doc.SupportsMembers) throw new NotSupportedException(doc.Limitation);
        ArchiveEntry[] allSelected = selected.Distinct().ToArray();
        ArchiveEntry[] entries = allSelected.Where(e => !e.IsDirectory).ToArray();
        foreach (var entry in entries) ValidateEntry(doc, entry);
        destination = System.IO.Path.GetFullPath(destination);
        EnsureNoReparsePoints(destination); Directory.CreateDirectory(destination); EnsureNoReparsePoints(destination);
        var targets = entries.Select(e => SafeDestination(destination, e.Path)).ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
            throw new InvalidDataException("Extraction paths collide.");
        if (targets.Any(File.Exists)) throw new IOException("A destination file already exists. Choose a new, empty destination folder.");
        byte[] buffer = new byte[128 * 1024];
        long copied = 0;
        var progressClock = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = -100;
        for (int i = 0; i < entries.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[i]; string target = targets[i];
            string parent = System.IO.Path.GetDirectoryName(target)!;
            EnsureNoReparsePoints(parent); Directory.CreateDirectory(parent); EnsureNoReparsePoints(parent);
            string temporary = System.IO.Path.Combine(parent, ".rpf-preview-" + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                using var source = OpenSource(doc, entry, token);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
                {
                    long length = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int read = await source.Content.ReadAsync(buffer, token);
                        if (read == 0) break;
                        length = checked(length + read);
                        if (length > entry.Size) throw new InvalidDataException("Decoded member exceeds its declared length.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token); copied += read;
                        if (progressClock.ElapsedMilliseconds - lastReport >= 100)
                        {
                            progress?.Report(new(entry.Path, i, entries.Length, copied));
                            lastReport = progressClock.ElapsedMilliseconds;
                        }
                    }
                    if (length != entry.Size) throw new InvalidDataException("Decoded member length does not match the archive index.");
                    await output.FlushAsync(token);
                }
                token.ThrowIfCancellationRequested();
                EnsureNoReparsePoints(parent);
                File.Move(temporary, target, false);
                progress?.Report(new(entry.Path, i + 1, entries.Length, copied));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        foreach (var entry in allSelected.Where(e => e.IsDirectory))
        {
            token.ThrowIfCancellationRequested();
            if (!doc.Entries.Contains(entry)) throw new InvalidDataException("Entry is not from this archive.");
            string folder = SafeDestination(destination, entry.Path);
            EnsureNoReparsePoints(folder); Directory.CreateDirectory(folder);
        }
    }

    private static void ValidateEntry(ArchiveDocument doc, ArchiveEntry entry)
    {
        if (!doc.SupportsMembers || !doc.Entries.Contains(entry)) throw new InvalidDataException("Entry does not belong to a supported archive.");
        if (!entry.CanExtract) throw new NotSupportedException(entry.Limitation ?? (entry.IsEncrypted ? "This member is encrypted. No decryption keys are included." : "Select a file to preview."));
    }

    public static string SafeDestination(string root, string memberPath)
    {
        string validated = ValidateMemberPath(memberPath);
        string prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(prefix, validated.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Member escapes the extraction folder.");
        return full;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (DirectoryInfo? dir = new(System.IO.Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Extraction through symbolic links or junctions is not supported.");
    }

    private sealed class Source(Stream content, IDisposable? owner = null) : IDisposable
    {
        public Stream Content { get; } = content;
        public void Dispose() { Content.Dispose(); owner?.Dispose(); }
    }

    private static Source OpenSource(ArchiveDocument doc, ArchiveEntry entry, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (doc.Format == "RDS5")
        {
            if (new FileInfo(doc.SourcePath).Length != doc.FileSize) throw new InvalidDataException("Texture pool size changed since it was opened.");
            var pool = TtpfReader.Inspect(doc.SourcePath, token);
            var texture = pool.Textures.ElementAtOrDefault(entry.Index);
            if (texture is null || texture.Name != entry.Path || texture.DdsSize != entry.Size || texture.VirtualOffset != entry.Offset)
                throw new InvalidDataException("Texture catalog changed since it was opened.");
            return new(new MemoryStream(TtpfReader.CreateDds(doc.SourcePath, entry.Index, token), false));
        }
        if (doc.Format == "ZIP")
        {
            var zip = ZipFile.OpenRead(doc.SourcePath);
            try
            {
                if (entry.Index >= zip.Entries.Count) throw new InvalidDataException("Archive changed since it was opened.");
                var z = zip.Entries[entry.Index];
                if (ValidateMemberPath(z.FullName) != entry.Path || z.Length != entry.Size || z.CompressedLength != entry.StoredSize)
                    throw new InvalidDataException("Archive changed since it was opened.");
                return new(z.Open(), zip);
            }
            catch { zip.Dispose(); throw; }
        }
        var file = File.Open(doc.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length != doc.FileSize) throw new InvalidDataException("Archive size changed since it was opened.");
            if (entry.Segments is { Count: > 0 } segments)
            {
                foreach (var segment in segments)
                    if (segment.Offset < 0 || segment.StoredSize < 6 || segment.Offset > file.Length || segment.StoredSize > file.Length - segment.Offset)
                        throw new InvalidDataException("Recovered block is outside the archive.");
                return new(new ZlibSegmentsStream(segments, segment =>
                {
                    file.Position = segment.Offset;
                    return new BoundedReadStream(file, segment.StoredSize, true);
                }, file));
            }
            file.Position = entry.Offset;
            Stream bounded = new BoundedReadStream(file, entry.StoredSize);
            return new(entry.Compression is "Deflate" or "Recovered raw deflate shader" ? new DeflateStream(bounded, CompressionMode.Decompress)
                : entry.Compression == "Recovered zlib" ? new ZLibStream(bounded, CompressionMode.Decompress) : bounded);
        }
        catch { file.Dispose(); throw; }
    }

    private sealed class BoundedReadStream(Stream inner, long remaining, bool leaveOpen = false) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read;
        }
        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]); remaining -= read; return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            int read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], token); remaining -= read; return read;
        }
        protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
