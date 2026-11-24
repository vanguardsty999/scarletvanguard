using System.Diagnostics;
using System.Text.Json;
using RpfPreview.Core;

if (args.Length < 3) throw new ArgumentException("Usage: IndexBuilder models|proof-models <source> <report-directory-or-proof-json>");
string source = Path.GetFullPath(args[1]);
var watch = Stopwatch.StartNew();
var length = new FileInfo(source).Length;
if (args[0] == "models-refresh")
{
    string output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
    int last = -1;
    int count = PayloadIndexBuilder.BuildModels(source, new InlineProgress<PayloadIndexProgress>(p =>
    {
        int step = (int)(p.Fraction * 100);
        if (step == last) return; last = step; Console.WriteLine(p.Message);
    }));
    using var file = File.OpenRead(source); byte[] header = new byte[64]; file.ReadExactly(header);
    var cache = RecoveryIndexCache.Load("models", source, length, header)!;
    var report = new { source, count, cache.IsComplete, elapsedSeconds = watch.Elapsed.TotalSeconds,
        dictionaryChildren = cache.Entries.Count(e => e.ResourceDrawableOffset != null), cache.Coverage,
        lucia = cache.Entries.Where(e => e.Name.Contains("cs_lucia_", StringComparison.OrdinalIgnoreCase)).ToArray() };
    File.WriteAllText(Path.Combine(output, "model-refresh.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { count, cache.IsComplete, elapsedSeconds = watch.Elapsed.TotalSeconds }));
}
else if (args[0] is "models" or "proof-models" or "retry-models" or "fragments")
{
    IReadOnlyList<long> candidates;
    var entries = new List<ArchiveEntry>();
    int allCandidateCount = 0;
    string output;
    if (args[0] == "proof-models")
    {
        using var json = JsonDocument.Parse(File.ReadAllText(args[2]));
        candidates = json.RootElement.EnumerateArray().Select(e => e.GetProperty("Offset").GetInt64()).ToArray();
        output = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
    }
    else if (args[0] is "retry-models" or "fragments")
    {
        output = Path.GetFullPath(args[2]);
        using var file = File.OpenRead(source); byte[] header = new byte[64]; file.ReadExactly(header);
        var cached = RecoveryIndexCache.Load("models", source, length, header) ?? throw new InvalidDataException("A prior model index is required for rejected-candidate validation.");
        entries.AddRange(cached.Entries);
        var known = entries.Select(e => e.Offset).ToHashSet();
        var all = args[0] == "fragments"
            ? Rpf9ModelRecovery.FindFragmentCandidateOffsets(source, progress: new InlineProgress<long>(p => Console.WriteLine($"Fragment scan {100.0 * p / length:0.00}%"))).ToArray()
            : JsonSerializer.Deserialize<long[]>(File.ReadAllText(Path.Combine(output, "full-model-candidates.json")))!;
        if (args[0] == "fragments") File.WriteAllText(Path.Combine(output, "fragment-candidates.json"), JsonSerializer.Serialize(all));
        allCandidateCount = all.Length; candidates = all.Where(o => !known.Contains(o)).ToArray();
        Console.WriteLine($"Retaining {known.Count:N0} validated ranges; rechecking {candidates.Count:N0} rejected candidates with the current decoder.");
    }
    else
    {
        output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
        long last = -1;
        var progress = new InlineProgress<long>(position => {
            long step = position / (1024L * 1024 * 1024);
            if (step == last) return; last = step;
            Console.WriteLine($"Scanned {position:N0}/{length:N0} bytes ({100.0 * position / length:0.0}%)");
        });
        candidates = Rpf9ModelRecovery.FindCandidateOffsets(source, progress: progress);
        File.WriteAllText(Path.Combine(output, "full-model-candidates.json"), JsonSerializer.Serialize(candidates));
    }
    Console.WriteLine($"Candidate headers: {candidates.Count:N0}");
    if (allCandidateCount == 0) allCandidateCount = candidates.Count;
    for (int i = 0; i < candidates.Count; i++)
    {


        foreach (var entry in Rpf9ModelRecovery.RecoverAtOffsets(source, length, new[] { candidates[i] }, entries.Count)) entries.Add(entry);
        if ((i + 1) % 1000 == 0)
        {
            Console.WriteLine($"Validated {i + 1:N0}/{candidates.Count:N0} candidates; {entries.Count:N0} recovered models");
            if (args[0] is "retry-models" or "fragments") RecoveryIndexCache.Save("models", source, entries,
                $"Full page+16 candidate scan; decoder refresh in progress, {i + 1:N0}/{candidates.Count:N0} prior rejections checked. Original directory is not decoded.");
        }
    }
    entries = entries.OrderBy(e => e.Offset).Select((e, i) => e with { Index = i }).ToList();
    string coverage = args[0] == "fragments"
        ? $"Full {length:N0}-byte drawable scan plus full fragment-header scan ({allCandidateCount:N0} fragment candidates); {entries.Count:N0} supported resources. Original directory remains undecoded."
        : args[0] != "proof-models"
        ? $"Full {length:N0}-byte drawable candidate scan ({allCandidateCount:N0} candidates), retaining previously validated fragment resources; {entries.Count:N0} total supported models. Other layouts and original directory remain undecoded."
        : "Independently validated research candidates only; not a full archive scan or original directory.";
    RecoveryIndexCache.Save("models", source, entries, coverage);
    var report = new { source, sourceLength = length, originalDirectoryDecoded = false, coverage, candidates = candidates.Count, recovered = entries.Count, elapsedSeconds = watch.Elapsed.TotalSeconds, cache = RecoveryIndexCache.CachePath("models", source), entries };
    string reportName = args[0] == "proof-models" ? "proof-model-index.json" : args[0] == "fragments" ? "fragment-index.json" : "full-model-index.json";
    File.WriteAllText(Path.Combine(output, reportName), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IgnoreReadOnlyProperties = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { recovered = entries.Count, elapsedSeconds = watch.Elapsed.TotalSeconds, cache = RecoveryIndexCache.CachePath("models", source) }));
}
else if (args[0] == "shaders")
{
    string output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
    long last = -1;
    var entries = Rpf9ShaderRecovery.RecoverRange(source, length, 0, length, 0,
        new InlineProgress<long>(position => { long step = position / (32L * 1024 * 1024); if (step == last) return; last = step; Console.WriteLine($"Shader scan {100.0 * position / length:0.0}%"); }));
    string coverage = $"Full {length:N0}-byte native-wrapper scan; {entries.Count:N0} shader containers with exact DEFLATE completion and DXBC bounds. Original RPF9 directory remains undecoded.";
    RecoveryIndexCache.Save("shaders", source, entries, coverage);
    File.WriteAllText(Path.Combine(output, "full-shader-index.json"), JsonSerializer.Serialize(new { source, sourceLength = length, originalDirectoryDecoded = false, coverage, count = entries.Count, elapsedSeconds = watch.Elapsed.TotalSeconds, entries }, new JsonSerializerOptions { WriteIndented = true, IgnoreReadOnlyProperties = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { shaders = entries.Count, elapsedSeconds = watch.Elapsed.TotalSeconds }));
}
else if (args[0] == "pool-input")
{
    byte[] dds = TtpfReader.CreateDds(source, 0);
    var info = DdsTextureReader.Inspect(dds);
    dds[info.DataOffset + 15] ^= 1;
    File.WriteAllBytes(Path.GetFullPath(args[2]), dds);
    Console.WriteLine(JsonSerializer.Serialize(new { bytes = dds.Length, info.Width, info.Height, info.Mips }));
}
else throw new ArgumentException("Unknown index mode.");

sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
