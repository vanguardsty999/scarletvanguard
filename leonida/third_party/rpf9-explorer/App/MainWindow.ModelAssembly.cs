using RpfPreview.Core;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RpfPreview.App;

public sealed class ModelAssemblySelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int count && count >= 2;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public partial class MainWindow
{
    private bool _modelAssemblyActive;
    private readonly HashSet<string> _modelAssemblySourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(FileRow Row, ArchiveDocument Document)> _queuedModelParts = [];
    private int QueuedModelPartCount => _queuedModelParts.Count;
    private ICommand? _addModelPartsCommand, _previewModelPartsCommand, _clearModelPartsCommand;
    public ICommand AddModelPartsCommand => _addModelPartsCommand ??= new RelayCommand(AddSelectedModelParts,
        () => !_busy && ThumbnailsCheckBox.IsChecked != true && FilesGrid.SelectedItems.Cast<FileRow>().Any(r => r.Entry.Compression == "Recovered model"));
    public ICommand PreviewModelPartsCommand => _previewModelPartsCommand ??= new RelayCommand(async () => await PreviewQueuedModelPartsAsync(),
        () => !_busy && _queuedModelParts.Count > 0);
    public ICommand ClearModelPartsCommand => _clearModelPartsCommand ??= new RelayCommand(ClearQueuedModelParts,
        () => !_busy && _queuedModelParts.Count > 0);

    private sealed record AssemblyResource(string Source, long Offset, long StoredSize, int Split, byte[] Bytes)
    {
        public ReadOnlyMemory<byte> System => Bytes.AsMemory(0, Split);
        public ReadOnlyMemory<byte> Graphics => Bytes.AsMemory(Split);
    }
    private sealed record AssemblyComponent(string Label, ArchiveDocument Document, ArchiveEntry Entry,
        RageModel Model, IReadOnlyDictionary<int, int> MaterialMap, string TextureDetails);

    private void ClearModelAssemblyState()
    {
        _modelAssemblyActive = false;
        _modelAssemblySourcePaths.Clear();
        if (ModelAssemblyBanner != null) { ModelAssemblyBanner.Text = ""; ModelAssemblyBanner.Visibility = Visibility.Collapsed; }
    }

    private void AddSelectedModelParts()
    {
        if (_busy) return;
        if (ThumbnailsCheckBox.IsChecked == true) { StatusText.Text = "Select model rows in the file list to add parts."; return; }
        var selected = FilesGrid.SelectedItems.Cast<FileRow>().ToArray();
        var existing = _queuedModelParts.Select(QueuedModelPartKey).ToHashSet();
        var additions = new List<(FileRow Row, ArchiveDocument Document)>();
        int duplicates = 0, ignored = 0;
        foreach (var row in selected)
        {
            var document = row.Asset?.Document ?? _document;
            if (row.Entry.Compression != "Recovered model" || document == null) { ignored++; continue; }
            var part = (Row: row, Document: document);
            if (!existing.Add(QueuedModelPartKey(part))) { duplicates++; continue; }
            additions.Add(part);
        }
        if (_queuedModelParts.Count + additions.Count > 64)
        { StatusText.Text = $"The parts queue holds at most 64 components. It already has {_queuedModelParts.Count}; choose fewer additional parts or clear it."; return; }
        _queuedModelParts.AddRange(additions); UpdateQueuedModelPartsControls();
        StatusText.Text = additions.Count > 0
            ? $"Added {additions.Count:N0} model parts · {_queuedModelParts.Count:N0} queued across searches"
            : selected.Length == 0 ? "Select one or more model rows, then choose Add selected parts." : "No new model parts were added.";
        if (duplicates > 0) StatusText.Text += $" · {duplicates:N0} already queued";
        if (ignored > 0) StatusText.Text += $" · {ignored:N0} non-model or source-less rows omitted";
    }

    private Task PreviewQueuedModelPartsAsync()
    {
        if (_busy) return Task.CompletedTask;
        if (_queuedModelParts.Count == 0) { StatusText.Text = "Add model parts from one or more searches before previewing the queue."; return Task.CompletedTask; }
        return PreviewModelAssemblySourcesAsync(_queuedModelParts.ToArray());
    }

    private void ClearQueuedModelParts()
    {
        _queuedModelParts.Clear(); UpdateQueuedModelPartsControls();
        if (StatusText != null) StatusText.Text = "Model-parts queue cleared.";
    }

    private static (string Source, string Entry, long Offset, int? Drawable) QueuedModelPartKey((FileRow Row, ArchiveDocument Document) part)
        => (Path.GetFullPath(part.Document.SourcePath).ToUpperInvariant(), part.Row.Entry.Path,
            part.Row.Entry.Offset, part.Row.Entry.ResourceDrawableOffset);

    private void UpdateQueuedModelPartsControls()
    {
        if (ModelPartsQueueCount != null)
        {
            ModelPartsQueueCount.Text = $"Parts: {_queuedModelParts.Count:N0} / 64";
            ModelPartsQueueCount.ToolTip = _queuedModelParts.Count == 0 ? "Add components from different model searches; the queue keeps their source identities."
                : string.Join("\n", _queuedModelParts.Take(12).Select(p => p.Row.DisplayName + " · " + Path.GetFileName(p.Document.SourcePath)))
                    + (_queuedModelParts.Count > 12 ? $"\n+ {_queuedModelParts.Count - 12} more components" : "");
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private async void PreviewTogether_Click(object sender, RoutedEventArgs e) => await PreviewModelsTogetherAsync();

    private async Task PreviewModelsTogetherAsync()
    {
        if (_busy) return;
        var selected = FilesGrid.SelectedItems.Cast<FileRow>().ToArray();
        if (selected.Length < 2 || selected.Count(r => r.Entry.Compression == "Recovered model") < 2)
        {
            StatusText.Text = "Ctrl-click or Shift-click two or more native model rows, then choose Preview together.";
            return;
        }
        await PreviewModelAssemblySelectionAsync(selected);
    }

    private Task PreviewModelAssemblySelectionAsync(FileRow[] selected)
    {
        var selection = selected.Select(r => (Row: r, Document: r.Asset?.Document ?? _document)).ToArray();
        if (selection.Any(x => x.Document == null)) { StatusText.Text = "A selected component has no loaded source archive."; return Task.CompletedTask; }
        return PreviewModelAssemblySourcesAsync(selection.Select(x => (x.Row, x.Document!)).ToArray());
    }

    private async Task PreviewModelAssemblySourcesAsync((FileRow Row, ArchiveDocument Document)[] selection)
    {
        var models = selection.Where(x => x.Row.Entry.Compression == "Recovered model").ToArray();
        if (models.Length > 64) { StatusText.Text = "Select at most 64 model components for one bounded static preview."; return; }
        var previous = _activePreview;
        _previewCancellation?.Cancel();
        _workCancellation?.Dispose();
        var work = _workCancellation = new CancellationTokenSource();
        SetBusy(true, true);
        try
        {
            if (previous != null)
            {
                try { await previous; }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException) { }
            }
            work.Token.ThrowIfCancellationRequested();
            ClosePlayer(); ResetAssetPreview();
            ImagePreview.Source = null; ImageTab.IsEnabled = false; ImageTab.Header = "Image";
            TextTab.Header = "Assembly details"; TextPreview.Text = "Preparing the selected model components…"; HexPreview.Text = "";
            PreviewNameText.Text = "Preparing selected static assembly"; PreviewMetaText.Text = $"{models.Length:N0} selected model components";
            PreviewTabs.SelectedItem = TextTab;
            _previewCancellation?.Dispose();
            _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
            WorkProgress.IsIndeterminate = false; WorkProgress.Maximum = models.Length; WorkProgress.Value = 0;
            var token = _previewCancellation.Token;
            _activePreview = BuildModelAssemblyAsync(selection, token);
            await _activePreview;
        }
        catch (OperationCanceledException)
        {
            _decodedModel = null; ClearModelAssemblyState(); ModelPreview.Clear(); ModelTab.IsEnabled = false;
            PreviewFootnoteText.Text = "Static model assembly canceled."; StatusText.Text = "Static model assembly canceled.";
        }
        catch (Exception ex)
        {
            _decodedModel = null; ClearModelAssemblyState(); ModelPreview.Clear(); ModelTab.IsEnabled = false;
            TextTab.Header = "Assembly details"; TextPreview.Text = "The selected models could not be assembled.\n\n" + ex.Message;
            PreviewTabs.SelectedItem = TextTab; StatusText.Text = "Could not preview selected models: " + ex.Message;
        }
        finally
        {
            work.Dispose();
            if (ReferenceEquals(_workCancellation, work)) _workCancellation = null;
            SetBusy(false);
            if (_modelAssemblyActive) { StageReplacementButton.IsEnabled = false; ReplacementControls.Visibility = Visibility.Collapsed; }
        }
    }

    private async Task BuildModelAssemblyAsync((FileRow Row, ArchiveDocument Document)[] selection, CancellationToken token)
    {
        const int maxVertices = 2_000_000, maxTriangles = 2_000_000, maxMeshes = 4096;
        const long maxPreviewImageBytes = 128L * 1024 * 1024;
        var meshes = new List<RageMesh>(); var materials = new List<RageMaterial>(); var preview = new List<MeshPreviewPart>();
        var components = new List<AssemblyComponent>(); var skipped = new List<string>(); var notes = new List<string>();
        var images = new HashSet<ImageSource>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<(string Source, string Entry, int? Drawable)>();
        int vertices = 0, triangles = 0, nextMaterial = 0, completed = 0, missingMaterials = 0, layeredMaterials = 0;
        long previewImageBytes = 0;
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        AssemblyResource? resource = null;
        foreach (var item in selection)
        {
            token.ThrowIfCancellationRequested();
            var entry = item.Row.Entry; var document = item.Document;
            string label = item.Row.Asset?.Name ?? entry.Name;
            string origin = Path.GetFileName(document.SourcePath) + " / " + entry.Path;
            _modelAssemblySourcePaths.Add(Path.GetFullPath(document.SourcePath));
            if (entry.Compression != "Recovered model") { skipped.Add(origin + " · not a native model component"); continue; }
            if (!seen.Add((document.SourcePath, entry.Path, entry.ResourceDrawableOffset))) { skipped.Add(origin + " · duplicate selection"); continue; }
            StatusText.Text = $"Preparing component {completed + 1} of {selection.Count(x => x.Row.Entry.Compression == "Recovered model")}: {label}";
            try
            {
                if (!entry.CanExtract) throw new NotSupportedException(entry.Limitation ?? "The resource cannot be read with the available decoder.");
                var previousResource = resource;
                var decoded = await Task.Run(() => {
                    token.ThrowIfCancellationRequested();
                    int split = entry.ResourceSystemBytes ?? checked((int)entry.Segments![0].Size);
                    AssemblyResource current;
                    if (previousResource != null && string.Equals(previousResource.Source, document.SourcePath, StringComparison.OrdinalIgnoreCase)
                        && previousResource.Offset == entry.Offset && previousResource.StoredSize == entry.StoredSize && previousResource.Split == split)
                        current = previousResource;
                    else
                    {
                        byte[] bytes = ArchiveReader.ReadResourceData(document, entry, token);
                        if (split <= 0 || split >= bytes.Length) throw new InvalidDataException("Invalid model resource section boundary.");
                        current = new AssemblyResource(document.SourcePath, entry.Offset, entry.StoredSize, split, bytes);
                    }
                    var model = RageModelDecoder.Decode(current.System.Span, current.Graphics.Span, entry.ResourceDrawableOffset)
                        ?? throw new InvalidDataException("This selected component does not contain supported validated geometry.");
                    return (Resource: current, Model: model);
                }, token);
                resource = decoded.Resource;
                var model = decoded.Model;
                int componentVertices = checked(model.Meshes.Sum(m => m.Positions.Count));
                int componentTriangles = checked(model.Meshes.Sum(m => m.Indices.Count / 3));
                if (componentVertices > maxVertices - vertices || componentTriangles > maxTriangles - triangles || model.Meshes.Count > maxMeshes - meshes.Count)
                    throw new NotSupportedException("Adding this component would exceed the combined geometry preview limit; it was left out.");
                token.ThrowIfCancellationRequested();
                IReadOnlyDictionary<int, ImageSource> resolved = new Dictionary<int, ImageSource>();
                string textureDetails;
                try
                {
                    resolved = await ResolveModelTexturesAsync(document, entry, model, resource.System, resource.Graphics, token);
                    missingMaterials += _modelMissingTextureMaterials; layeredMaterials += _modelLayerPreviewMaterials;
                    textureDetails = _modelTextureDetails;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                { textureDetails = "Texture lookup unavailable: " + ex.Message; notes.Add(origin + " · " + textureDetails); }
                var map = new Dictionary<int, int>();
                foreach (int material in (model.Materials?.Select(m => m.ShaderIndex) ?? Enumerable.Empty<int>())
                    .Concat(model.Meshes.Where(m => m.ShaderIndex.HasValue).Select(m => m.ShaderIndex!.Value)).Distinct().Order()) map[material] = nextMaterial++;
                if (model.Materials != null) materials.AddRange(model.Materials.Select(m => m with { ShaderIndex = map[m.ShaderIndex] }));
                string componentLabel = $"{components.Count + 1:00} | {model.Name}";
                foreach (var mesh in model.Meshes)
                {
                    int? shader = mesh.ShaderIndex is int original ? map[original] : null;
                    string meshName = componentLabel + " | " + mesh.Name + (mesh.ShaderIndex is int native ? $" | source material {native}" : "");
                    var combined = mesh with { Name = meshName, ShaderIndex = shader };
                    meshes.Add(combined);
                    foreach (var p in mesh.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                    ImageSource? texture = mesh.ShaderIndex is int sourceShader && resolved.TryGetValue(sourceShader, out var color) ? color : null;
                    if (texture != null && !images.Contains(texture))
                    {
                        long bytes = texture is BitmapSource bitmap ? (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 : 0;
                        if (bytes > maxPreviewImageBytes - previewImageBytes)
                        { texture = null; notes.Add(componentLabel + " · an additional texture exceeded the combined image budget; this mesh uses a neutral preview."); }
                        else { images.Add(texture); previewImageBytes += bytes; }
                    }
                    preview.Add(new(meshName, mesh.Positions, mesh.Indices) { TexCoords = mesh.TexCoords, VertexColors = mesh.VertexColors, Texture = texture });
                }
                vertices += componentVertices; triangles += componentTriangles;
                components.Add(new(componentLabel, document, entry, model, map, textureDetails));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
            { skipped.Add(origin + " · " + ex.Message); }
            finally { completed++; WorkProgress.Value = completed; }
        }
        token.ThrowIfCancellationRequested();
        string details = AssemblyDetails(selection.Length, components, skipped, notes);
        TextTab.Header = "Assembly details"; TextPreview.Text = details; HexPreview.Text = "A static assembly has no single source byte range. Select an individual component for its resource bytes.";
        if (components.Count == 0)
        {
            _decodedModel = null; ClearModelAssemblyState(); ModelPreview.Clear(); ModelTab.IsEnabled = false; PreviewTabs.SelectedItem = TextTab;
            PreviewNameText.Text = "No selected model components were decoded"; PreviewMetaText.Text = $"{skipped.Count:N0} skipped or unsupported selections";
            PreviewFootnoteText.Text = "See Assembly details for every selected component and its decoder result.";
            StatusText.Text = "No validated model components could be assembled."; return;
        }
        _decodedModel = new RageModel("Selected_model_assembly", meshes.ToArray(), min, max) {
            IsFragment = components.Any(c => c.Model.IsFragment), HasBoneBindings = components.Any(c => c.Model.HasBoneBindings),
            Materials = materials.ToArray(), PresentationNote = "Experimental user-selected static assembly. Source coordinates, UVs, colors and component names are retained. No authored character identity, bone animation or missing parts are inferred. OBJ exports geometry and UVs; native shader effects and materials are not exported." };
        _modelAssemblyActive = true;
        _modelMissingTextureMaterials = missingMaterials; _modelLayerPreviewMaterials = layeredMaterials;
        _modelTextureDetails = string.Join("\n\n", components.Select(c => c.Label + "\n" + c.TextureDetails));
        ModelPreview.ShowMeshes(preview);
        ModelTab.IsEnabled = true; PreviewTabs.SelectedItem = ModelTab;
        PreviewNameText.Text = $"Selected static model assembly · {components.Count:N0} components";
        PreviewNameText.ToolTip = "Only the user-selected components are included. Original character membership is unknown.";
        PreviewMetaText.Text = $"{vertices:N0} vertices · {triangles:N0} triangles · {meshes.Count:N0} mesh parts · {skipped.Count:N0} skipped selections";
        ModelAssemblyBanner.Text = $"Experimental static assembly · {components.Count} selected components shown" + (skipped.Count > 0 ? $" · {skipped.Count} omitted (see Assembly details)" : "")
            + ". Source coordinates are retained. Parts are not automatically identified or fitted into an authored character; alternate parts may overlap.";
        ModelAssemblyBanner.Visibility = Visibility.Visible;
        PreviewFootnoteText.Text = $"{ModelPreview.PresentedTexturedMeshes:N0} textured mesh parts · {missingMaterials:N0} unresolved materials · static geometry; rigging and native shader effects are not applied";
        StatusText.Text = $"Previewing {components.Count:N0} selected model components together · {skipped.Count:N0} skipped · export combined geometry with Export OBJ.";
    }

    private static string AssemblyDetails(int selected, IReadOnlyList<AssemblyComponent> components,
        IReadOnlyList<string> skipped, IReadOnlyList<string> notes)
    {
        var text = new StringBuilder("Experimental static model assembly\n\n");
        text.AppendLine($"Selected rows: {selected}\nDecoded components: {components.Count}\nSkipped selections: {skipped.Count}\n");
        text.AppendLine("Only user-selected components are shown in their original coordinates. The assembly applies no additional translation, scale, inferred bone pose, authored character identity or automatic missing-part selection. Alternate components and LODs can overlap. A complete body is shown only when the selected, supported components actually contain it.\n");
        foreach (var component in components)
        {
            text.AppendLine(component.Label);
            text.AppendLine("Source: " + component.Document.SourcePath);
            text.AppendLine("Entry: " + component.Entry.Path);
            text.AppendLine($"Resource offset: 0x{component.Entry.Offset:X}; drawable: 0x{component.Model.DrawableOffset:X}");
            text.AppendLine($"Meshes: {component.Model.Meshes.Count}; UV meshes: {component.Model.Meshes.Count(m => m.TexCoords != null)}; bone bindings: {component.Model.HasBoneBindings}");
            text.AppendLine("Material map (source → assembly): " + (component.MaterialMap.Count == 0 ? "none" : string.Join(", ", component.MaterialMap.Select(p => p.Key + " → " + p.Value))));
            if (!string.IsNullOrEmpty(component.Model.PresentationNote)) text.AppendLine(component.Model.PresentationNote);
            text.AppendLine(component.TextureDetails); text.AppendLine();
        }
        if (skipped.Count > 0) { text.AppendLine("Omitted selections:"); foreach (string row in skipped) text.AppendLine("• " + row); text.AppendLine(); }
        if (notes.Count > 0) { text.AppendLine("Preview limitations:"); foreach (string row in notes.Distinct()) text.AppendLine("• " + row); text.AppendLine(); }
        text.AppendLine("Export OBJ preserves the selected components' object names, geometry, coordinates and supported UVs. Native material mappings are retained above and in the in-memory assembly; OBJ export does not include materials, texture images, skeletons or animation.");
        return text.ToString();
    }
}
