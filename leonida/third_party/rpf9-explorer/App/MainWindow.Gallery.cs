using RpfPreview.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RpfPreview.App;

public partial class MainWindow
{
    private const int ImagesPerPage = 96;
    private FileRow[] _galleryRows = [];
    private int _galleryPage;
    private CancellationTokenSource? _thumbnailCancellation;
    private Task? _thumbnailTask;
    private readonly SemaphoreSlim _thumbnailDecodeLock = new(1);
    private readonly Dictionary<string, ImageSource> _thumbnailCache = new();
    private sealed class ThumbnailContext { public string? ResourceKey; public byte[]? Resource; public int Split; }

    private void CancelThumbnails()
    {
        if (_thumbnailCancellation is not { } cancellation) return;
        _thumbnailCancellation = null; cancellation.Cancel();
        if (_thumbnailTask is { IsCompleted: false } task) _ = task.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
        else cancellation.Dispose();
    }
    private void ThumbnailMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || FilesGrid == null) return;
        if (ThumbnailsCheckBox.IsChecked == true) { _loading = true; AssetTypeFilter.SelectedIndex = 1; _loading = false; }
        RefreshFiles();
    }
    private void ConfigureGallery(FileRow[] rows, bool reset)
    {
        CancelThumbnails();
        bool show = ThumbnailsCheckBox.IsChecked == true;
        FilesGrid.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ImageGallery.Visibility = GalleryPaging.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { ImageGallery.ItemsSource = null; return; }
        _galleryRows = rows.Where(r => r.Asset?.IsImage == true).ToArray();
        if (reset) _galleryPage = 0;
        int pages = Math.Max(1, (_galleryRows.Length + ImagesPerPage - 1) / ImagesPerPage);
        _galleryPage = Math.Clamp(_galleryPage, 0, pages - 1);
        var page = _galleryRows.Skip(_galleryPage * ImagesPerPage).Take(ImagesPerPage).ToArray();
        ImageGallery.ItemsSource = page; ImagePageText.Text = $"Page {_galleryPage + 1:N0}/{pages:N0}";
        var cancellation = _thumbnailCancellation = new CancellationTokenSource();
        _thumbnailTask = LoadThumbnailPageAsync(page, cancellation.Token);
    }
    private async Task LoadThumbnailPageAsync(FileRow[] rows, CancellationToken token)
    {
        var context = new ThumbnailContext();
        foreach (var row in rows)
        {
            if (token.IsCancellationRequested) return;
            var asset = row.Asset!;
            try
            {
                var staged = FindImageReplacement(asset);
                string key = asset.SourcePath + "\0" + asset.Entry.Path + "\0" + asset.SubresourceIndex + "\0" + staged?.Record.Sha256;
                if (_thumbnailCache.TryGetValue(key, out var cached)) { row.Thumbnail = cached; row.ThumbnailStatus = ""; continue; }
                row.ThumbnailStatus = "Loading…";
                await _thumbnailDecodeLock.WaitAsync(token);
                ImageSource bitmap;
                try { bitmap = await Task.Run(() => DecodeWorkspaceThumbnail(asset, staged?.Path, context, token), token); }
                finally { _thumbnailDecodeLock.Release(); }
                if (token.IsCancellationRequested) return;
                if (_thumbnailCache.Count >= 512) _thumbnailCache.Clear();
                _thumbnailCache[key] = bitmap; row.Thumbnail = bitmap; row.ThumbnailStatus = "";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
            { row.ThumbnailStatus = "Preview unavailable"; row.Thumbnail = null; }
        }
    }
    private ImageSource DecodeWorkspaceThumbnail(WorkspaceAsset asset, string? stagedPath, ThumbnailContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); BitmapSource bitmap;
        if (stagedPath != null)
        {
            byte[] data = File.ReadAllBytes(stagedPath);
            if (Path.GetExtension(stagedPath).Equals(".dds", StringComparison.OrdinalIgnoreCase)) bitmap = PixelBitmap(DdsTextureReader.DecodeTexturePixels(data));
            else bitmap = DecodeImage(data);
        }
        else if (asset.Texture is { } texture)
        {
            string key = asset.SourcePath + "\0" + asset.Entry.Offset;
            if (context.ResourceKey != key)
            {
                context.Resource = ArchiveReader.ReadResourceData(asset.Document, asset.Entry, token);
                context.Split = asset.Entry.ResourceSystemBytes ?? checked((int)asset.Entry.Segments![0].Size); context.ResourceKey = key;
            }
            int mip = 0; while (mip + 1 < texture.MipCount && (Math.Max(1, texture.Width >> mip) > 192 || Math.Max(1, texture.Height >> mip) > 192)) mip++;
            bitmap = PixelBitmap(RageTextureDecoder.DecodeTexturePixelsAtMip(context.Resource.AsSpan(0, context.Split), context.Resource.AsSpan(context.Split), texture.Index, mip));
        }
        else if (asset.PoolTexture != null) bitmap = PixelBitmap(TtpfReader.DecodeThumbnail(asset.SourcePath, asset.PoolTexture.Index, token));
        else
        {
            byte[] data = asset.Document.ReadPreview(asset.Entry, 16 * 1024 * 1024, token);
            bitmap = asset.Entry.Extension == ".dds" ? PixelBitmap(DdsTextureReader.DecodeTexturePixels(data)) : DecodeImage(data);
        }
        if (bitmap.PixelWidth > 160 || bitmap.PixelHeight > 120)
        {
            double scale = Math.Min(160d / bitmap.PixelWidth, 120d / bitmap.PixelHeight);
            bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)); bitmap.Freeze();
        }
        token.ThrowIfCancellationRequested(); return bitmap;
    }
    private static BitmapSource PixelBitmap(RageTexturePixels pixels)
    {
        var image = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra32, pixels.Width * 4); image.Freeze(); return image;
    }
    private (ReplacementRecord Record, string Path)? FindImageReplacement(WorkspaceAsset asset)
    {
        if (!_sourceProjects.TryGetValue(asset.SourcePath, out var project)) return null;
        ReplacementTarget target = asset.Texture != null ? new(asset.Entry.Path, asset.Entry.Offset, "texture", asset.Texture.Index)
            : asset.PoolTexture != null ? new(asset.Entry.Path, asset.Entry.Offset, "texture", asset.PoolTexture.Index, asset.PoolTexture.AssetKey.ToString("X16"))
            : new(asset.Entry.Path, asset.Entry.Offset, "binary");
        var record = project.Find(target);
        return record == null ? null : (record, project.AssetPath(record));
    }
    private void Gallery_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((e.AddedItems.OfType<FileRow>().LastOrDefault() ?? ImageGallery.SelectedItem) is FileRow row) FilesGrid.SelectedItem = row;
        UpdateExtractButtons();
    }
    private void PreviousImagePage_Click(object sender, RoutedEventArgs e) { _galleryPage--; ConfigureGallery(_rows.ToArray(), false); }
    private void NextImagePage_Click(object sender, RoutedEventArgs e) { _galleryPage++; ConfigureGallery(_rows.ToArray(), false); }
}
