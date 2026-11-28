using RpfPreview.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RpfPreview.App;

public partial class MainWindow
{
    private BinkPlaybackSession? _player;
    private WriteableBitmap? _videoBitmap;
    private Window? _fullscreen;
    private bool _playing, _updatingSeek, _draggingSeek;
    private int _videoFrame;
    private long _presentedFrames;

    private async Task PreviewRecoveredVideoAsync(ArchiveDocument document, ArchiveEntry entry, CancellationToken token)
    {
        var info = ArchiveReader.GetVideoInfo(document, entry)
            ?? throw new InvalidDataException("The video's frame index or bounds could not be validated.");
        token.ThrowIfCancellationRequested();
        TextPreview.Text = $"Bink video\n\nDimensions: {info.Width:N0} × {info.Height:N0}\nFrame rate: {info.FramesPerSecond:0.###} fps\nDuration: {info.DurationSeconds:0.###} seconds\nFrames: {info.Frames:N0}\nAudio tracks: {info.AudioTracks:N0}\n\nSource offset: 0x{entry.Offset:X}\nStored size: {FormatSize(entry.StoredSize)}\n\nRecovered stream. The original filename and folder are unknown.";
        PreviewMetaText.Text = $"{info.Width:N0} × {info.Height:N0}  ·  {info.FramesPerSecond:0.###} fps  ·  {Clock(info.DurationSeconds)}  ·  {info.AudioTracks} audio track(s)";
        HexPreview.Text = FormatHex(document.ReadPreview(entry, 4096, token));
        PreviewFootnoteText.Text = "Opening movie…";
        BinkPlaybackSession? session = null;
        session = new BinkPlaybackSession(document.SourcePath, entry.Offset, entry.StoredSize, token,
            async (pixels, frame, playing) => await Dispatcher.InvokeAsync(() => {
                if (token.IsCancellationRequested || _player != session) return;
                var header = session!.Header;
                if (_videoBitmap == null)
                {
                    _videoBitmap = new WriteableBitmap(header.Width, header.Height, 96, 96, PixelFormats.Bgra32, null);
                    ImagePreview.Source = _videoBitmap;
                    ImageTab.Header = "Video";
                    ImageTab.IsEnabled = true;
                    VideoControls.Visibility = Visibility.Visible;
                    PreviewTabs.SelectedItem = ImageTab;
                    _updatingSeek = true;
                    SeekSlider.Minimum = 1;
                    SeekSlider.Maximum = Math.Max(1, header.Frames);
                    SeekSlider.Value = 1;
                    _updatingSeek = false;
                    VolumeSlider.IsEnabled = header.AudioEnabled;
                    AudioLabel.Text = header.AudioTracks == 0 ? "No audio" : header.AudioEnabled ? "Volume" : "Audio unavailable";
                    VolumeSlider.ToolTip = header.AudioTracks > 1 ? "Plays the first audio track" : "Volume";
                    PreviewFootnoteText.Text = "Ready to play · double-click the video for full screen";
                }
                _videoBitmap.WritePixels(new Int32Rect(0, 0, header.Width, header.Height), pixels, header.Width * 4, 0);
                _presentedFrames++;
                UpdatePlaybackState(frame, playing, false);
            }),
            async (frame, playing, ended) => await Dispatcher.InvokeAsync(() => {
                if (!token.IsCancellationRequested && _player == session) UpdatePlaybackState(frame, playing, ended);
            }),
            message => Dispatcher.BeginInvoke(() => {
                if (token.IsCancellationRequested || _player != session) return;
                _playing = false;
                PlayPauseButton.Content = "Play";
                VideoControls.IsEnabled = false;
                PreviewFootnoteText.Text = "Playback stopped: " + message;
            }));
        _player = session;
        session.Send("VOLUME " + (int)VolumeSlider.Value);
        session.Send("LOOP " + (LoopCheckBox.IsChecked == true ? "1" : "0"));
        await session.FirstFrame.WaitAsync(TimeSpan.FromSeconds(20), token);
    }

    private void UpdatePlaybackState(int frame, bool playing, bool ended)
    {
        _videoFrame = frame;
        _playing = playing;
        PlayPauseButton.Content = playing ? "Pause" : "Play";
        if (!_draggingSeek)
        {
            _updatingSeek = true;
            SeekSlider.Value = frame;
            _updatingSeek = false;
            UpdatePlaybackClock(frame);
        }
        if (ended) PreviewFootnoteText.Text = "End of movie · press Play to watch again";
        else PreviewFootnoteText.Text = (playing ? "Playing" : "Paused") + " · double-click the video for full screen";
    }
    private void UpdatePlaybackClock(double frame)
    {
        if (_audioPlayer != null) { UpdateAudioClock(frame); return; }
        if (_player?.Header is not { } header) return;
        PlaybackTimeText.Text = $"{Clock(Math.Max(0, frame - 1) / header.FrameRate)} / {Clock(header.Frames / header.FrameRate)}";
        SeekSlider.ToolTip = $"Frame {(int)frame:N0} of {header.Frames:N0}";
    }
    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }
    private void ClosePlayer()
    {
        ResetAssetPreview();
        _fullscreen?.Close();
        _fullscreen = null;
        _player?.Dispose();
        _player = null;
        _videoBitmap = null;
        _playing = false;
        _draggingSeek = false;
        _videoFrame = 0;
        if (VideoControls != null) { VideoControls.Visibility = Visibility.Collapsed; VideoControls.IsEnabled = true; }
    }
    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_audioPlayer != null) { if (_audioPlayer.Playing) _audioPlayer.Pause(); else _audioPlayer.Play(); return; }
        if (_player == null) return;
        _playing = !_playing;
        _player.Send(_playing ? "PLAY" : "PAUSE");
        PlayPauseButton.Content = _playing ? "Pause" : "Play";
    }
    private void StopVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_audioPlayer != null) { _audioPlayer.Stop(); return; }
        _player?.Send("PAUSE"); _player?.Send("SEEK 1");
    }
    private void StepVideo_Click(object sender, RoutedEventArgs e) => _player?.Send("STEP");
    private void Loop_Click(object sender, RoutedEventArgs e) { if (_audioPlayer != null) _audioPlayer.Loop = LoopCheckBox.IsChecked == true; _player?.Send("LOOP " + (LoopCheckBox.IsChecked == true ? "1" : "0")); }
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { _audioPlayer?.Volume(e.NewValue); _player?.Send("VOLUME " + (int)e.NewValue); }
    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSeek) UpdatePlaybackClock(e.NewValue);
    }
    private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e) { _draggingSeek = true; }
    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e) { CommitSeek(); }
    private void SeekSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) CommitSeek();
    }
    private void CommitSeek()
    {
        _draggingSeek = false;
        if (_audioPlayer != null) { _audioPlayer.Seek(SeekSlider.Value); return; }
        _player?.Send("SEEK " + (int)Math.Round(SeekSlider.Value));
    }
    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Fullscreen_Click(sender, e);
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_videoBitmap == null || _player == null) return;
        if (_fullscreen != null) { _fullscreen.Activate(); return; }
        var image = new Image { Source = _videoBitmap, Stretch = Stretch.Uniform };
        var grid = new Grid { Background = Brushes.Black };
        grid.Children.Add(image);
        var close = new Button { Content = "Exit full screen (Esc)", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(15), Opacity = .8 };
        grid.Children.Add(close);
        var hint = new TextBlock { Text = "Space: play / pause     ← / →: seek 5 seconds", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16) };
        grid.Children.Add(hint);
        var window = _fullscreen = new Window { Owner = this, Title = "Movie preview", Content = grid, Background = Brushes.Black, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, WindowState = WindowState.Maximized };
        close.Click += (_, _) => window.Close();
        window.PreviewKeyDown += (_, key) => {
            if (key.Key == Key.Escape) window.Close();
            else if (key.Key == Key.Space) PlayPause_Click(this, new RoutedEventArgs());
            else if (key.Key is Key.Left or Key.Right && _player?.Header is { } header)
                _player.Send("SEEK " + Math.Clamp(_videoFrame + (int)(header.FrameRate * (key.Key == Key.Left ? -5 : 5)), 1, header.Frames));
            else return;
            key.Handled = true;
        };
        image.MouseLeftButtonDown += (_, mouse) => { if (mouse.ClickCount == 2) window.Close(); };
        window.Closed += (_, _) => { if (_fullscreen == window) _fullscreen = null; };
        window.Show();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
