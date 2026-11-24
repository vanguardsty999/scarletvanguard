using Microsoft.Win32;
using RpfPreview.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace RpfPreview.App;

public partial class MainWindow
{
    private readonly Dictionary<string, ArchiveDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReplacementWorkspace> _sourceProjects = new(StringComparer.OrdinalIgnoreCase);
    private AssetWorkspaceIndex? _assetIndex;
    private FileRow[] _allRows = [];
    private ArchiveDocument? _scopeSource;
    private ArchiveEntry? _scopeContainer;
    private string? _workspaceDirectory;
    private AssetWorkspaceProject? _sessionProject;
    private DispatcherTimer? _searchTimer;
    private sealed record Navigation(ArchiveDocument? Source = null, string? Folder = null, ArchiveEntry? Container = null, WorkspaceAsset? Asset = null);
    private sealed record MoreTextures(TreeViewItem Parent, ArchiveDocument Source, int Skip);

    private static IEnumerable<string> DirectorySources(string path)
        => Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".rpf" or ".ttpf" or ".zip").Order(StringComparer.OrdinalIgnoreCase);

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog { Title = "Load archives and texture pools from a folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try { await OpenSourcesAsync(DirectorySources(dialog.FolderName), true, dialog.FolderName); }
        catch (Exception ex) { StatusText.Text = "Could not load folder: " + ex.Message; }
    }
    private async void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "Open saved workspace", Filter = "Asset workspace|*.rpfworkspace|Replacement project|*.rpfproject" };
        if (dialog.ShowDialog(this) != true) return;
        if (Path.GetExtension(dialog.FileName).Equals(".rpfproject", StringComparison.OrdinalIgnoreCase)) await OpenReplacementProjectAsync(dialog.FileName);
        else await OpenSavedWorkspaceAsync(dialog.FileName);
    }

    private async Task<bool> OpenSourcesAsync(IEnumerable<string> paths, bool append, string? directory = null)
    {
        if (_busy) return false;
        string[] requested = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(513).ToArray();
        if (requested.Length == 0) { StatusText.Text = "No RPF, ZIP or TTPF files were found."; return false; }
        if (requested.Length > 512) { StatusText.Text = "Open at most 512 sources in a workspace."; return false; }
        var loaded = append ? new Dictionary<string, ArchiveDocument>(_documents, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        _previewCancellation?.Cancel(); CancelThumbnails(); ClosePlayer();
        _workCancellation = new CancellationTokenSource(); var token = _workCancellation.Token;
        SetBusy(true, true); WorkProgress.IsIndeterminate = false; WorkProgress.Maximum = 1;
        var errors = new List<string>(); var discoveryWarnings = new List<string>();
        try
        {
            for (int i = 0; i < requested.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string source = requested[i]; StatusText.Text = $"Loading source {i + 1:N0}/{requested.Length:N0} · {Path.GetFileName(source)}";
                WorkProgress.Value = .8 * i / requested.Length;
                try
                {
                    var opened = await Task.Run(() => ArchiveReader.Open(source), token);
                    if (opened.Format == "RPF9" && opened.HeaderBytes.AsSpan(0, 4).SequenceEqual("9FPR"u8))
                    {
                        int sourceIndex = i;
                        var discovery = new Progress<PayloadIndexProgress>(p =>
                        {
                            WorkProgress.Value = .8 * (sourceIndex + p.Fraction) / requested.Length;
                            StatusText.Text = Path.GetFileName(source) + " · " + p.Message;
                        });
                        try
                        {
                            await Task.Run(() => PayloadIndexBuilder.BuildModels(source, discovery, token), token);
                            token.ThrowIfCancellationRequested();
                            opened = await Task.Run(() => ArchiveReader.Open(source), token);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
                        { discoveryWarnings.Add(Path.GetFileName(source) + " · model discovery incomplete: " + ex.Message); }
                    }
                    loaded[source] = opened;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
                { errors.Add(Path.GetFileName(source) + ": " + ex.Message); }
            }
            if (loaded.Count == 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Take(5)));
            if (loaded.Count > 512) throw new InvalidDataException("Workspace source limit is 512.");
            StatusText.Text = "Indexing model names and texture metadata…";
            var progress = new Progress<WorkspaceIndexProgress>(p => { WorkProgress.Value = .8 + .2 * p.Completed / Math.Max(1, p.Total); StatusText.Text = $"Indexing assets · {p.Completed:N0}/{p.Total:N0} entries"; });
            var index = await Task.Run(() => AssetWorkspaceIndex.Build(loaded.Values, progress, token), token);
            token.ThrowIfCancellationRequested();
            if (append) _sessionProject?.AddSources(loaded.Keys);
            if (!append) { _sourceProjects.Clear(); _sessionProject = null; ClearQueuedModelParts(); }
            _documents.Clear(); foreach (var pair in loaded) _documents.Add(pair.Key, pair.Value);
            _assetIndex = index;
            BuildLoadedTextureLookup();
            _allRows = index.Assets.Select(a => new FileRow(a.Entry) { Asset = a }).ToArray();
            _workspaceDirectory = directory ?? (append ? _workspaceDirectory : null);
            _scopeSource = _documents.Count == 1 ? _documents.Values.First() : null;
            _scopeContainer = null; _selectedFolder = null;
            SetActiveDocument(_scopeSource ?? _documents.Values.First());
            _shownReplacementTarget = null;
            _loading = true; SearchBox.Text = ""; AssetTypeFilter.SelectedIndex = 0; ThumbnailsCheckBox.IsChecked = false;
            AvailableOnlyCheckBox.IsChecked = _documents.Count == 1 && _document?.Format == "RDS5";
            AvailableOnlyCheckBox.Visibility = Visibility.Visible; DropZone.Visibility = Visibility.Collapsed; ArchiveWorkspace.Visibility = Visibility.Visible;
            FilesGrid.Visibility = Visibility.Visible; UnsupportedPanel.Visibility = Visibility.Collapsed;
            BuildWorkspaceTree(); _loading = false;
            RefreshReplacementAnnotations(); RefreshFiles(); ResetPreview(); UpdateWorkspaceHeader();
            _sessionProject?.Save(CurrentWorkspaceView());
            StatusText.Text = $"Loaded {_documents.Count:N0} sources · {_allRows.Count(r => r.Asset?.Kind == "model"):N0} models · {_allRows.Count(r => r.Asset?.IsImage == true):N0} images indexed" + (errors.Count > 0 ? $" · {errors.Count} sources unavailable" : "");
            if (discoveryWarnings.Count > 0) StatusText.Text += " · model discovery incomplete";
            if (errors.Count + index.Warnings.Count + discoveryWarnings.Count > 0) { NoticeBorder.Visibility = Visibility.Visible; NoticeText.Text += $" · {errors.Count + index.Warnings.Count + discoveryWarnings.Count:N0} indexing notices (hover for details)"; NoticeText.ToolTip = string.Join(Environment.NewLine, errors.Concat(discoveryWarnings).Concat(index.Warnings).Take(30)); }
            return true;
        }
        catch (OperationCanceledException) { StatusText.Text = "Loading canceled · previously loaded workspace retained"; return false; }
        catch (Exception ex) { StatusText.Text = "Could not load workspace: " + ex.Message; return false; }
        finally { _loading = false; _workCancellation?.Dispose(); _workCancellation = null; SetBusy(false); }
    }

    private void SetActiveDocument(ArchiveDocument document)
    {
        _document = document;
        if (!_sourceProjects.TryGetValue(document.SourcePath, out _replacementWorkspace))
        {
            _replacementWorkspace = _sessionProject?.ForSource(document.SourcePath, false);
            if (_replacementWorkspace != null) _sourceProjects[document.SourcePath] = _replacementWorkspace;
        }
        RefreshReplacementKeySet();
        HeaderMetadataText.Text = string.Join(Environment.NewLine, document.Metadata.Select(p => p.Key + ": " + p.Value));
    }
    private void UpdateWorkspaceHeader()
    {
        if (_documents.Count == 0) return;
        ArchiveNameText.Text = _scopeSource == null ? $"Workspace · {_documents.Count:N0} sources" : Path.GetFileName(_scopeSource.SourcePath);
        ArchivePathText.Text = _scopeSource?.SourcePath ?? _workspaceDirectory ?? "Multiple source archives";
        ArchiveStatsText.Text = $"{_allRows.Length:N0} indexed assets"; FormatText.Text = _documents.Count > 1 ? "Workspace" : _document?.Format;
        FormatBadge.Visibility = Visibility.Visible;
        bool recovery = _documents.Values.Any(d => d.Format == "RPF9");
        NoticeBorder.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Text = recovery ? "RPF9 payload recovery · original directories and archive rebuilding remain unavailable · edits save in separate projects" : "";
        Title = (_sessionProject == null ? "rpf9-explorer 1.2" : Path.GetFileName(_sessionProject.ProjectPath) + " · rpf9-explorer 1.2");
    }

    private void BuildWorkspaceTree()
    {
        FolderTree.Items.Clear();
        var root = new TreeViewItem { Header = "All loaded sources", Tag = new Navigation(), IsExpanded = true };
        FolderTree.Items.Add(root);
        foreach (var source in _documents.Values.OrderBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            string label = _workspaceDirectory != null ? Path.GetRelativePath(_workspaceDirectory, source.SourcePath) : Path.GetFileName(source.SourcePath);
            var node = new TreeViewItem { Header = label, ToolTip = source.SourcePath, Tag = new Navigation(source) };
            node.Items.Add("Loading…"); node.Expanded += ExpandWorkspaceNode; root.Items.Add(node);
        }
    }
    private void ExpandWorkspaceNode(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node || e.OriginalSource != node || node.Items.Count != 1 || node.Items[0] is not string || node.Tag is not Navigation navigation) return;
        node.Items.Clear();
        if (navigation.Source is not { } document) return;
        if (navigation.Container is { } container)
        {
            foreach (var asset in _assetIndex!.Children(document, container)) node.Items.Add(AssetTreeLeaf(asset));
            return;
        }
        if (document.Format == "RDS5") { AppendPoolTreePage(node, document, 0); return; }
        string prefix = navigation.Folder == null ? "" : navigation.Folder + "/";
        var folders = document.Entries.Select(x => x.Folder).Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && p.Length > prefix.Length)
            .Select(p => prefix + p[prefix.Length..].Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
        {
            var child = new TreeViewItem { Header = Path.GetFileName(folder), Tag = new Navigation(document, folder) };
            child.Items.Add("Loading…"); child.Expanded += ExpandWorkspaceNode; node.Items.Add(child);
        }
        foreach (var entry in document.Entries.Where(x => x.Compression == "Recovered texture dictionary" && x.Folder == (navigation.Folder ?? "")))
        {
            var children = _assetIndex!.Children(document, entry);
            var child = new TreeViewItem { Header = $"Texture dictionary {entry.Offset:X} ({children.Count})", Tag = new Navigation(document, navigation.Folder, entry), ToolTip = entry.Path };
            child.Items.Add("Loading…"); child.Expanded += ExpandWorkspaceNode; node.Items.Add(child);
        }
    }
    private TreeViewItem AssetTreeLeaf(WorkspaceAsset asset) => new() { Header = asset.Name, ToolTip = asset.SearchText, Tag = new Navigation(asset.Document, asset.Entry.Folder, asset.IsTextureChild ? asset.Entry : null, asset) };
    private void AppendPoolTreePage(TreeViewItem node, ArchiveDocument source, int skip)
    {
        var assets = _allRows.Where(r => r.Asset?.Document == source).Skip(skip).Take(201).ToArray();
        foreach (var row in assets.Take(200)) node.Items.Add(AssetTreeLeaf(row.Asset!));
        if (assets.Length > 200) node.Items.Add(new TreeViewItem { Header = "Show next 200 textures…", Tag = new MoreTextures(node, source, skip + 200) });
    }
    private void NavigateWorkspace(object? tag)
    {
        if (tag is MoreTextures more) { more.Parent.Items.Remove(FolderTree.SelectedItem); AppendPoolTreePage(more.Parent, more.Source, more.Skip); return; }
        if (tag is not Navigation navigation) return;
        _searchTimer?.Stop(); _loading = true; SearchBox.Text = ""; _loading = false;
        _scopeSource = navigation.Source; _selectedFolder = navigation.Folder; _scopeContainer = navigation.Container;
        if (_scopeSource != null) SetActiveDocument(_scopeSource);
        if ((_scopeContainer != null || navigation.Asset?.IsImage == true) && SelectedAssetType != "image") { _loading = true; AssetTypeFilter.SelectedIndex = 1; _loading = false; }
        RefreshFiles(); UpdateWorkspaceHeader();
        if (navigation.Asset != null)
        {
            FilesGrid.SelectedItem = _rows.FirstOrDefault(r => r.Asset == navigation.Asset);
            if (FilesGrid.SelectedItem != null) FilesGrid.ScrollIntoView(FilesGrid.SelectedItem);
            if (FilesGrid.SelectedItem is FileRow selected && ThumbnailsCheckBox.IsChecked == true) { _galleryPage = Array.IndexOf(_galleryRows, selected) / ImagesPerPage; ConfigureGallery(_rows.ToArray(), false); ImageGallery.SelectedItem = selected; ImageGallery.ScrollIntoView(selected); }
        }
    }
    private void UpFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_scopeContainer != null) _scopeContainer = null;
        else if (_selectedFolder != null) { int slash = _selectedFolder.LastIndexOf('/'); _selectedFolder = slash < 0 ? null : _selectedFolder[..slash]; }
        else _scopeSource = null;
        RefreshFiles(); UpdateWorkspaceHeader();
    }
    private void FilesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileRow { Asset: { } asset } && _assetIndex?.HasChildren(asset) == true)
            NavigateWorkspace(new Navigation(asset.Document, asset.Entry.Folder, asset.Entry));
    }
    private string SelectedAssetType => (AssetTypeFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
    private void WorkspaceFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FilesGrid == null) return;
        if (ThumbnailsCheckBox.IsChecked == true && SelectedAssetType != "image") { _loading = true; ThumbnailsCheckBox.IsChecked = false; _loading = false; }
        RefreshFiles();
    }
    private void ScheduleSearch()
    {
        _searchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Stop(); _searchTimer.Tick -= ApplySearchTick; _searchTimer.Tick += ApplySearchTick; _searchTimer.Start();
    }
    private void ApplySearchTick(object? sender, EventArgs e) { _searchTimer!.Stop(); RefreshFiles(); }

    private WorkspaceViewState CurrentWorkspaceView()
    {
        var asset = (FilesGrid.SelectedItem as FileRow)?.Asset;
        return new(SearchBox.Text, SelectedAssetType, ThumbnailsCheckBox.IsChecked == true, AvailableOnlyCheckBox.IsChecked == true,
            _scopeSource?.SourcePath, _selectedFolder, _scopeContainer?.Path, asset?.SourcePath, asset?.Entry.Path, asset?.Texture?.Index ?? -1, SearchAllSourcesCheckBox.IsChecked == true);
    }
    private bool CreateSavedWorkspace()
    {
        if (_documents.Count == 0) return false;
        var dialog = new SaveFileDialog { Title = "Save workspace and staged edits", Filter = "RPF asset workspace|*.rpfworkspace", DefaultExt = ".rpfworkspace", FileName = "Assets.rpfworkspace" };
        if (dialog.ShowDialog(this) != true) return false;
        var project = AssetWorkspaceProject.Create(dialog.FileName, _documents.Keys);
        foreach (var existing in _sourceProjects.Values.ToArray()) project.ImportSourceProject(existing);
        if (_replacementWorkspace != null && !_sourceProjects.ContainsKey(_replacementWorkspace.SourcePath)) project.ImportSourceProject(_replacementWorkspace);
        _sessionProject = project;
        foreach (var source in _documents.Keys) if (project.ForSource(source, false) is { } edits) _sourceProjects[source] = edits;
        if (_document != null) _replacementWorkspace = project.ForSource(_document.SourcePath, true);
        if (_replacementWorkspace != null) _sourceProjects[_replacementWorkspace.SourcePath] = _replacementWorkspace;
        project.Save(CurrentWorkspaceView()); RefreshReplacementAnnotations(); UpdateWorkspaceHeader(); return true;
    }
    private void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _documents.Count == 0) return;
        try
        {
            if (_sessionProject == null && !CreateSavedWorkspace()) return;
            _sessionProject!.AddSources(_documents.Keys); _sessionProject.Save(CurrentWorkspaceView());
            StatusText.Text = $"Workspace saved · {_documents.Count:N0} sources · staged edits saved separately from game archives";
        }
        catch (Exception ex) { StatusText.Text = "Could not save workspace: " + ex.Message; }
    }
    private async Task<bool> OpenSavedWorkspaceAsync(string path)
    {
        if (_busy) return false;
        try
        {
            var project = await Task.Run(() => AssetWorkspaceProject.Load(path));
            if (!await OpenSourcesAsync(project.SourcePaths, false)) return false;
            _sessionProject = project;
            foreach (string source in project.SourcePaths) if (project.ForSource(source, false) is { } edits) _sourceProjects[source] = edits;
            if (_document != null) SetActiveDocument(_document);
            var view = project.View; _loading = true;
            SearchBox.Text = view.Search; AssetTypeFilter.SelectedItem = AssetTypeFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, view.Type)) ?? AssetTypeFilter.Items[0];
            ThumbnailsCheckBox.IsChecked = view.Thumbnails; AvailableOnlyCheckBox.IsChecked = view.AvailableOnly; SearchAllSourcesCheckBox.IsChecked = view.SearchAllSources;
            _scopeSource = view.ScopeSource != null && _documents.TryGetValue(view.ScopeSource, out var scope) ? scope : null;
            _selectedFolder = view.Folder; _scopeContainer = _scopeSource?.Entries.FirstOrDefault(e => e.Path == view.Container);
            _loading = false; RefreshReplacementAnnotations(); RefreshFiles(); UpdateWorkspaceHeader();
            var selected = _rows.FirstOrDefault(r => r.Asset?.SourcePath == view.SelectedSource && r.Entry.Path == view.SelectedEntry && (r.Asset?.Texture?.Index ?? -1) == view.SelectedTexture);
            if (selected != null) { FilesGrid.SelectedItem = selected; FilesGrid.ScrollIntoView(selected); if (view.Thumbnails) { _galleryPage = Array.IndexOf(_galleryRows, selected) / ImagesPerPage; ConfigureGallery(_rows.ToArray(), false); ImageGallery.SelectedItem = selected; ImageGallery.ScrollIntoView(selected); } }
            StatusText.Text = "Workspace reopened · sources, search and staged edits restored"; return true;
        }
        catch (Exception ex) { _loading = false; StatusText.Text = "Could not open workspace: " + ex.Message; return false; }
    }
    private void ClearWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _previewCancellation?.Cancel(); CancelThumbnails(); ClosePlayer();
        ClearQueuedModelParts();
        _documents.Clear(); _sourceProjects.Clear(); _allRows = []; _assetIndex = null; _document = _scopeSource = null;
        _scopeContainer = null; _selectedFolder = null; _sessionProject = null; _replacementWorkspace = null; _shownReplacementTarget = null;
        _rows.Clear(); FolderTree.Items.Clear(); ImageGallery.ItemsSource = null;
        _workspaceDirectory = null; _thumbnailCache.Clear(); _loadedTextureLocations.Clear(); _loadedResidentTextures.Clear(); _loadedResidentNames.Clear(); _defaultResidentNames.Clear(); _defaultResidentSources.Clear(); _defaultResidentRoot = null;
        _modelImageCache.Clear(); _modelImageCacheBytes = 0; _streamingCatalog = null; _streamingPoolDirectory = null;
        ArchiveWorkspace.Visibility = Visibility.Collapsed; DropZone.Visibility = Visibility.Visible; NoticeBorder.Visibility = Visibility.Collapsed;
        ArchiveNameText.Text = "No sources loaded"; ArchivePathText.Text = "Open archives or an entire folder"; ArchiveStatsText.Text = ""; FormatBadge.Visibility = Visibility.Collapsed;
        ResetPreview(); SetBusy(false); StatusText.Text = "Workspace cleared · saved projects retained";
    }
}
