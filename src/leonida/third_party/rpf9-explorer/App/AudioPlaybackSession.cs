using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace RpfPreview.App;



internal sealed class AudioPlaybackSession : IDisposable
{
    private readonly MediaPlayer _media = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<double, bool, bool> _changed;
    private readonly string _path;
    private bool _disposed;
    public bool Playing { get; private set; }
    public bool Loop { get; set; }
    public double Duration => _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan.TotalSeconds : 0;
    public double Position => _disposed ? 0 : _media.Position.TotalSeconds;
    public Task Opened => _opened.Task;
    public string PcmPath => _path;

    public AudioPlaybackSession(string pcmPath, Action<double, bool, bool> changed, Action<string> error)
    {
        _path = pcmPath;
        _changed = changed;
        _media.MediaOpened += (_, _) => { _opened.TrySetResult(); Notify(); };
        _media.MediaFailed += (_, e) => {
            _opened.TrySetException(e.ErrorException);
            Playing = false;
            _timer.Stop();
            error(e.ErrorException.Message);
        };
        _media.MediaEnded += (_, _) => {
            if (Loop) { _media.Position = TimeSpan.Zero; _media.Play(); }
            else { Playing = false; _timer.Stop(); _changed(Duration, false, true); }
        };
        _timer.Tick += (_, _) => Notify();
        _media.Volume = .6;
        _media.Open(new Uri(Path.GetFullPath(pcmPath)));
    }

    public void Play()
    {
        if (_disposed) return;
        if (Position >= Duration - .01) _media.Position = TimeSpan.Zero;
        _media.Play(); Playing = true; _timer.Start(); Notify();
    }
    public void Pause() { if (_disposed) return; _media.Pause(); Playing = false; _timer.Stop(); Notify(); }
    public void Stop() { if (_disposed) return; Pause(); _media.Position = TimeSpan.Zero; Notify(); }
    public void Seek(double seconds) { if (_disposed) return; _media.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration)); Notify(); }
    public void Volume(double percent) { if (!_disposed) _media.Volume = Math.Clamp(percent / 100, 0, 1); }
    private void Notify() { if (!_disposed) _changed(Position, Playing, false); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _media.Close();
        _opened.TrySetCanceled();
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
