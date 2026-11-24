using RpfPreview.Core;
using System.Windows;

namespace RpfPreview.App;

public partial class MainWindow
{
    private bool _scanning;
    private async void ScanArchive_Click(object sender, RoutedEventArgs e)
        => await ScanEntireArchiveAsync();

    private async Task ScanEntireArchiveAsync()
    {
        if (_busy || _document?.Format != "RPF9") return;
        var document = _document;
        string? selected = (FilesGrid.SelectedItem as FileRow)?.Entry.Path;
        _previewCancellation?.Cancel(); CancelThumbnails(); ClosePlayer();
        _workCancellation = new CancellationTokenSource();
        _scanning = true;
        SetBusy(true, true); WorkProgress.IsIndeterminate = false; WorkProgress.Maximum = 1; WorkProgress.Value = 0;
        StatusText.Text = "Scanning the entire archive for supported payloads…";
        PayloadIndexResult? result = null;
        try
        {
            var progress = new Progress<PayloadIndexProgress>(p => { WorkProgress.Value = p.Fraction; StatusText.Text = p.Message; });
            result = await Task.Run(() => PayloadIndexBuilder.Build(document.SourcePath, progress, _workCancellation.Token), _workCancellation.Token);
        }
        catch (OperationCanceledException) { StatusText.Text = "Scan canceled. Completed valid indexes remain available."; }
        catch (Exception ex) { StatusText.Text = "Scan stopped: " + ex.Message; }
        finally { _workCancellation?.Dispose(); _workCancellation = null; _scanning = false; SetBusy(false); }
        if (result != null)
        {
            await OpenSourcesAsync(new[] { document.SourcePath }, true);
            FilesGrid.SelectedItem = _rows.FirstOrDefault(r => r.Asset?.SourcePath == document.SourcePath && r.Entry.Path == selected);
            StatusText.Text = $"Full source scan completed · {result.Models:N0} models · {result.TextureDictionaries:N0} texture dictionaries · {result.AudioBanks:N0} audio banks · {result.Shaders:N0} shaders. Original RPF9 directory remains undecoded.";
        }
    }
}
