namespace RpfPreview.Core;

public sealed record PayloadIndexProgress(double Fraction, string Message);
public sealed record PayloadIndexResult(int Models, int TextureDictionaries, int AudioBanks, int Shaders = 0);


public static class PayloadIndexBuilder
{
    public static int BuildModels(string source, IProgress<PayloadIndexProgress>? progress = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        source = Path.GetFullPath(source);
        long length = new FileInfo(source).Length;
        if (length < 64) throw new InvalidDataException("The source is too short.");
        byte[] header = new byte[64];
        using (var file = File.OpenRead(source)) file.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("9FPR"u8)) throw new NotSupportedException("Model discovery supports the observed little-endian RPF9 format.");
        var cached = RecoveryIndexCache.Load("models", source, length, header);
        if (cached?.IsComplete == true)
        {
            progress?.Report(new(1, $"Model index ready · {cached.Entries.Count:N0} models"));
            return cached.Entries.Count;
        }
        var previous = cached ?? RecoveryIndexCache.LoadLegacyModels(source, length, header);
        var models = previous?.Entries.ToList() ?? new List<ArchiveEntry>();
        var known = models.Select(entry => entry.Offset).ToHashSet();
        var offsets = Rpf9ModelRecovery.FindCandidateOffsets(source, progress:
            new InlineProgress<long>(position => progress?.Report(new(.35 * position / length,
                $"Discovering model containers · {position:N0}/{length:N0} bytes"))), cancellationToken: token);
        int accepted = 0;
        try
        {
            for (int i = 0; i < offsets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!known.Contains(offsets[i]))
                {
                    var entries = Rpf9ModelRecovery.RecoverAtOffsets(source, length, new[] { offsets[i] }, models.Count, token);
                    if (models.Count + entries.Count > 500_000) throw new InvalidDataException("The model index exceeds 500,000 entries. Index a smaller source.");
                    models.AddRange(entries); accepted += entries.Count;
                }
                if (i % 50 == 0 || i + 1 == offsets.Count)
                    progress?.Report(new(.35 + .65 * (i + 1) / Math.Max(1, offsets.Count),
                        $"Indexing models · {i + 1:N0}/{offsets.Count:N0} containers · {models.Count:N0} models"));
                if (i > 0 && i % 1000 == 0 && accepted > 0)
                {
                    RecoveryIndexCache.Save("models", source, models,
                        $"Dictionary-aware model scan in progress · {i + 1:N0}/{offsets.Count:N0} containers validated.");
                    accepted = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (accepted > 0) RecoveryIndexCache.Save("models", source, models,
                "Dictionary-aware model scan canceled. Validated models are retained; discovery will resume on reopen.");
            throw;
        }
        token.ThrowIfCancellationRequested();
        var ordered = models.OrderBy(entry => entry.Offset).ThenBy(entry => entry.ResourceDrawableOffset ?? -1)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal).Select((entry, index) => entry with { Index = index }).ToArray();
        RecoveryIndexCache.Save("models", source, ordered,
            $"Full {length:N0}-byte model-container scan · {ordered.Length:N0} supported models including drawable dictionary children. Unknown layouts and the original RPF9 directory remain undecoded.", complete: true);
        progress?.Report(new(1, $"Model index ready · {ordered.Length:N0} models"));
        return ordered.Length;
    }

    public static PayloadIndexResult Build(string source, IProgress<PayloadIndexProgress>? progress = null, CancellationToken token = default)
    {
        source = Path.GetFullPath(source);
        long length = new FileInfo(source).Length;
        if (length < 64) throw new InvalidDataException("The source is too short.");
        using (var file = File.OpenRead(source))
        {
            Span<byte> magic = stackalloc byte[4]; file.ReadExactly(magic);
            if (!magic.SequenceEqual("9FPR"u8)) throw new NotSupportedException("Full payload scanning currently supports the observed little-endian RPF9 format.");
        }
        void Report(int stage, double part, string text) => progress?.Report(new((stage + Math.Clamp(part, 0, 1)) / 4, text));
        int models = BuildModels(source, new InlineProgress<PayloadIndexProgress>(p => Report(0, p.Fraction, p.Message)), token);

        var textureOffsets = Rpf9TextureRecovery.FindCandidateOffsets(source, 0, null,
            new InlineProgress<long>(position => Report(1, .45 * position / length, $"Scanning texture headers · {position:N0}/{length:N0} bytes")), token);
        var textures = new List<ArchiveEntry>();
        for (int i = 0; i < textureOffsets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            textures.AddRange(Rpf9TextureRecovery.RecoverAtOffsets(source, length, new[] { textureOffsets[i] }, textures.Count, token));
            if (i % 100 == 0) Report(1, .45 + .55 * i / Math.Max(1, textureOffsets.Count), $"Validating textures · {i:N0}/{textureOffsets.Count:N0} candidates · {textures.Count:N0} supported dictionaries");
        }
        RecoveryIndexCache.Save("textures", source, textures, $"Full {length:N0}-byte page+16 scan; {textures.Count:N0} supported dictionaries; original directory remains undecoded.");

        var audio = AwcRecovery.RecoverFull(source,
            new InlineProgress<long>(position => Report(2, (double)position / length, $"Scanning audio banks · {position:N0}/{length:N0} bytes")), token);
        token.ThrowIfCancellationRequested();
        AwcRecovery.WriteCache(source, audio);
        var shaders = Rpf9ShaderRecovery.RecoverRange(source, length, 0, length, 0,
            new InlineProgress<long>(position => Report(3, (double)position / length, $"Scanning native shaders · {position:N0}/{length:N0} bytes")), token);
        token.ThrowIfCancellationRequested();
        RecoveryIndexCache.Save("shaders", source, shaders, $"Full {length:N0}-byte raw-wrapper scan; {shaders.Count:N0} validated native shader containers. Original directory remains undecoded.");
        Report(3, 1, $"Index ready · {models:N0} models · {textures.Count:N0} texture dictionaries · {audio.Count:N0} audio banks · {shaders.Count:N0} shaders");
        return new(models, textures.Count, audio.Count, shaders.Count);
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
