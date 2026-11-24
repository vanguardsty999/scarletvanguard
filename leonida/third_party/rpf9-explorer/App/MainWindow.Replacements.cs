using Microsoft.Win32;
using RpfPreview.Core;
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
    private ReplacementWorkspace? _replacementWorkspace;
    private ReplacementTarget? _shownReplacementTarget;
    private readonly HashSet<(string Path, long Offset)> _replacementEntryKeys = [];
    private readonly Dictionary<string, HashSet<(string Path, long Offset, int Index)>> _replacementSourceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<(string Path, long Offset)>> _replacementParentKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _writingPoolCopy;
    private Task? _poolCopyTask;
    private bool _waitingForPoolClose, _closeAfterPoolCopy;

    private ReplacementTarget? CurrentReplacementTarget()
    {
        if (_modelAssemblyActive) return null;
        if (_shownReplacementTarget != null) return _shownReplacementTarget;
        if (_document == null || FilesGrid.SelectedItem is not FileRow row || !row.Entry.CanExtract && row.Entry.Compression != "RDS5 texture") return null;
        var entry = row.Entry;
        int choice = (AssetSelector.SelectedItem as AssetChoice)?.Index ?? 0;
        return entry.Compression switch {
            "Recovered model" => new(entry.Path, entry.Offset, "model"),
            "Recovered texture dictionary" => new(entry.Path, entry.Offset, "texture", choice, _textureDictionary?.Textures.FirstOrDefault(t => t.Index == choice)?.AssetKey?.ToString("X16")),
            "RDS5 texture" => new(entry.Path, entry.Offset, "texture", entry.Index,
                _ttpfInfo?.Textures.ElementAtOrDefault(entry.Index)?.AssetKey.ToString("X16")),
            "Recovered AWC" => new(entry.Path, entry.Offset, "audio", choice),
            _ => new(entry.Path, entry.Offset, "binary")
        };
    }

    private bool HasStagedReplacement(ArchiveEntry entry) => _replacementWorkspace != null && _replacementEntryKeys.Contains((entry.Path, entry.Offset));

    private void RefreshReplacementKeySet()
    {
        _replacementEntryKeys.Clear();
        if (_replacementWorkspace != null)
            foreach (var replacement in _replacementWorkspace.Entries) _replacementEntryKeys.Add((replacement.Target.EntryPath, replacement.Target.EntryOffset));
        _replacementSourceKeys.Clear();
        _replacementParentKeys.Clear();
        foreach (var project in _sourceProjects.Values)
        {
            var edits = project.Entries;
            _replacementSourceKeys[project.SourcePath] = edits.Select(r => (r.Target.EntryPath, r.Target.EntryOffset, r.Target.SubresourceIndex)).ToHashSet();
            _replacementParentKeys[project.SourcePath] = edits.Select(r => (r.Target.EntryPath, r.Target.EntryOffset)).ToHashSet();
        }
    }

    private bool HasAssetReplacement(WorkspaceAsset asset)
        => _replacementSourceKeys.TryGetValue(asset.SourcePath, out var keys) && (asset.IsTextureChild
            ? keys.Contains((asset.Entry.Path, asset.Entry.Offset, asset.Texture!.Index))
            : _replacementParentKeys[asset.SourcePath].Contains((asset.Entry.Path, asset.Entry.Offset)));

    private void UpdateReplacementControls()
    {
        if (StageReplacementButton == null) return;
        var target = CurrentReplacementTarget();
        StageReplacementButton.IsEnabled = !_busy && target != null;
        ReplacementControls.Visibility = target != null && _replacementWorkspace?.Find(target) != null ? Visibility.Visible : Visibility.Collapsed;
        var staged = target == null ? null : _replacementWorkspace?.Find(target);
        BuildNativeTextureButton.Visibility = target?.Kind == "texture" && staged?.Format.Equals("DDS", StringComparison.OrdinalIgnoreCase) == true
            && FilesGrid.SelectedItem is FileRow { Entry.Compression: "Recovered texture dictionary" } ? Visibility.Visible : Visibility.Collapsed;
        ExportPoolCopyButton.Visibility = target?.Kind == "texture" && staged?.Format.Equals("DDS", StringComparison.OrdinalIgnoreCase) == true
            && _document?.Format == "RDS5" && _ttpfInfo?.Textures.FirstOrDefault(t => t.Index == target.SubresourceIndex)?.SupportsNativeReplacement == true ? Visibility.Visible : Visibility.Collapsed;
        string? projectPath = _sessionProject?.ProjectPath ?? _replacementWorkspace?.ProjectPath;
        ReplacementProjectText.Text = projectPath == null ? "" : Path.GetFileNameWithoutExtension(projectPath);
        ReplacementProjectText.ToolTip = projectPath;
    }

    private void RefreshReplacementAnnotations()
    {
        if (_replacementWorkspace != null) _sourceProjects[_replacementWorkspace.SourcePath] = _replacementWorkspace;
        RefreshReplacementKeySet();
        foreach (var row in _allRows) row.HasReplacement = row.Asset != null ? HasAssetReplacement(row.Asset) : HasStagedReplacement(row.Entry);
        _thumbnailCache.Clear();
        if (ThumbnailsCheckBox.IsChecked == true) ConfigureGallery(_rows.ToArray(), false);
        UpdateReplacementControls();
    }

    private bool CreateReplacementProject()
    {
        if (_document == null) return false;
        if (_sessionProject == null && !CreateSavedWorkspace()) return false;
        _replacementWorkspace = _sessionProject!.ForSource(_document.SourcePath, true);
        _sourceProjects[_document.SourcePath] = _replacementWorkspace!;
        RefreshReplacementAnnotations();
        StatusText.Text = "Replacement project created · imported assets are staged separately; RPF9 archive writing remains unavailable";
        return true;
    }

    private async void NewReplacementProject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try { if (CreateSavedWorkspace()) await PreviewSelectedFileAsync(); }
        catch (Exception ex) { StatusText.Text = "Could not create project: " + ex.Message; }
    }

    private async void OpenReplacementProject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "Open replacement project", Filter = "RPF replacement project|*.rpfproject", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await OpenReplacementProjectAsync(dialog.FileName);
    }

    private async Task<bool> OpenReplacementProjectAsync(string path)
    {
        if (_busy) return false;
        try
        {
            var workspace = await Task.Run(() => ReplacementWorkspace.Load(path));
            if (!await OpenArchiveAsync(workspace.SourcePath)) return false;
            _replacementWorkspace = workspace;
            _sourceProjects[workspace.SourcePath] = workspace;
            RefreshReplacementAnnotations();
            var first = workspace.Entries.FirstOrDefault();
            if (first != null)
            {
                FilesGrid.SelectedItem = _rows.FirstOrDefault(r => r.Entry.Path == first.Target.EntryPath && r.Entry.Offset == first.Target.EntryOffset);
                if (_activePreview != null) await _activePreview;
                if (FilesGrid.SelectedItem != null)
                {
                    FilesGrid.ScrollIntoView(FilesGrid.SelectedItem);
                    await ShowReplacementAsync(first.Target);
                }
            }
            StatusText.Text = $"Opened {workspace.Entries.Count:N0} staged replacements · source archive has not been rewritten";
            return true;
        }
        catch (Exception ex) { StatusText.Text = "Could not open replacement project: " + ex.Message; return false; }
    }

    private async void StageReplacement_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _document == null) return;
        try
        {
            if (_activePreview != null) await _activePreview;
            var target = CurrentReplacementTarget();
            if (target == null) return;
            var dialog = new OpenFileDialog { Title = "Choose a staged " + target.Kind + " replacement", CheckFileExists = true,
                Filter = target.Kind switch {
                    "model" => "Wavefront OBJ model|*.obj",
                    "texture" => "Textures and images|*.dds;*.png;*.jpg;*.jpeg",
                    "audio" => "Wave audio|*.wav",
                    _ => "Replacement file|*.*"
                } };
            if (dialog.ShowDialog(this) != true) return;
            if (_replacementWorkspace == null && !CreateReplacementProject()) return;
            SetBusy(true);
            var workspace = _replacementWorkspace!;
            await Task.Run(() => workspace.Import(target, dialog.FileName, "Staged " + target.Kind));
            _sessionProject?.Save(CurrentWorkspaceView());
            RefreshReplacementAnnotations();
            StatusText.Text = "Replacement staged · original archive unchanged · use Original / Staged replacement to compare";
            SetBusy(false);
            await ShowReplacementAsync(target);
        }
        catch (Exception ex) { StatusText.Text = "Could not stage replacement: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private async void PreviewReplacement_Click(object sender, RoutedEventArgs e)
    {
        try { var target = CurrentReplacementTarget(); if (target != null) await ShowReplacementAsync(target); }
        catch (Exception ex) { StatusText.Text = "Could not preview replacement: " + ex.Message; }
    }

    private async void PreviewOriginal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = CurrentReplacementTarget();
            await PreviewSelectedFileAsync();
            if (target is { SubresourceIndex: >= 0 } && AssetSelector.ItemsSource != null && target.Kind is "texture" or "audio")
            {
                var choice = AssetSelector.Items.OfType<AssetChoice>().FirstOrDefault(c => c.Index == target.SubresourceIndex);
                if (choice != null) { AssetSelector.SelectedItem = choice; if (_activePreview != null) await _activePreview; }
            }
            UpdateReplacementControls();
        }
        catch (Exception ex) { StatusText.Text = "Could not preview original: " + ex.Message; }
    }

    private async void RemoveReplacement_Click(object sender, RoutedEventArgs e)
    {
        var target = CurrentReplacementTarget();
        if (target == null || _replacementWorkspace == null) return;
        try
        {
            _replacementWorkspace.Remove(target);
            _shownReplacementTarget = null;
            RefreshReplacementAnnotations();
            await PreviewSelectedFileAsync();
            StatusText.Text = "Staged replacement removed · original archive unchanged";
        }
        catch (Exception ex) { StatusText.Text = "Could not remove replacement: " + ex.Message; }
    }

    private async Task ShowReplacementAsync(ReplacementTarget target)
    {
        if (_replacementWorkspace?.Find(target) is not ReplacementRecord replacement) return;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose();
        var cancellation = _previewCancellation = new CancellationTokenSource();
        ClosePlayer();
        _shownReplacementTarget = target;
        ImagePreview.Source = null; ImageTab.IsEnabled = false; ModelTab.IsEnabled = false;
        TextTab.Header = "Details";
        PreviewNameText.Text = replacement.OriginalFileName + " · staged replacement";
        PreviewMetaText.Text = $"{replacement.Format} · {FormatSize(replacement.Length)} · source archive unchanged";
        TextPreview.Text = $"Staged replacement\n\nTarget: {target.EntryPath}\nType: {target.Kind}\nSubresource: {target.SubresourceIndex}\nImported: {replacement.OriginalFileName}\nSHA-256: {replacement.Sha256}\n\nStored in a separate replacement project. This asset has not been written into the game archive. RPF9 packing and directory/integrity updates remain undecoded.";
        PreviewTabs.SelectedItem = TextTab;
        try
        {
            string path = _replacementWorkspace.AssetPath(replacement);
            _activePreview = PreviewReplacementFileAsync(path, target.Kind, cancellation.Token, replacement.OriginalFileName);
            await _activePreview;
            if (!cancellation.IsCancellationRequested) PreviewFootnoteText.Text = "Staged replacement preview · source archive unchanged";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) { PreviewFootnoteText.Text = "Staged asset saved; preview unavailable: " + ex.Message; PreviewTabs.SelectedItem = TextTab; } }
        finally { UpdateReplacementControls(); }
    }

    private async Task PreviewReplacementFileAsync(string path, string kind, CancellationToken token, string displayName)
    {
        if (kind == "model")
        {
            var model = await Task.Run(() => ObjModelReader.Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(displayName)), token);
            token.ThrowIfCancellationRequested(); _decodedModel = model;
            ModelPreview.ShowMeshes(model.Meshes.Select(m => new MeshPreviewPart(m.Name, m.Positions, m.Indices) { TexCoords = m.TexCoords }));
            ModelTab.IsEnabled = true; PreviewTabs.SelectedItem = ModelTab;
            PreviewMetaText.Text += $" · {ModelPreview.PresentedVertices:N0} vertices · {ModelPreview.PresentedTriangles:N0} triangles";
            return;
        }
        if (kind == "texture")
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            ImageSource image;
            if (bytes.AsSpan(0, Math.Min(4, bytes.Length)).SequenceEqual("DDS "u8))
            {
                var info = DdsTextureReader.Inspect(bytes);
                PreviewMetaText.Text += $" · {info.Width:N0} × {info.Height:N0} · {info.Format} · {info.Mips} mips · {info.Shape}";
                TextPreview.Text += $"\n\nDDS preview: {info.PreviewWidth} × {info.PreviewHeight}; first face/slice. All imported mip/slice bytes remain in the staged file.\n{info.PreviewNote}";
                var pixels = await Task.Run(() => DdsTextureReader.DecodeTexturePixels(bytes), token);
                image = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra32, pixels.Width * 4);
                image.Freeze();
            }
            else image = await Task.Run(() => DecodeImage(bytes), token);
            token.ThrowIfCancellationRequested(); ImagePreview.Source = image;
            ImageTab.Header = "Replacement"; ImageTab.IsEnabled = true; PreviewTabs.SelectedItem = ImageTab;
            return;
        }
        if (kind == "audio") { await PreviewReplacementAudioAsync(path, token); return; }
        byte[] preview = await Task.Run(() => { using var input = File.OpenRead(path); byte[] bytes = new byte[(int)Math.Min(input.Length, 256 * 1024)]; input.ReadExactly(bytes); return bytes; }, token);
        token.ThrowIfCancellationRequested(); HexPreview.Text = FormatHex(preview.AsSpan(0, Math.Min(preview.Length, 65536)));
        if (LooksLikeText(preview)) { TextPreview.Text += "\n\nReplacement contents:\n\n" + PlainText(preview); PreviewTabs.SelectedItem = TextTab; }
        else PreviewTabs.SelectedItem = HexTab;
    }

    private async Task PreviewReplacementAudioAsync(string path, CancellationToken token)
    {
        string output = Path.Combine(Path.GetTempPath(), "RpfPreview-staged-" + Guid.NewGuid().ToString("N") + ".wav");
        bool retained = false;
        try
        {
            var start = new ProcessStartInfo(FindPreviewTool("ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "-v", "error", "-xerror", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", path, "-t", "120", "-map", "0:a:0", "-map_metadata", "-1", "-c:a", "pcm_s16le", "-f", "wav", output }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("The audio preview decoder did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            var errors = process.StandardError.ReadToEndAsync(); var report = process.StandardOutput.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { } throw; }
            string error = (await errors).Trim(); await report;
            if (process.ExitCode != 0) throw new IOException(error.Length > 0 ? error : "The staged audio could not be decoded.");
            token.ThrowIfCancellationRequested();
            ImagePreview.Source = DrawWaveform(output, out int rate, out int channels);
            AudioPlaybackSession? session = null;
            session = new AudioPlaybackSession(output,
                (position, playing, ended) => { if (_audioPlayer == session && !token.IsCancellationRequested) UpdateAudioState(position, playing, ended); },
                message => { if (_audioPlayer == session && !token.IsCancellationRequested) PreviewFootnoteText.Text = message; });
            _audioPlayer = session; retained = true;
            session.Volume(VolumeSlider.Value); session.Loop = LoopCheckBox.IsChecked == true;
            await session.Opened.WaitAsync(TimeSpan.FromSeconds(10), token);
            _updatingSeek = true; SeekSlider.Minimum = 0; SeekSlider.Maximum = Math.Max(.01, session.Duration); SeekSlider.Value = 0; _updatingSeek = false;
            _audioDecodeReport = JsonSerializer.Serialize(new { codec = "Staged WAV", sampleRate = rate, channels, previewLimited = session.Duration >= 119.99 });
            _audioPreviewLimited = session.Duration >= 119.99; _audioTrimUnresolved = false;
            ImageTab.Header = "Replacement audio"; ImageTab.IsEnabled = true; PreviewTabs.SelectedItem = ImageTab;
            VideoControls.Visibility = Visibility.Visible; VideoControls.IsEnabled = true;
            StepVideoButton.IsEnabled = FullscreenButton.IsEnabled = false;
            VolumeSlider.IsEnabled = true; AudioLabel.Text = "Volume";
            PreviewMetaText.Text += $" · {rate:N0} Hz · {channels} channels · {session.Duration:0.##} s preview";
            UpdateAudioState(0, false, false);
        }
        finally { if (!retained) { try { File.Delete(output); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } } }
    }

    private async void BuildNativeTexture_Click(object sender, RoutedEventArgs e)
    {
        var target = CurrentReplacementTarget();
        if (_busy || _document == null || target == null || _replacementWorkspace?.Find(target) is not ReplacementRecord replacement
            || FilesGrid.SelectedItem is not FileRow { Entry.Compression: "Recovered texture dictionary" } row) return;
        try
        {
            var document = _document; string assetPath = _replacementWorkspace.AssetPath(replacement);
            SetBusy(true);
            var result = await Task.Run(() => {
                byte[] resource = ArchiveReader.ReadResourceData(document, row.Entry);
                int split = row.Entry.ResourceSystemBytes ?? checked((int)row.Entry.Segments![0].Size);
                return NativeTextureReplacement.Stage(resource.AsSpan(0, split), resource.AsSpan(split), target.SubresourceIndex, File.ReadAllBytes(assetPath));
            });
            SetBusy(false);
            var dialog = new SaveFileDialog { Title = "Save staged decoded texture resource",
                Filter = "Decoded native resource bundle|*.r9texdata", DefaultExt = ".r9texdata",
                FileName = Path.GetFileNameWithoutExtension(replacement.OriginalFileName) + "-native-replacement.r9texdata" };
            if (dialog.ShowDialog(this) != true) return;
            string output = Path.GetFullPath(dialog.FileName);
            if (new[] { document.SourcePath, assetPath, _replacementWorkspace.ProjectPath }.Any(p => string.Equals(output, p, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Choose a separate output file.");
            await File.WriteAllBytesAsync(output, result.ResourceData);
            TextPreview.Text += $"\n\nNative resource staged: {output}\nSystem boundary: {result.ResourceSystemBytes:N0} bytes\nChanged graphics bytes: {result.ChangedGraphicsBytes:N0}\nOriginal graphics SHA-256: {result.SourceGraphicsSha256}\nStaged graphics SHA-256: {result.StagedGraphicsSha256}\nThe source archive was not rewritten.";
            StatusText.Text = $"Native texture resource saved · {result.ChangedGraphicsBytes:N0} graphics bytes changed · original RPF9 unchanged";
        }
        catch (Exception ex) { StatusText.Text = "Could not build native texture: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private async void ExportPoolCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _document?.Format != "RDS5") return;
        var target = CurrentReplacementTarget();
        if (target == null || _replacementWorkspace?.Find(target) is not ReplacementRecord replacement) return;
        var dialog = new SaveFileDialog { Title = "Export experimental modified texture-pool copy",
            Filter = "RDS5 texture pool|*.ttpf", DefaultExt = ".ttpf", FileName = Path.GetFileNameWithoutExtension(_document.SourcePath) + "-modified.ttpf" };
        if (dialog.ShowDialog(this) != true) return;
        try { await ExportPoolCopyAsync(target, replacement, dialog.FileName); }
        catch (OperationCanceledException) { StatusText.Text = "Pool copy canceled · original source unchanged"; }
        catch (Exception ex) { StatusText.Text = "Could not export pool copy: " + ex.Message; }
    }

    private async Task<Rds5ReplacementResult> ExportPoolCopyAsync(ReplacementTarget target, ReplacementRecord replacement, string destination)
    {
        if (_document?.Format != "RDS5" || _replacementWorkspace == null) throw new InvalidOperationException("No source texture pool is open.");
        string input = _replacementWorkspace.AssetPath(replacement), source = _document.SourcePath;
        string output = Path.GetFullPath(destination);
        if (new[] { source, input, _replacementWorkspace.ProjectPath }.Any(p => string.Equals(output, p, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Choose a new output file separate from the source and project.");
        var drive = new DriveInfo(Path.GetPathRoot(output)!);
        if (drive.IsReady && drive.AvailableFreeSpace < _document.FileSize + new FileInfo(input).Length)
            throw new IOException("The output drive does not have enough free space for this pool copy.");
        _workCancellation = new CancellationTokenSource(); _writingPoolCopy = true;
        SetBusy(true, true); WorkProgress.IsIndeterminate = false; WorkProgress.Maximum = 1; WorkProgress.Value = 0;
        try
        {
            var progress = new Progress<Rds5WriteProgress>(p => {
                WorkProgress.Value = Math.Clamp((double)p.CompletedBytes / Math.Max(1, p.TotalBytes), 0, 1);
                StatusText.Text = p.Phase;
            });
            var task = Task.Run(() => Rds5TextureWriter.ExportReplacementCopy(source, target.SubresourceIndex,
                File.ReadAllBytes(input), output, _workCancellation.Token, progress), _workCancellation.Token);
            _poolCopyTask = task;
            var result = await task;
            TextPreview.Text += $"\n\nExperimental modified pool copy: {result.DestinationPath}\nChanged chunks: {result.ChangedChunks:N0}\nChanged decoded bytes: {result.ChangedDecodedBytes:N0}\n{result.ValidationNote}";
            PreviewFootnoteText.Text = "Experimental pool copy saved · original metadata retained · game integrity/compatibility unverified";
            StatusText.Text = "Modified texture-pool copy saved · original unchanged · game compatibility unverified";
            return result;
        }
        finally { _poolCopyTask = null; _workCancellation?.Dispose(); _workCancellation = null; _writingPoolCopy = false; SetBusy(false); }
    }

    private async void WaitForPoolCopyOnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAfterPoolCopy) return;
        if (_waitingForPoolClose) { e.Cancel = true; return; }
        if (_poolCopyTask is not { IsCompleted: false } task) return;
        e.Cancel = true; _waitingForPoolClose = true;
        _workCancellation?.Cancel();
        StatusText.Text = "Canceling pool copy and cleaning temporary output…";
        try { await task; } catch (Exception) {   }
        _closeAfterPoolCopy = true; Close();
    }
}
