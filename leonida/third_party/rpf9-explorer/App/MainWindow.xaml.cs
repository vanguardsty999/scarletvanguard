using Microsoft.Win32;
using RpfPreview.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RpfPreview.App;

public partial class MainWindow : Window
{
    private ArchiveDocument? _document;
    private CancellationTokenSource? _workCancellation;
    private CancellationTokenSource? _previewCancellation;
    private Task? _activePreview;
    private readonly string[] _arguments;
    private string? _selectedFolder;
    private bool _busy;
    private bool _loading;
    private bool IsRecovery => _document?.Metadata.ContainsKey("Recovery mode") == true;
    private readonly RangeObservableCollection<FileRow> _rows = [];
    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        OpenCommand = new RelayCommand(() => PickArchive(), () => !_busy);
        SaveCommand = new RelayCommand(() => SaveWorkspace_Click(this, new RoutedEventArgs()), () => !_busy && _documents.Count > 0);
        InitializeComponent();
        DataContext = this;
        FilesGrid.ItemsSource = _rows;
        Closing += WaitForPoolCopyOnClosing;
        Closed += (_, _) => { _previewCancellation?.Cancel(); CancelThumbnails(); _searchTimer?.Stop(); ClosePlayer(); _workCancellation?.Cancel(); };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        int selectFlag = Array.IndexOf(_arguments, "--select");
        var paths = _arguments.Take(selectFlag < 0 ? _arguments.Length : selectFlag).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        bool opened = paths.Length == 1 ? await OpenArchiveAsync(paths[0]) : paths.Length > 1 && await OpenSourcesAsync(paths.SelectMany(p => Directory.Exists(p) ? DirectorySources(p) : new[] { p }), false);
        if (opened)
        {
            var select = Array.IndexOf(_arguments, "--select");
            if (select >= 0 && select + 1 < _arguments.Length)
            {
                FilesGrid.SelectedItem = _rows.FirstOrDefault(r => r.Entry.Path == _arguments[select + 1]);
                if (FilesGrid.SelectedItem != null) FilesGrid.ScrollIntoView(FilesGrid.SelectedItem);
            }
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => PickArchive();
    private async void PickArchive()
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "Add archives or texture pools", Filter = "Archives and texture pools|*.rpf;*.zip;*.ttpf|All files|*.*", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) == true) await OpenSourcesAsync(dialog.FileNames, true);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            if (paths.Length == 1 && Path.GetExtension(paths[0]).ToLowerInvariant() is ".rpfproject" or ".rpfworkspace") { await OpenArchiveAsync(paths[0]); return; }
            try { await OpenSourcesAsync(paths.SelectMany(p => Directory.Exists(p) ? DirectorySources(p) : new[] { p }), true, paths.FirstOrDefault(Directory.Exists)); }
            catch (Exception ex) { StatusText.Text = "Could not load dropped sources: " + ex.Message; }
        }
    }

    private async Task<bool> OpenArchiveAsync(string path)
    {
        if (Path.GetExtension(path).Equals(".rpfworkspace", StringComparison.OrdinalIgnoreCase)) return await OpenSavedWorkspaceAsync(path);
        if (Path.GetExtension(path).Equals(".rpfproject", StringComparison.OrdinalIgnoreCase)) return await OpenReplacementProjectAsync(path);
        if (Directory.Exists(path)) return await OpenSourcesAsync(DirectorySources(path), false, path);
        return await OpenSourcesAsync(new[] { path }, false);
    }

    private void BuildFolders() => BuildWorkspaceTree();

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_loading) NavigateWorkspace((e.NewValue as TreeViewItem)?.Tag);
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) ScheduleSearch(); }
    private void AvailableOnly_Changed(object sender, RoutedEventArgs e) { if (!_loading) RefreshFiles(); }

    private void RefreshFiles()
    {
        if (FilesGrid == null || _assetIndex == null) return;
        var text = SearchBox.Text.Trim(); var query = AssetSearchQuery.Parse(text);
        bool gallery = ThumbnailsCheckBox.IsChecked == true;
        string type = gallery ? "image" : SelectedAssetType;
        bool global = text.Length > 0 && SearchAllSourcesCheckBox.IsChecked == true;
        IEnumerable<FileRow> rows = _allRows;
        if (!global)
        {
            if (_scopeSource != null) rows = rows.Where(r => r.Asset?.Document == _scopeSource);
            if (_scopeContainer != null) rows = rows.Where(r => r.Asset?.IsTextureChild == true && r.Entry == _scopeContainer);
            else if (_selectedFolder != null) rows = rows.Where(r => r.Entry.Path.StartsWith(_selectedFolder + "/", StringComparison.OrdinalIgnoreCase));
        }
        if (_scopeContainer == null && text.Length == 0 && type == "all") rows = rows.Where(r => r.Asset?.IsTextureChild != true);
        if (type != "all") rows = rows.Where(r => r.Asset != null && AssetSearchQuery.TypeMatches(r.Asset.Kind, type));
        if (AvailableOnlyCheckBox.IsChecked == true) rows = rows.Where(r => r.Asset?.Available == true);
        if (text.Length > 0) rows = rows.Where(r => r.Asset != null && query.Matches(r.Asset));
        var filtered = rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.SourceLabel, StringComparer.OrdinalIgnoreCase).ToArray();
        _rows.Replace(filtered);
        FolderPathText.Text = global ? "Search all loaded sources" : (_scopeSource == null ? "All loaded sources" : Path.GetFileName(_scopeSource.SourcePath))
            + (_selectedFolder == null ? "" : " / " + _selectedFolder) + (_scopeContainer == null ? "" : " / " + _scopeContainer.Name);
        UpFolderButton.IsEnabled = _scopeSource != null || _selectedFolder != null || _scopeContainer != null;
        FileCountText.Text = $"{filtered.Length:N0} assets · {filtered.Select(r => r.Asset?.SourcePath).Distinct().Count():N0} sources";
        ConfigureGallery(filtered, true); UpdateExtractButtons();
        bool unavailable = filtered.Length == 0 && text.Length == 0 && _scopeSource?.SupportsMembers == false;
        UnsupportedPanel.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        if (unavailable) { FilesGrid.Visibility = ImageGallery.Visibility = Visibility.Collapsed; UnsupportedDetailText.Text = _scopeSource!.Limitation; }
    }

    private async void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await PreviewSelectedFileAsync();

    private async Task PreviewSelectedFileAsync()
    {
        _shownReplacementTarget = null;
        UpdateExtractButtons();
        if (_loading) return;
        _previewCancellation?.Cancel();
        if (FilesGrid.SelectedItem is not FileRow row || _document == null) { ClosePlayer(); ResetPreview(); return; }
        ClosePlayer();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation = _previewCancellation = new CancellationTokenSource();
        var entry = row.Entry;
        if (row.Asset != null) SetActiveDocument(row.Asset.Document);
        var document = _document!;
        PreviewNameText.Text = row.DisplayName;
        PreviewNameText.ToolTip = entry.Path;
        PreviewMetaText.Text = $"{entry.Kind} · {FormatSize(entry.Size)} · {entry.Compression}";
        PreviewFootnoteText.Text = "Reading a bounded preview…";
        ImagePreview.Source = null;
        ImageTab.IsEnabled = false;
        ImageTab.Header = "Image";
        TextTab.Header = "Text";
        _activePreview = null;
        TextPreview.Text = "Loading preview…";
        HexPreview.Text = "";
        PreviewTabs.SelectedItem = TextTab;
        try
        {
            if (entry.Compression == "RDS5 texture")
            {
                _activePreview = PreviewTtpfAsync(document, entry, cancellation.Token); await _activePreview; return;
            }
            if (!entry.CanExtract) throw new NotSupportedException(entry.Limitation ?? "This file is encrypted. Its contents cannot be previewed without a compatible decoder and key.");
            if (document.Format == "RPF9" && entry.Compression.StartsWith("Recovered Bink", StringComparison.Ordinal)
                && entry.Extension is ".bk2" or ".bik")
            {
                _activePreview = PreviewRecoveredVideoAsync(document, entry, cancellation.Token);
                await _activePreview;
                return;
            }
            if (entry.Compression == "Recovered texture dictionary")
            {
                _activePreview = PreviewTexturesAsync(document, entry, cancellation.Token, row.Asset?.Texture?.Index ?? 0); await _activePreview; return;
            }
            if (entry.Compression == "Recovered model")
            {
                _activePreview = PreviewModelAsync(document, entry, cancellation.Token); await _activePreview; return;
            }
            if (entry.Compression == "Recovered AWC")
            {
                _activePreview = PreviewAudioAsync(document, entry, cancellation.Token); await _activePreview; return;
            }
            var isImage = entry.Extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".dds";
            var isMetadata = entry.Extension == ".psin";
            var isShader = entry.Compression == "Recovered raw deflate shader";
            var limit = (isImage || isMetadata || isShader) && entry.Size <= 16 * 1024 * 1024 ? 16 * 1024 * 1024 : 256 * 1024;
            var bytes = await Task.Run(() => document.ReadPreview(entry, limit, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || _document != document) return;
            HexPreview.Text = FormatHex(bytes.AsSpan(0, Math.Min(bytes.Length, 65536)));
            if (isShader)
            {
                TextPreview.Text = ShaderPreview.Describe(bytes); TextTab.Header = "Structure";
                PreviewTabs.SelectedItem = TextTab; PreviewFootnoteText.Text = "Compiled native shader · static container inspection · no execution"; return;
            }
            if (isMetadata)
            {
                TextPreview.Text = PsinPreview.Describe(bytes);
                TextTab.Header = "Structure";
                PreviewTabs.SelectedItem = TextTab;
                PreviewFootnoteText.Text = "Complete metadata container · generated filename · not a decompiled script or rendered model";
                return;
            }
            var looksText = LooksLikeText(bytes);
            TextPreview.Text = looksText ? PlainText(bytes) : $"{entry.Kind}\n\nThis file does not appear to contain plain text. Switch to Hex to inspect its bytes.\n\nSpecialized game textures, models and audio require a format-specific viewer.";
            PreviewTabs.SelectedItem = looksText ? TextTab : HexTab;
            if (isImage && entry.Size <= limit && bytes.Length <= limit)
            {
                BitmapSource bitmap = entry.Extension == ".dds" ? PixelBitmap(DdsTextureReader.DecodeTexturePixels(bytes)) : DecodeImage(bytes);
                ImagePreview.Source = bitmap;
                ImageTab.IsEnabled = true;
                PreviewTabs.SelectedItem = ImageTab;
                PreviewMetaText.Text += $" · {bitmap.PixelWidth} × {bitmap.PixelHeight} preview";
            }
            var limited = bytes.Length >= limit && entry.Size > bytes.Length;
            PreviewFootnoteText.Text = $"{(limited ? "Preview limited to " : "Read ")}{FormatSize(bytes.Length)} · " + (IsRecovery ? "recovered stream; original name unknown" : "source unchanged");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            {
                TextPreview.Text = "Preview unavailable\n\n" + ex.Message;
                PreviewTabs.SelectedItem = TextTab;
                PreviewFootnoteText.Text = "No file content was executed.";
            }
        }
        finally { UpdateReplacementControls(); }
    }

    private static BitmapImage DecodeImage(byte[] bytes)
    {
        using var probeStream = new MemoryStream(bytes, false);
        var frame = BitmapFrame.Create(probeStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > 20000 || height > 20000 || (long)width * height > 60_000_000)
            throw new InvalidDataException("Image dimensions exceed the preview limit.");
        using var stream = new MemoryStream(bytes, false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (width >= height) image.DecodePixelWidth = Math.Min(width, 1920);
        else image.DecodePixelHeight = Math.Min(height, 1920);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ResetPreview()
    {
        ClosePlayer();
        PreviewNameText.Text = "Select a file";
        PreviewMetaText.Text = "Preview a file before extracting it.";
        PreviewFootnoteText.Text = "Previews never run archived programs.";
        TextPreview.Text = "Select a file in the archive to see its contents here.\n\nText, common images and raw bytes can be previewed. Game-specific resources are shown as bytes.";
        HexPreview.Text = "";
        ImagePreview.Source = null;
        ImageTab.IsEnabled = false;
        ImageTab.Header = "Image";
        TextTab.Header = "Text";
        PreviewTabs.SelectedItem = TextTab;
    }

    private async void ExtractSelected_Click(object sender, RoutedEventArgs e)
    {
        var entries = SelectedAssetRows().Select(x => x.Asset!).ToArray();
        if (entries.Any(x => !x.Available))
        {
            StatusText.Text = "Selection contains a file that cannot be decoded. Select only available files to extract.";
            return;
        }
        await ExtractAsync(entries);
    }
    private async void ExtractAll_Click(object sender, RoutedEventArgs e)
    {
        var visible = _rows.Select(r => r.Asset!).ToArray();
        await ExtractAsync(visible.Where(a => a.Available).ToArray(), visible.Count(a => !a.Available));
    }

    private async Task ExtractAsync(WorkspaceAsset[] entries, int skippedFiles = 0)
    {
        if (_document == null || _busy || entries.Length == 0) return;
        var folder = new OpenFolderDialog { Title = "Choose where to create the extracted archive folder", Multiselect = false };
        if (folder.ShowDialog(this) != true) return;
        var stem = (_documents.Count == 1 ? Path.GetFileNameWithoutExtension(_document.SourcePath) : "Workspace") + "_extracted";
        var destination = Path.Combine(folder.FolderName, stem);
        for (var counter = 2; Directory.Exists(destination) || File.Exists(destination); counter++) destination = Path.Combine(folder.FolderName, stem + "_" + counter);
        try
        {
            var fileCount = entries.Length;
            var size = entries.Sum(x => x.Size);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!);
            if (drive.IsReady && size > drive.AvailableFreeSpace) throw new IOException($"The selected drive does not have enough free space for {FormatSize(size)} of files.");
            _workCancellation = new CancellationTokenSource();
            SetBusy(true, true);
            WorkProgress.IsIndeterminate = false;
            WorkProgress.Maximum = Math.Max(1, fileCount);
            WorkProgress.Value = 0;
            StatusText.Text = "Extracting to " + destination + (skippedFiles > 0 ? $" · skipping {skippedFiles:N0} unsupported files" : "");
            var progress = new Progress<ExtractionProgress>(p =>
            {
                WorkProgress.Value = p.CompletedFiles;
                StatusText.Text = $"{p.CompletedFiles:N0}/{p.TotalFiles:N0} · {p.CurrentFile} · {FormatSize(p.BytesCopied)}";
            });
            await Task.Run(() => WorkspaceAssetExporter.ExtractAsync(entries, destination, progress, _workCancellation.Token), _workCancellation.Token);
            var skippedNotice = skippedFiles > 0 ? $"\n\nSkipped {skippedFiles:N0} unsupported or encrypted files. Their contents were not extracted." : "";
            StatusText.Text = $"Extracted {fileCount:N0} files to {destination}" + (skippedFiles > 0 ? $" · skipped {skippedFiles:N0} unsupported files" : "");
            MessageBox.Show(this, $"Extracted {fileCount:N0} files to:\n\n{destination}" + skippedNotice + "\n\nNo archived programs were run.", "Extraction complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { StatusText.Text = "Extraction canceled. Completed files remain in the destination folder."; }
        catch (Exception ex)
        {
            StatusText.Text = "Extraction stopped: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Extraction stopped", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { _workCancellation?.Dispose(); _workCancellation = null; SetBusy(false); }
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_document == null) return;
        var dialog = new SaveFileDialog { Title = "Save archive inspection report", Filter = "JSON report|*.json", DefaultExt = ".json", FileName = Path.GetFileNameWithoutExtension(_document.SourcePath) + "-inspection.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(_document.SourcePath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a report filename different from the opened archive.");
            var report = new { inspectedAtUtc = DateTime.UtcNow, source = _document.SourcePath, format = _document.Format, bytes = _document.FileSize, supportsMembers = _document.SupportsMembers, originalIndexDecoded = _document.SupportsMembers && !IsRecovery, limitation = _document.Limitation, metadata = _document.Metadata, headerHex = Convert.ToHexString(_document.HeaderBytes), membersDecoded = IsRecovery ? null : (int?)_document.Entries.Count, recoveredEntries = IsRecovery ? (int?)_document.Entries.Count : null };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = "Inspection report saved to " + dialog.FileName;
        }
        catch (Exception ex) { StatusText.Text = "Could not save report: " + ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { _workCancellation?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = _scanning ? "Canceling scan…" : _writingPoolCopy ? "Canceling modified pool copy…" : "Canceling extraction…"; }
    private void SetBusy(bool busy, bool cancelable = false)
    {
        _busy = busy;
        OpenButton.IsEnabled = !busy;
        OpenFolderButton.IsEnabled = !busy;
        SaveWorkspaceButton.IsEnabled = !busy && _documents.Count > 0;
        ScanArchiveButton.IsEnabled = !busy && _document?.Format == "RPF9";
        FolderTree.IsEnabled = !busy;
        FilesGrid.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy && _documents.Count > 0;
        AssetTypeFilter.IsEnabled = ThumbnailsCheckBox.IsEnabled = !busy;
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        WorkProgress.IsIndeterminate = busy;
        CancelButton.Visibility = busy && cancelable ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        UpdateExtractButtons();
        CommandManager.InvalidateRequerySuggested();
    }
    private void UpdateExtractButtons()
    {
        if (ExtractAllButton == null) return;
        ExtractAllButton.Content = "Extract results…";
        ExtractAllButton.ToolTip = "Extract available assets in the current results, grouped by source. Texture images export as DDS. This exports original assets; edits are stored in the saved workspace.";
        ExtractAllButton.IsEnabled = !_busy && _rows.Any(r => r.Asset?.Available == true);
        var selected = SelectedAssetRows();
        var unsupportedSelection = selected.Any(x => x.Asset?.Available != true);
        ExtractSelectedButton.IsEnabled = !_busy && selected.Length > 0 && !unsupportedSelection;
        ExtractSelectedButton.ToolTip = unsupportedSelection ? "The selection includes a member that cannot be decoded. Select available files only." : "Extract every selected file to a new directory.";
        UpdateReplacementControls();
    }
    private FileRow[] SelectedAssetRows() => (ThumbnailsCheckBox.IsChecked == true ? ImageGallery.SelectedItems : FilesGrid.SelectedItems).Cast<FileRow>().ToArray();

    private static bool LooksLikeText(byte[] bytes)
    {
        if (bytes.Length == 0) return true;
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return true;
        var count = Math.Min(bytes.Length, 8192);
        var bad = 0;
        for (var i = 0; i < count; i++) if (bytes[i] == 0 || (bytes[i] < 32 && bytes[i] != 9 && bytes[i] != 10 && bytes[i] != 13)) bad++;
        return bad < Math.Max(1, count / 100);
    }
    private static string PlainText(byte[] bytes)
    {
        Encoding encoding = Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) encoding = Encoding.Unicode;
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) encoding = Encoding.BigEndianUnicode;
        var value = encoding.GetString(bytes);
        return new string(value.Select(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t' ? '·' : c).ToArray());
    }
    public static string FormatSize(long size)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)size;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "N0" : "0.##") + " " + units[unit];
    }
    private static string FormatHex(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < bytes.Length; index += 16)
        {
            builder.Append(index.ToString("X8")).Append("  ");
            var length = Math.Min(16, bytes.Length - index);
            for (var column = 0; column < 16; column++) builder.Append(column < length ? bytes[index + column].ToString("X2") : "  ").Append(' ');
            builder.Append(" ");
            for (var column = 0; column < length; column++) builder.Append(bytes[index + column] is >= 32 and <= 126 ? (char)bytes[index + column] : '.');
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public sealed class FileRow(ArchiveEntry entry) : System.ComponentModel.INotifyPropertyChanged
    {
        public ArchiveEntry Entry { get; } = entry;
        public WorkspaceAsset? Asset { get; init; }
        private ImageSource? _thumbnail;
        private string _thumbnailStatus = "Loading…";
        public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); } }
        public string ThumbnailStatus { get => _thumbnailStatus; set { _thumbnailStatus = value; PropertyChanged?.Invoke(this, new(nameof(ThumbnailStatus))); } }
        public string SourceLabel => Asset?.SourceName ?? "";
        public long SortSize => Asset?.Size ?? Entry.Size;
        public string ImageDetails => Asset?.PoolTexture is { BaseMipLevel: > 0 } pool ? $"{pool.ExportWidth} × {pool.ExportHeight} · mip {pool.BaseMipLevel} · {pool.Format}"
            : Asset is { Width: > 0 } a ? $"{a.Width} × {a.Height} · {a.Format}" : Asset?.Format ?? "";
        private bool _hasReplacement;
        public bool HasReplacement { get => _hasReplacement; set { if (_hasReplacement == value) return; _hasReplacement = value; PropertyChanged?.Invoke(this, new(nameof(ChangeLabel))); } }
        public string ChangeLabel => HasReplacement ? "Staged" : "";
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string SizeLabel => !Entry.CanExtract && SortSize == 0 ? "—" : FormatSize(SortSize);
        public string EntryToolTip => (Asset?.SourcePath ?? "") + "\n" + (Asset?.LogicalPath ?? Entry.Path) + (Entry.Limitation == null ? "" : "\n" + Entry.Limitation)
            + (Asset?.PoolTexture?.PreviewNote is string note ? "\n" + note : "");
        public string OffsetLabel => Entry.Offset.ToString("X10");
        public string TypeLabel => Asset?.IsTextureChild == true ? "Texture" : Entry.Compression switch { "Recovered AWC" => "Audio bank", "Recovered model" => "Model", "Recovered texture dictionary" => "Texture folder", "RDS5 texture" => "Streaming texture", "Recovered raw deflate shader" => "Native shader", _ => Entry.Extension };
        public string DisplayName => Asset?.Texture?.Name ?? (Entry.ResourceDrawableOffset is int drawable
            ? Entry.Name.Split("_at_", StringSplitOptions.None)[0] + $" · child {drawable:X}"
            : Entry.Compression.StartsWith("Recovered Bink", StringComparison.Ordinal)
            ? "Movie " + Entry.Name.Split('_').ElementAtOrDefault(1) + Entry.Extension
            : Entry.Compression == "Recovered model" ? Entry.Name.Split("_at_", StringSplitOptions.None)[0]
            : Entry.Compression == "Recovered texture dictionary" ? "Texture dictionary " + Entry.Offset.ToString("X10")
            : Entry.Compression == "Recovered AWC" ? "Audio bank " + Entry.Name.Split('_').ElementAtOrDefault(1)
            : Entry.Extension == ".psin" ? "Metadata " + Entry.Offset.ToString("X10") + ".psin" : Entry.Name);
    }
    private sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) => execute();
        public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    }
}
