using Microsoft.Win32;
using RpfPreview.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RpfPreview.App;

public partial class MainWindow
{
    private sealed record AssetChoice(int Index, string Label);
    private RageTextureDictionary? _textureDictionary;
    private TtpfInfo? _ttpfInfo;
    private int _ttpfTextureIndex = -1;
    private byte[]? _textureSystem, _textureGraphics;
    private RageModel? _decodedModel;
    private AwcInfo? _audioInfo;
    private ArchiveDocument? _audioDocument;
    private ArchiveEntry? _audioEntry;
    private AudioPlaybackSession? _audioPlayer;
    private CancellationTokenSource? _audioTrackCancellation;
    private bool _loadingAssetChoice;
    private bool _loadingTextureSlice;
    private int _selectedTextureIndex = -1, _textureRequestVersion;
    private string? _audioDecodeReport;
    private bool _audioTrimUnresolved, _audioPreviewLimited;

    private void ResetAssetPreview()
    {
        ClearModelAssemblyState();
        _audioTrackCancellation?.Cancel();
        _audioTrackCancellation?.Dispose();
        _audioTrackCancellation = null;
        _audioPlayer?.Dispose(); _audioPlayer = null;
        _textureDictionary = null; _textureSystem = _textureGraphics = null;
        _ttpfInfo = null; _ttpfTextureIndex = -1;
        _audioInfo = null; _audioDocument = null; _audioEntry = null; _decodedModel = null;
        _audioDecodeReport = null;
        _audioTrimUnresolved = _audioPreviewLimited = false;
        _selectedTextureIndex = -1; _textureRequestVersion++;
        if (AssetSliceSelector != null) { _loadingTextureSlice = true; AssetSliceSelector.ItemsSource = null; AssetSliceSelector.Visibility = Visibility.Collapsed; _loadingTextureSlice = false; }
        if (AssetSelectorBar != null) AssetSelectorBar.Visibility = Visibility.Collapsed;
        if (AssetSelector != null) { _loadingAssetChoice = true; AssetSelector.ItemsSource = null; _loadingAssetChoice = false; }
        if (ModelTab != null) { ModelTab.IsEnabled = false; ModelPreview.Clear(); }
        if (StepVideoButton != null) StepVideoButton.IsEnabled = true;
        if (FullscreenButton != null) FullscreenButton.IsEnabled = true;
    }

    private async Task PreviewTexturesAsync(ArchiveDocument document, ArchiveEntry entry, CancellationToken token, int textureIndex = 0)
    {
        var bytes = await Task.Run(() => ArchiveReader.ReadResourceData(document, entry, token), token);
        token.ThrowIfCancellationRequested();
        int split = entry.ResourceSystemBytes ?? checked((int)entry.Segments![0].Size);
        if (split <= 0 || split >= bytes.Length) throw new InvalidDataException("Invalid resource section boundary.");
        _textureSystem = bytes[..split]; _textureGraphics = bytes[split..];
        _textureDictionary = RageTextureDecoder.Inspect(_textureSystem, _textureGraphics)
            ?? throw new InvalidDataException("This resource does not match the supported texture dictionary layout.");
        TextTab.Header = "Details";
        TextPreview.Text = $"Recovered texture dictionary\n\n{_textureDictionary.Textures.Count:N0} textures\nSystem section: {FormatSize(split)}\nGraphics section: {FormatSize(bytes.Length - split)}\n\nThe texture names are embedded in the resource. The original archive member name is unknown.\n\nExport DDS creates a standard, detiled texture with its mipmaps. Extract selected saves the decoded system and graphics sections as an application-specific .r9texdata recovery file; no native RSC9 header is invented.";
        HexPreview.Text = FormatHex(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
        ImageTab.Header = "Texture"; ImageTab.IsEnabled = true;
        AssetExportButton.Content = "Export DDS…"; AssetExportButton.IsEnabled = true; AssetExportButton.ToolTip = "Export all source mip levels and array/volume slices";
        AssetSelectorBar.Visibility = Visibility.Visible;
        _loadingAssetChoice = true;
        AssetSelector.ItemsSource = _textureDictionary.Textures.Select(t => new AssetChoice(t.Index, $"{t.Name} · {t.Width} × {t.Height} · {t.Format} · {t.Shape}")).ToArray();
        AssetSelector.SelectedIndex = textureIndex;
        _loadingAssetChoice = false;
        await ShowTextureAsync(textureIndex, token);
    }

    private async Task PreviewTtpfAsync(ArchiveDocument document, ArchiveEntry entry, CancellationToken token)
    {
        var pool = await Task.Run(() => TtpfReader.Inspect(document.SourcePath, token), token);
        token.ThrowIfCancellationRequested();
        if (_document != document) return;
        var texture = pool.Textures.Single(t => t.Index == entry.Index);
        _ttpfInfo = pool; _ttpfTextureIndex = texture.Index;
        TextTab.Header = "Details";
        string sourceSize = texture.Dimension == 3 ? $"{texture.Width:N0} × {texture.Height:N0} × {texture.Depth:N0}" : $"{texture.Width:N0} × {texture.Height:N0}";
        string exportSize = !texture.CanExtract ? "Unavailable" : texture.Dimension == 3 ? $"{texture.ExportWidth:N0} × {texture.ExportHeight:N0} × {texture.ExportDepth:N0}" : $"{texture.ExportWidth:N0} × {texture.ExportHeight:N0}";
        string exportedMips = texture.CanExtract ? $"{texture.BaseMipLevel} through {texture.BaseMipLevel + texture.Mips - 1}" : "Unavailable";
        string shapeSuffix = texture.Dimension != 1 || texture.Depth > 1 ? $" · {texture.Shape}" : "";
        TextPreview.Text = $"Native streaming texture\n\nSource: {sourceSize} · {texture.Format}{shapeSuffix}\nDDS export: {exportSize}\nSource mip levels: {texture.SourceMips}\nExported source mip levels: {exportedMips}\nVirtual offset: 0x{texture.VirtualOffset:X}\nNative asset key: {texture.AssetKey:X16}\nOpaque metadata token: {texture.Id}\n\nGenerated filenames are not original archive names. Exact native asset keys resolve supported model texture references. Export DDS and Extract selected both create a standard, detiled DDS file using the available mip levels.\n\n{texture.PreviewNote}";
        AssetSelectorBar.Visibility = Visibility.Visible;
        _loadingAssetChoice = true;
        AssetSelector.ItemsSource = new[] { new AssetChoice(texture.Index, $"{texture.ExportWidth} × {texture.ExportHeight} · {texture.Format}{shapeSuffix}" + (texture.BaseMipLevel > 0 ? $" · source mip {texture.BaseMipLevel}" : "")) };
        AssetSelector.SelectedIndex = 0; _loadingAssetChoice = false;
        _loadingTextureSlice = true;
        int sliceCount = texture.CanPreview ? (texture.Dimension == 3 ? Math.Max(1, texture.Depth >> texture.PreviewMipLevel) : texture.Depth) : 0;
        AssetSliceSelector.ItemsSource = sliceCount > 1
            ? Enumerable.Range(0, sliceCount).Select(i => texture.Dimension == 2 ? $"Cube {i / 6 + 1}, face {i % 6 + 1}" : $"Slice {i + 1} of {sliceCount}").ToArray()
            : null;
        AssetSliceSelector.SelectedIndex = sliceCount > 1 ? 0 : -1;
        AssetSliceSelector.Visibility = sliceCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        _loadingTextureSlice = false;
        AssetExportButton.Content = "Export DDS…"; AssetExportButton.IsEnabled = texture.CanExtract;
        AssetExportButton.ToolTip = texture.PartialMips ? $"Export {exportSize}, starting at source mip {texture.BaseMipLevel}. Unsupported source mip levels are not included." : "Export the validated native texture as DDS";
        PreviewMetaText.Text = $"{exportSize} DDS · {texture.Format} · {texture.Mips} exported mip levels{shapeSuffix}" + (texture.BaseMipLevel > 0 ? $" · source mip {texture.BaseMipLevel}, original {sourceSize}" : "");
        if (!texture.CanPreview)
        {
            PreviewFootnoteText.Text = texture.Limitation ?? "DDS export is available; this format cannot be displayed yet.";
            return;
        }
        await ShowTtpfSliceAsync(pool, texture, token);
    }

    private async Task ShowTtpfSliceAsync(TtpfInfo pool, TtpfTextureInfo texture, CancellationToken token)
    {
        int slice = Math.Max(0, AssetSliceSelector.SelectedIndex), version = ++_textureRequestVersion;
        var pixels = await Task.Run(() => TtpfReader.DecodeTexturePixels(pool.SourcePath, texture.Index, slice, token), token);
        if (token.IsCancellationRequested || _ttpfInfo != pool || _ttpfTextureIndex != texture.Index || version != _textureRequestVersion) return;
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra32, pixels.Width * 4);
        bitmap.Freeze(); ImagePreview.Source = bitmap;
        ImageTab.Header = "Texture"; ImageTab.IsEnabled = true; PreviewTabs.SelectedItem = ImageTab;
        PreviewFootnoteText.Text = texture.PreviewNote ?? "Streaming texture · native AMD tile layout decoded";
    }
    private async Task ShowTextureAsync(int index, CancellationToken token)
    {
        if (_textureDictionary == null || _textureSystem == null || _textureGraphics == null) return;
        var system = _textureSystem; var graphics = _textureGraphics; var dictionary = _textureDictionary;
        var texture = dictionary.Textures.Single(t => t.Index == index);
        if (_selectedTextureIndex != index)
        {
            _selectedTextureIndex = index; _loadingTextureSlice = true;
            int depth = texture.Dimension == 3 ? Math.Max(1, texture.Depth >> texture.PreviewMipLevel) : texture.Depth;
            AssetSliceSelector.ItemsSource = Enumerable.Range(0, depth).Select(i => texture.Dimension == 2 ? $"Cube {i / 6 + 1}, face {i % 6 + 1}" : $"Slice {i + 1} of {depth}").ToArray();
            AssetSliceSelector.SelectedIndex = 0; AssetSliceSelector.Visibility = depth > 1 ? Visibility.Visible : Visibility.Collapsed;
            _loadingTextureSlice = false;
        }
        int slice = Math.Max(0, AssetSliceSelector.SelectedIndex), version = ++_textureRequestVersion;
        var pixels = await Task.Run(() => RageTextureDecoder.DecodeTexturePixels(system, graphics, index, slice), token);
        if (token.IsCancellationRequested || _textureDictionary != dictionary || version != _textureRequestVersion) return;
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra32, pixels.Width * 4);
        bitmap.Freeze(); ImagePreview.Source = bitmap;
        PreviewMetaText.Text = $"{texture.Width:N0} × {texture.Height:N0} · {texture.Format} · {texture.MipCount} mip levels · {texture.Shape}";
        PreviewFootnoteText.Text = texture.PreviewNote ?? $"{texture.Name} · detiled from the source graphics data";
        PreviewTabs.SelectedItem = ImageTab;
    }

    private async Task PreviewModelAsync(ArchiveDocument document, ArchiveEntry entry, CancellationToken token)
    {
        var decoded = await Task.Run(() => {
            var bytes = ArchiveReader.ReadResourceData(document, entry, token);
            int split = entry.ResourceSystemBytes ?? checked((int)entry.Segments![0].Size);
            if (split <= 0 || split >= bytes.Length) throw new InvalidDataException("Invalid model section boundary.");
            var model = RageModelDecoder.Decode(bytes.AsSpan(0, split), bytes.AsSpan(split), entry.ResourceDrawableOffset)
                ?? throw new InvalidDataException("The resource does not contain a supported validated model.");
            return (Model: model, System: bytes.AsMemory(0, split), Graphics: bytes.AsMemory(split));
        }, token);
        token.ThrowIfCancellationRequested();
        var model = decoded.Model;
        _decodedModel = model;
        ModelPreview.ShowMeshes(model.Meshes.Select(m => new MeshPreviewPart(m.Name, m.Positions, m.Indices) { TexCoords = m.TexCoords, VertexColors = m.VertexColors }));
        ModelTab.IsEnabled = true; PreviewTabs.SelectedItem = ModelTab;
        TextTab.Header = "Details";
        TextPreview.Text = $"Native model: {model.Name}\n\n{(model.IsDictionaryChild ? "Selected drawable dictionary component · original coordinates retained\n" : "")}{model.Meshes.Count:N0} mesh parts\n{ModelPreview.PresentedVertices:N0} vertices\n{ModelPreview.PresentedTriangles:N0} triangles\n{model.Meshes.Count(m => m.TexCoords != null):N0} mesh parts with validated UV coordinates\n{model.Materials?.Count ?? 0:N0} native material records\n\nGeometry comes from the source vertex and meshlet index buffers. The preview uses a neutral material. Material texture IDs are retained, but unresolved bindings are not rendered. Export OBJ saves validated geometry and supported UV coordinates.\n\n{model.PresentationNote}";
        PreviewMetaText.Text = $"{model.Name} · {ModelPreview.PresentedVertices:N0} vertices · {ModelPreview.PresentedTriangles:N0} triangles";
        PreviewFootnoteText.Text = model.PresentationNote ?? "Geometry preview · neutral material · drag to orbit, right-drag to pan, wheel to zoom";
        try
        {
            var textures = await ResolveModelTexturesAsync(document, entry, model, decoded.System, decoded.Graphics, token);
            if (token.IsCancellationRequested || _decodedModel != model || _document != document) return;
            if (textures.Count > 0)
            {
                ModelPreview.ShowMeshes(model.Meshes.Select(m => new MeshPreviewPart(m.Name, m.Positions, m.Indices) {
                    TexCoords = m.TexCoords, VertexColors = m.VertexColors, Texture = m.ShaderIndex is int shader && textures.TryGetValue(shader, out var image) ? image : null }), false);
                PreviewFootnoteText.Text = $"{ModelPreview.PresentedTexturedMeshes:N0} textured mesh parts · {_modelMissingTextureMaterials:N0} unresolved materials" + (_modelLayerPreviewMaterials > 0 ? $" · {_modelLayerPreviewMaterials} base/secondary-layer previews" : "");
                TextPreview.Text += $"\n\n{ModelPreview.PresentedTexturedMeshes:N0} mesh parts display a color texture matched by exact native asset key. Embedded resources and available lower mips are used where supported. Layered materials use a labelled color-layer preview; native blending and other shader effects are not simulated.";
            }
            else PreviewFootnoteText.Text = ModelPreview.PresentedVertexColoredMeshes > 0
                ? $"{ModelPreview.PresentedVertexColoredMeshes} raw vertex-color mesh previews · palette, region tint and native alpha effects are not applied"
                : model.PresentationNote ?? "Geometry preview · no supported diffuse texture binding is available";
            TextPreview.Text += "\n\nAutomatic texture lookup:\n" + _modelTextureDetails;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            if (!token.IsCancellationRequested) PreviewFootnoteText.Text = "Geometry preview · streaming texture lookup unavailable: " + ex.Message;
        }
    }

    private async Task PreviewAudioAsync(ArchiveDocument document, ArchiveEntry entry, CancellationToken token)
    {
        var info = await Task.Run(() => AwcInfo.Inspect(document.SourcePath, entry.Offset, entry.StoredSize), token)
            ?? throw new InvalidDataException("The recovered AWC container could not be validated.");
        token.ThrowIfCancellationRequested();
        _audioInfo = info; _audioDocument = document; _audioEntry = entry;
        TextTab.Header = "Details";
        TextPreview.Text = $"Recovered AWC audio bank\n\n{info.DeclaredStreams:N0} declared streams\n{info.Streams.Count:N0} streams with audio format records\n{info.Streams.Count(s => s.SupportsPreview):N0} playable streams or music channels\n\nGenerated archive filename; IDs come from the AWC records. PCM, Opus and XMA previews are bounded to 120 seconds. Opus encoder pre-skip and final trimming are not established. Extract selected preserves the original AWC container.";
        HexPreview.Text = FormatHex(document.ReadPreview(entry, 4096, token));
        ImageTab.Header = "Audio"; ImageTab.IsEnabled = true;
        AssetSelectorBar.Visibility = Visibility.Visible;
        AssetSliceSelector.Visibility = Visibility.Collapsed;
        AssetExportButton.Content = "Save WAV…"; AssetExportButton.IsEnabled = false; AssetExportButton.ToolTip = "Save the decoded audio preview";
        _loadingAssetChoice = true;
        AssetSelector.ItemsSource = info.Streams.Select(s => new AssetChoice(s.Index,
            (s.IsStreamed ? $"Music channel {s.MusicChannel + 1}" : $"Stream {s.Index + 1}") + $" · {s.Id:X8} · {s.Channels} ch · {s.DurationSeconds:0.##} s" + (s.SupportsPreview ? "" : $" · codec {s.Codec:X2} unavailable"))).ToArray();
        var first = info.Streams.FirstOrDefault(s => s.SupportsPreview);
        AssetSelector.SelectedItem = AssetSelector.Items.Cast<AssetChoice>().FirstOrDefault(c => c.Index == first?.Index);
        _loadingAssetChoice = false;
        if (first == null)
        {
            PreviewTabs.SelectedItem = TextTab;
            PreviewFootnoteText.Text = "Bank recovered; its audio codec is not yet supported for playback.";
            return;
        }
        await DecodeAudioTrackAsync(first.Index, token);
    }

    private async Task DecodeAudioTrackAsync(int index, CancellationToken selectionToken)
    {
        if (_audioInfo == null || _audioDocument == null || _audioEntry == null) return;
        _audioTrackCancellation?.Cancel(); _audioTrackCancellation?.Dispose();
        var cancellation = _audioTrackCancellation = CancellationTokenSource.CreateLinkedTokenSource(selectionToken);
        var token = cancellation.Token;
        _audioPlayer?.Dispose(); _audioPlayer = null;
        AssetExportButton.IsEnabled = false; VideoControls.Visibility = Visibility.Collapsed;
        var stream = _audioInfo.Streams.Single(s => s.Index == index);
        if (!stream.SupportsPreview) throw new NotSupportedException($"Audio codec 0x{stream.Codec:X2} is not supported by the current preview decoder.");
        var source = _audioDocument.SourcePath; var entry = _audioEntry;
        var helper = FindPreviewTool(stream.Codec == 5 ? "ffmpeg.exe" : "opus-preview.exe");
        string output = Path.Combine(Path.GetTempPath(), "RpfPreview-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        string? xma = null;
        bool retained = false;
        PreviewFootnoteText.Text = "Decoding audio preview…";
        try
        {
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (stream.Codec == 5)
            {
                xma = output + ".xma.wav";
                await Task.Run(() => AwcRecovery.WriteXmaWave(source, entry.Offset, entry.StoredSize, index, xma), token);
                token.ThrowIfCancellationRequested();
                foreach (var argument in new[] { "-v", "error", "-xerror", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", xma, "-t", "120", "-map_metadata", "-1", "-bitexact", "-c:a", "pcm_s16le", "-f", "wav", output }) start.ArgumentList.Add(argument);
            }
            else foreach (var argument in new[] { source, "0x" + entry.Offset.ToString("X"), "0x" + entry.StoredSize.ToString("X"), index.ToString(), output, "120" }) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("The audio decoder could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            var report = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { }
                throw;
            }
            var error = (await errors).Trim(); _audioDecodeReport = await report;
            if (process.ExitCode != 0 || !File.Exists(output)) throw new IOException(error.Length > 0 ? error : "The audio decoder did not create a PCM preview.");
            if (stream.Codec == 5) _audioDecodeReport = JsonSerializer.Serialize(new { codec = "XMA2", sampleRate = stream.SampleRate, channels = stream.Channels, declaredSamples = stream.Samples, previewLimited = stream.DurationSeconds > 120, decoder = "FFmpeg native XMA2" });
            token.ThrowIfCancellationRequested();
            ImagePreview.Source = DrawWaveform(output, out int pcmRate, out int pcmChannels);
            using (var decoded = JsonDocument.Parse(_audioDecodeReport!))
                _audioPreviewLimited = decoded.RootElement.TryGetProperty("previewLimited", out var limit) && limit.GetBoolean();
            _audioTrimUnresolved = stream.Codec == 0x0B;
            AudioPlaybackSession? session = null;
            session = new AudioPlaybackSession(output,
                (position, playing, ended) => { if (_audioPlayer == session && !token.IsCancellationRequested) UpdateAudioState(position, playing, ended); },
                message => { if (_audioPlayer == session && !token.IsCancellationRequested) PreviewFootnoteText.Text = "Audio playback failed: " + message; });
            _audioPlayer = session; retained = true;
            session.Volume(VolumeSlider.Value); session.Loop = LoopCheckBox.IsChecked == true;
            await session.Opened.WaitAsync(TimeSpan.FromSeconds(10), token);
            token.ThrowIfCancellationRequested();
            _updatingSeek = true; SeekSlider.Minimum = 0; SeekSlider.Maximum = Math.Max(.01, session.Duration); SeekSlider.Value = 0; _updatingSeek = false;
            VideoControls.Visibility = Visibility.Visible; VideoControls.IsEnabled = true;
            StepVideoButton.IsEnabled = false; FullscreenButton.IsEnabled = false;
            VolumeSlider.IsEnabled = true; AudioLabel.Text = "Volume";
            AssetExportButton.IsEnabled = true; PreviewTabs.SelectedItem = ImageTab;
            var codec = stream.Codec switch { 0x0B => "Opus", 5 => "XMA2", 0 => "PCM", _ => $"Codec {stream.Codec:X2}" };
            PreviewMetaText.Text = $"{codec} · {pcmRate:N0} Hz · {pcmChannels} channel(s) · {session.Duration:0.###} s decoded preview";
            UpdateAudioState(0, false, false);
        }
        finally
        {
            if (!retained) { try { File.Delete(output); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            if (xma != null) { try { File.Delete(xma); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }
    }
    private void UpdateAudioState(double position, bool playing, bool ended)
    {
        _playing = playing; PlayPauseButton.Content = playing ? "Pause" : "Play";
        if (!_draggingSeek) { _updatingSeek = true; SeekSlider.Value = position; _updatingSeek = false; UpdateAudioClock(position); }
        PreviewFootnoteText.Text = (ended ? "End of preview" : playing ? "Playing" : "Paused")
            + (_audioPreviewLimited ? " · first 120 seconds" : "")
            + (_audioTrimUnresolved ? " · Opus packet preview; encoder trim unresolved" : " · decoded PCM preview")
            + (_shownReplacementTarget != null ? " · staged replacement" : "");
    }
    private void UpdateAudioClock(double position)
    {
        if (_audioPlayer == null) return;
        PlaybackTimeText.Text = $"{Clock(position)} / {Clock(_audioPlayer.Duration)}";
        SeekSlider.ToolTip = $"{position:0.###} seconds";
    }
    private async void AssetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAssetChoice || AssetSelector.SelectedItem is not AssetChoice choice) return;
        var token = _previewCancellation?.Token ?? CancellationToken.None;
        try
        {
            if (_ttpfInfo != null) return;
            _activePreview = _textureDictionary != null ? ShowTextureAsync(choice.Index, token) : DecodeAudioTrackAsync(choice.Index, token);
            await _activePreview;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) { PreviewFootnoteText.Text = ex.Message; PreviewTabs.SelectedItem = TextTab; } }
        finally { UpdateReplacementControls(); }
    }
    private async void AssetSliceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTextureSlice) return;
        var token = _previewCancellation?.Token ?? CancellationToken.None;
        try
        {
            if (_ttpfInfo != null && _ttpfTextureIndex >= 0)
            {
                _activePreview = ShowTtpfSliceAsync(_ttpfInfo, _ttpfInfo.Textures.Single(t => t.Index == _ttpfTextureIndex), token);
                await _activePreview;
                return;
            }
            if (_textureDictionary == null || _selectedTextureIndex < 0) return;
            _activePreview = ShowTextureAsync(_selectedTextureIndex, token);
            await _activePreview;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) PreviewFootnoteText.Text = ex.Message; }
    }
    private static string FindPreviewTool(string name) => new[] {
        Path.Combine(AppContext.BaseDirectory, "Tools", name),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Tools", name))
    }.Concat(name == "ffmpeg.exe" ? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => p.Length > 0).Select(p => Path.Combine(p, name)) : Array.Empty<string>())
        .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("The preview tool is missing: Tools\\" + name);

    private void AssetExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool poolTexture = _ttpfInfo != null && _ttpfTextureIndex >= 0;
            bool texture = poolTexture || _textureDictionary != null && AssetSelector.SelectedItem is AssetChoice;
            string name = poolTexture ? _ttpfInfo!.Textures[_ttpfTextureIndex].Name : texture ? _textureDictionary!.Textures.Single(t => t.Index == ((AssetChoice)AssetSelector.SelectedItem).Index).Name : "Audio preview";
            name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            var dialog = new SaveFileDialog { Title = texture ? "Export texture" : "Save decoded audio preview", Filter = texture ? "DDS texture|*.dds" : "PCM wave audio|*.wav", FileName = name, DefaultExt = texture ? ".dds" : ".wav" };
            if (dialog.ShowDialog(this) != true) return;
            if ((_document != null && string.Equals(Path.GetFullPath(dialog.FileName), _document.SourcePath, StringComparison.OrdinalIgnoreCase)) || _modelAssemblySourcePaths.Contains(Path.GetFullPath(dialog.FileName))) throw new IOException("Choose a filename different from every source archive.");
            if (poolTexture) File.WriteAllBytes(dialog.FileName, TtpfReader.CreateDds(_ttpfInfo!.SourcePath, _ttpfTextureIndex));
            else if (texture) File.WriteAllBytes(dialog.FileName, RageTextureDecoder.CreateDds(_textureSystem!, _textureGraphics!, ((AssetChoice)AssetSelector.SelectedItem).Index));
            else if (_audioPlayer != null) File.Copy(_audioPlayer.PcmPath, dialog.FileName, true);
            else throw new InvalidOperationException("No decoded audio preview is available.");
            StatusText.Text = "Saved " + dialog.FileName;
        }
        catch (Exception ex) { StatusText.Text = "Export failed: " + ex.Message; }
    }
    private void ModelExport_Click(object sender, RoutedEventArgs e)
    {
        if (_decodedModel == null) return;
        var dialog = new SaveFileDialog { Title = _modelAssemblyActive ? "Export experimental static assembly geometry" : "Export decoded model geometry", Filter = "Wavefront OBJ|*.obj", DefaultExt = ".obj", FileName = Path.GetFileNameWithoutExtension(_decodedModel.Name) };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if ((_document != null && string.Equals(Path.GetFullPath(dialog.FileName), _document.SourcePath, StringComparison.OrdinalIgnoreCase)) || _modelAssemblySourcePaths.Contains(Path.GetFullPath(dialog.FileName))) throw new IOException("Choose a filename different from every source archive.");
            File.WriteAllText(dialog.FileName, RageModelDecoder.CreateObj(_decodedModel)); StatusText.Text = "Saved " + dialog.FileName;
        }
        catch (Exception ex) { StatusText.Text = "Export failed: " + ex.Message; }
    }

    private static ImageSource DrawWaveform(string path, out int sampleRate, out int channels)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || bytes.Length > 256 * 1024 * 1024 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("The audio helper returned an unsupported PCM layout.");
        int dataOffset = -1, dataLength = 0; bool pcm = false;
        sampleRate = channels = 0;
        for (int p = 12; p <= bytes.Length - 8;)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4));
            if (chunkLength > bytes.Length - p - 8) throw new InvalidDataException("The PCM chunk is truncated.");
            int size = (int)chunkLength;
            if (bytes.AsSpan(p, 4).SequenceEqual("fmt "u8) && size >= 16)
            {
                pcm = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 8)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 22)) == 16;
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 10));
                sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 12)));
            }
            if (bytes.AsSpan(p, 4).SequenceEqual("data"u8)) { dataOffset = p + 8; dataLength = size; break; }
            p = checked(p + 8 + size + (size & 1));
        }
        if (!pcm || dataOffset < 0 || dataLength < 2) throw new InvalidDataException("The preview is not nonempty PCM16 audio.");
        const int width = 1024, height = 256;
        int count = dataLength / 2;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 24, 27)), null, new Rect(0, 0, width, height));
            context.DrawLine(new Pen(Brushes.DimGray, 1), new Point(0, height / 2), new Point(width, height / 2));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(121, 175, 221)), 1);
            for (int x = 0; x < width; x++)
            {
                int start = (int)((long)x * count / width), end = (int)((long)(x + 1) * count / width); int peak = 0;
                for (int i = start; i < end; i++) peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + i * 2))));
                double amplitude = peak / 32768.0 * (height / 2 - 6);
                context.DrawLine(pen, new Point(x + .5, height / 2 - amplitude), new Point(x + .5, height / 2 + amplitude));
            }
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); result.Render(drawing); result.Freeze(); return result;
    }
}
