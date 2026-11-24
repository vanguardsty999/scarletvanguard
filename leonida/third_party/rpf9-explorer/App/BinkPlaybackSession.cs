using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace RpfPreview.App;



internal sealed class BinkPlaybackSession : IDisposable
{
    internal sealed record VideoHeader(int Width, int Height, int SourceWidth, int SourceHeight,
        int Frames, double FrameRate, int AudioTracks, bool AudioEnabled);
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _registration;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _commandLock = new();
    private bool _disposed;
    public VideoHeader Header { get; private set; } = null!;
    public Task FirstFrame => _firstFrame.Task;
    public Task Completion { get; }

    public BinkPlaybackSession(string path, long offset, long length, CancellationToken cancellation,
        Func<byte[], int, bool, Task> frame, Func<int, bool, bool, Task> state, Action<string> error)
    {
        var helper = new[] {
            Path.Combine(AppContext.BaseDirectory, "Tools", "bink-player.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Tools", "bink-player.exe"))
        }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Keep Tools\\bink-player.exe beside rpf9-explorer.exe to play movies.");
        var start = new ProcessStartInfo(helper) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("0x" + offset.ToString("X"));
        start.ArgumentList.Add("0x" + length.ToString("X"));
        _process = new Process { StartInfo = start };
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try { if (!_process.Start()) throw new IOException("Could not start the video decoder."); }
        catch { _process.Dispose(); _lifetime.Dispose(); throw; }
        _registration = _lifetime.Token.Register(StopProcess);
        Completion = Task.Run(async () => {
            var errors = _process.StandardError.ReadToEndAsync();
            try
            {
                var stream = _process.StandardOutput.BaseStream;
                var intro = new byte[44];
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    startup.CancelAfter(TimeSpan.FromSeconds(15));
                    await stream.ReadExactlyAsync(intro, startup.Token);
                }
                int Read(int i) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(intro.AsSpan(i * 4, 4)));
                if (Read(0) != 0x50504652 || Read(1) != 1 || Read(2) is < 1 or > 1920 || Read(3) is < 1 or > 1080
                    || Read(6) < 1 || Read(7) < 1 || Read(8) < 1)
                    throw new InvalidDataException("Invalid video decoder response.");
                Header = new(Read(2), Read(3), Read(4), Read(5), Read(6), (double)Read(7) / Read(8), Read(9), Read(10) != 0);
                var pixels = new byte[checked(Header.Width * Header.Height * 4)];
                var packet = new byte[16];
                while (true)
                {
                    await stream.ReadExactlyAsync(packet, _lifetime.Token);
                    var magic = BinaryPrimitives.ReadUInt32LittleEndian(packet);
                    int Word(int i) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(i * 4, 4)));
                    var number = Word(1);
                    if (number < 1 || number > Header.Frames) throw new InvalidDataException("Invalid video frame number.");
                    if (magic == 0x4d415246)
                    {
                        if (Word(2) != pixels.Length) throw new InvalidDataException("Invalid video frame size.");
                        var playing = Word(3) != 0;
                        await stream.ReadExactlyAsync(pixels, _lifetime.Token);
                        await frame(pixels, number, playing);
                        _firstFrame.TrySetResult();
                    }
                    else if (magic == 0x54415453) await state(number, Word(2) != 0, Word(3) != 0);
                    else throw new InvalidDataException("Unknown video decoder packet.");
                }
            }
            catch (Exception ex)
            {
                if (_lifetime.IsCancellationRequested) _firstFrame.TrySetCanceled();
                else
                {
                    StopProcess();
                    string detail;
                    try { detail = (await errors.WaitAsync(TimeSpan.FromSeconds(2))).Trim(); }
                    catch { detail = ""; }
                    var message = detail.Length > 0 ? detail : ex is OperationCanceledException ? "The video decoder did not respond in time." : ex.Message;
                    _firstFrame.TrySetException(new IOException(message, ex));
                    error(message);
                }
            }
        });
    }

    public void Send(string command)
    {
        lock (_commandLock)
        {
            if (_disposed || _lifetime.IsCancellationRequested) return;
            try { _process.StandardInput.WriteLine(command); _process.StandardInput.Flush(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        }
    }
    private void StopProcess()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        lock (_commandLock) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel();
        _registration.Dispose();
        _ = Completion.ContinueWith(_ => { _process.Dispose(); _lifetime.Dispose(); }, TaskScheduler.Default);
    }
}
