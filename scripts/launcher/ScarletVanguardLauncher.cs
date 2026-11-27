// Scarlet Vanguard DX12 port launcher.
// Build with scripts\build-launcher.ps1. ASCII source for csc.exe compatibility.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ScarletVanguardLauncher
{
    internal static class Paths
    {
        // The launcher may be moved with the repository.  Resolve the root from bin\ instead
        // of pinning a developer machine path into a distributable binary.
        public static string Repo { get { return Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory).FullName; } }
        public const string DefaultGame = "";
        public const string ExpectedExeSha256 = "";
        public const string DxcDir = @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64";
        public const string XclData = @"F:\XCL-data";
        public const string VictoryCache = @"F:\victory-cache";
        public const string VictoryData = @"F:\victory-data";
        private static readonly string SavedGameFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScarletVanguard", "launcher-game-dir.txt");
        private static string savedGame = LoadSavedGame();

        public static string Game
        {
            get
            {
                string value = Environment.GetEnvironmentVariable("SV_GAME_DIR");
                if (!string.IsNullOrEmpty(value)) return value;
                return string.IsNullOrEmpty(savedGame) ? DefaultGame : savedGame;
            }
        }

        public static void SetGameDirectory(string path)
        {
            savedGame = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(SavedGameFile);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SavedGameFile, savedGame, Encoding.UTF8);
        }

        private static string LoadSavedGame()
        {
            try
            {
                if (!File.Exists(SavedGameFile)) return "";
                return File.ReadAllText(SavedGameFile, Encoding.UTF8).Trim();
            }
            catch (Exception) { return ""; }
        }

        public static string Bin { get { return Path.Combine(Repo, "bin"); } }
        public static string Xcl { get { return Path.Combine(Repo, @"xcl\target\release\xcl.exe"); } }
        public static string Manifest { get { return Path.Combine(Bin, "ScarletVanguard-ShaderManifest.txt"); } }
        public static string Logs { get { return Path.Combine(Repo, "measurements", "launcher"); } }
        public static string BrandImage { get { return Path.Combine(Repo, @"assets\branding\scarlet-vanguard-banner.png"); } }
        public static string Exe { get { return Path.Combine(Game, "game_scarlett_master.exe"); } }
        public static string Shaders { get { return Path.Combine(Game, "shaders.rpf"); } }
    }

    internal sealed class Validation
    {
        public int FileCount;
        public long ExeBytes;
        public string ExeHash = "";
        public long RpfBytes;
        public string RpfHash = "";
        public string RpfMagic = "";
        public readonly List<string> Locked = new List<string>();
        public string Splash = "";
        public int CacheFiles;
        public long CacheBytes;
        public bool ManifestOk;
        public string Summary = "";

        public bool ExeOk
        {
            get
            {
                return !string.IsNullOrEmpty(ExeHash) &&
                    (string.IsNullOrEmpty(Paths.ExpectedExeSha256) ||
                    string.Equals(ExeHash, Paths.ExpectedExeSha256, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool RpfOk
        {
            get { return RpfMagic == "9FPR"; }
        }
    }

    public class MainForm : Form
    {
        private PictureBox splash;
        private Label status;
        private Label hint;
        private Label telemetryLeft;
        private Label telemetryRight;
        private System.Windows.Forms.Timer telemetryTimer;
        private ProgressBar progress;
        private Button updateButton;
        private Button startButton;
        private Button focusButton;
        private Button openLogButton;
        private Button copyDiagnosticsButton;
        private Button browseGameButton;
        private RichTextBox log;
        private Label logCaption;
        private Process gameProcess;
        private Validation lastValidation;
        private bool autoStart;
        private bool dred;
        private bool focusPending;
        private bool precompileActive;
        private bool precompileLaunchedHere;
        private int precompileShadersDone;
        private int precompileShadersTotal;
        private int precompileElapsedSeconds;
        private int precompilePipelines;
        private string runtimeLogPath = "";
        private string gpuInfo = "";
        private string lastStatusText = "";

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public MainForm()
        {
            Text = "Scarlet Vanguard - Compatibility Runtime";
            Size = new Size(1120, 760);
            MinimumSize = new Size(960, 650);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 20, 26);
            ForeColor = Color.FromArgb(235, 238, 245);
            Font = new Font("Segoe UI", 9F);

            splash = new PictureBox();
            splash.Dock = DockStyle.Top;
            splash.Height = 218;
            splash.SizeMode = PictureBoxSizeMode.Zoom;
            splash.BackColor = Color.Black;

            status = new Label();
            status.Dock = DockStyle.Top;
            status.Height = 58;
            status.Padding = new Padding(18, 8, 18, 4);
            status.BackColor = Color.FromArgb(27, 33, 44);
            status.ForeColor = Color.FromArgb(235, 238, 245);
            status.Font = new Font("Segoe UI", 9F, FontStyle.Bold);

            hint = new Label();
            hint.Dock = DockStyle.Top;
            hint.Height = 28;
            hint.Padding = new Padding(18, 4, 18, 2);
            hint.BackColor = Color.FromArgb(27, 33, 44);
            hint.ForeColor = Color.FromArgb(161, 203, 236);
            hint.Text = "Selecione uma instalacao autorizada. O launcher verifica os arquivos e utiliza a arte do titulo quando uma logo for encontrada.";

            Panel telemetry = new Panel();
            telemetry.Dock = DockStyle.Top;
            telemetry.Height = 122;
            telemetry.BackColor = Color.FromArgb(17, 22, 30);

            telemetryRight = MakeTelemetryLabel();
            telemetryRight.Dock = DockStyle.Fill;
            telemetry.Controls.Add(telemetryRight);

            telemetryLeft = MakeTelemetryLabel();
            telemetryLeft.Dock = DockStyle.Left;
            telemetryLeft.Width = 548;
            telemetry.Controls.Add(telemetryLeft);

            telemetryTimer = new System.Windows.Forms.Timer();
            telemetryTimer.Interval = 1000;
            telemetryTimer.Tick += delegate { UpdateTelemetry(); };

            progress = new ProgressBar();
            progress.Dock = DockStyle.Top;
            progress.Height = 4;
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 30;
            progress.Visible = false;

            Panel buttons = new Panel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 72;
            buttons.Padding = new Padding(18, 12, 18, 12);
            buttons.BackColor = Color.FromArgb(27, 33, 44);

            updateButton = MakeButton("Atualizar shaders", 18, 154, false);
            updateButton.Click += delegate { UpdateShaders(); };
            buttons.Controls.Add(updateButton);

            startButton = MakeButton("Iniciar jogo", 180, 188, true);
            startButton.Click += delegate { StartGame(); };
            buttons.Controls.Add(startButton);

            focusButton = MakeButton("Trazer jogo a frente", 372, 178, false);
            focusButton.Click += delegate { FocusGame(); };
            focusButton.Enabled = false;
            buttons.Controls.Add(focusButton);

            copyDiagnosticsButton = MakeButton("Copiar diagnostico", 554, 154, false);
            copyDiagnosticsButton.Click += delegate { CopyDiagnostics(); };
            buttons.Controls.Add(copyDiagnosticsButton);

            openLogButton = MakeButton("Abrir log", 712, 116, false);
            openLogButton.Click += delegate { OpenRuntimeLog(); };
            openLogButton.Enabled = false;
            buttons.Controls.Add(openLogButton);

            browseGameButton = MakeButton("Procurar pasta do jogo", 832, 200, false);
            browseGameButton.Click += delegate { ChooseGameFolder(); };
            buttons.Controls.Add(browseGameButton);

            logCaption = new Label();
            logCaption.Dock = DockStyle.Top;
            logCaption.Height = 29;
            logCaption.Padding = new Padding(18, 7, 18, 3);
            logCaption.BackColor = Color.FromArgb(14, 18, 25);
            logCaption.ForeColor = Color.FromArgb(146, 181, 215);
            logCaption.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            logCaption.Text = "ATIVIDADE RECENTE  |  eventos do launcher e caminho do runtime";

            log = new RichTextBox();
            log.Dock = DockStyle.Fill;
            log.ReadOnly = true;
            log.BorderStyle = BorderStyle.None;
            log.BackColor = Color.FromArgb(10, 14, 20);
            log.ForeColor = Color.FromArgb(204, 218, 234);
            log.Font = new Font("Consolas", 9.25F);
            log.Padding = new Padding(18, 10, 18, 10);

            Controls.Add(log);
            Controls.Add(logCaption);
            Controls.Add(buttons);
            Controls.Add(progress);
            Controls.Add(telemetry);
            Controls.Add(hint);
            Controls.Add(status);
            Controls.Add(splash);

            Load += delegate { OnLoaded(); };
        }

        private static Label MakeTelemetryLabel()
        {
            Label label = new Label();
            label.BackColor = Color.FromArgb(17, 22, 30);
            label.ForeColor = Color.FromArgb(190, 216, 240);
            label.Font = new Font("Consolas", 8.75F);
            label.Padding = new Padding(18, 7, 10, 4);
            label.AutoSize = false;
            label.AutoEllipsis = true;
            return label;
        }

        private Button MakeButton(string text, int left, int width, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.Left = left;
            b.Top = 16;
            b.Width = width;
            b.Height = 42;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Color.FromArgb(95, 182, 255) : Color.FromArgb(77, 114, 154);
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(39, 111, 180) : Color.FromArgb(42, 62, 86);
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(31, 93, 151) : Color.FromArgb(35, 52, 73);
            b.BackColor = primary ? Color.FromArgb(32, 103, 170) : Color.FromArgb(31, 47, 67);
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI", 9F, primary ? FontStyle.Bold : FontStyle.Regular);
            return b;
        }

        private void OnLoaded()
        {
            Log(string.IsNullOrEmpty(Paths.Game) ? "Nenhuma pasta de jogo selecionada. Use Procurar pasta do jogo." : "Game folder: " + Paths.Game);
            if (dred) Log("D3D12 DRED diagnostics enabled for this run.");
            LoadSplash(FindSplash());
            gpuInfo = DetectGpu();
            telemetryTimer.Start();
            UpdateTelemetry();
            RefreshStatus(true);
        }

        public void QueueAutoStart()
        {
            autoStart = true;
        }

        public void EnableDred()
        {
            dred = true;
        }

        private void SetBusy(bool busy)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetBusy(busy)));
                return;
            }
            updateButton.Enabled = !busy;
            startButton.Enabled = !busy;
            browseGameButton.Enabled = !busy;
            if (busy && !precompileActive) progress.Style = ProgressBarStyle.Marquee;
            progress.Visible = busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void Log(string line)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => Log(line)));
                return;
            }
            string entry = DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine;
            string lower = line.ToLowerInvariant();
            Color color = Color.FromArgb(204, 218, 234);
            if (lower.Contains("failed") || lower.Contains("error") || lower.Contains("exit") || lower.Contains("lock"))
                color = Color.FromArgb(255, 151, 151);
            else if (lower.Contains("started") || lower.Contains("ok") || lower.Contains("valid") || lower.Contains("focused"))
                color = Color.FromArgb(140, 232, 174);
            else if (lower.Contains("waiting") || lower.Contains("stale") || lower.Contains("check"))
                color = Color.FromArgb(255, 210, 126);
            log.SelectionStart = log.TextLength;
            log.SelectionColor = color;
            log.AppendText(entry);
            log.SelectionColor = log.ForeColor;
            log.ScrollToCaret();
        }

        private void RunAsync<T>(Func<T> work, Action<T, Exception> done)
        {
            SetBusy(true);
            Thread thread = new Thread(() =>
            {
                T result = default(T);
                Exception error = null;
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        SetBusy(false);
                        if (done != null) done(result, error);
                        else if (error != null) Log("Error: " + error.Message);
                    }));
                }
                catch (ObjectDisposedException) { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void RefreshStatus(bool autoPrecompile)
        {
            RunAsync<Validation>(Scan, delegate(Validation v, Exception error)
            {
                if (error != null)
                {
                    Log("Validation failed: " + error.Message);
                    return;
                }
                lastValidation = v;
                ShowValidation(v);
                if (autoStart && v.ManifestOk)
                {
                    autoStart = false;
                    Log("Auto-start requested; starting game.");
                    StartGame();
                    return;
                }
                if (autoPrecompile && !v.ManifestOk && IsPrecompileRunning())
                {
                    BeginPrecompileStatus(false);
                    Log("Shader precompile is already running; waiting for it to finish.");
                }
                else if (autoPrecompile && !v.ManifestOk)
                {
                    Log("Pre-compilacao completa de shaders necessaria. Clique em Atualizar shaders antes de iniciar.");
                }
            });
        }

        private void ShowValidation(Validation v)
        {
            bool ready = v.ExeOk && v.RpfOk && v.Locked.Count == 0 && v.ManifestOk;
            string state = ready ? "PRONTO PARA INICIAR" : "PREPARACAO NECESSARIA";
            string locks = v.Locked.Count == 0 ? "nenhum arquivo em uso" : v.Locked.Count + " arquivo(s) em uso";
            status.Text =
                state + "  |  " + (v.ExeOk ? "executavel verificado" : "executavel invalido") +
                "  |  " + (v.RpfOk ? "shaders.rpf OK" : "shaders.rpf invalido") + Environment.NewLine +
                "Cache DXIL: " + v.CacheFiles + " arquivos / " + Mb(v.CacheBytes) + " MB" +
                "  |  Manifest: " + (v.ManifestOk ? "valido" : "ausente ou desatualizado") +
                "  |  " + locks;
            status.ForeColor = ready ? Color.FromArgb(140, 232, 174) : Color.FromArgb(255, 210, 126);
            status.BackColor = ready ? Color.FromArgb(20, 49, 43) : Color.FromArgb(57, 45, 27);
            lastStatusText = status.Text;
        }

        private void UpdateTelemetry()
        {
            MonitorExternalPrecompile();
            Process title = FindTitleProcess();
            string tail = ReadLogTail(runtimeLogPath);

            string frames = GrabLast(tail, @"(\d+)\s+frames presented", 1);
            string fps = GrabLast(tail, @"(\d+(?:\.\d+)?)\s+fps over the last", 1);
            string draws = GrabLast(tail, @"(\d+)\s+draws with translated pipelines", 1);
            string dispatches = GrabLast(tail, @"(\d+)\s+dispatches", 1);
            string coherent = GrabLast(tail, @"coherent\s+(\d+/\d+ MB)", 1);
            string sparse = GrabLast(tail, @"sparse\s+(\d+) MB", 1);
            string gpuLine = Grab(tail, @"GPU:\s*([^\r\n]+)", 1);
            string dxc = Grab(tail, @"DXC\s+(\d+\.\d+\.\d+\.\d+)", 1);
            string waves = Grab(tail, @"waves of\s+(\d+)", 1);

            string windowTitle = title == null ? "" : title.MainWindowTitle;
            string titleFps = Grab(windowTitle, @"(\d+(?:\.\d+)?)\s+FPS", 1);
            string running = Grab(windowTitle, @"compiling shaders:\s*(\d+)\s+running", 1);
            string done = Grab(windowTitle, @"(\d+)\s+done", 1);
            string queued = Grab(windowTitle, @"(\d+)\s+pipelines queued", 1);

            Validation v = lastValidation;
            string manifest = v == null ? "not scanned" : (v.ManifestOk ? "valid" : "stale");
            string cache = v == null ? "-" : (v.CacheFiles + " files / " + Mb(v.CacheBytes) + " MB");
            string summary = v == null ? "" : v.Summary;
            string shaderState;
            if (running != "" || done != "" || queued != "")
                shaderState = running + " running, " + done + " done, " + queued + " queued";
            else
                shaderState = "idle (cache ready at title start)";

            string gpu = string.IsNullOrEmpty(gpuLine) ? gpuInfo : gpuLine;
            telemetryLeft.Text =
                "SHADERS   manifest " + manifest + " | cache DXIL " + cache + Environment.NewLine +
                "          ultima verificacao: " + Clip(summary, 68) + Environment.NewLine +
                "          fila atual: " + Clip(shaderState, 74) + Environment.NewLine +
                "GRAFICOS  " + Clip(gpu, 76) + Environment.NewLine +
                "          D3D12 wave " + (waves == "" ? "-" : waves) + " | DXC " + (dxc == "" ? "-" : dxc);

            string perf;
            if (title == null)
            {
                perf = frames == ""
                    ? "aguardando os primeiros frames"
                    : "ultima sessao: " + (fps == "" ? "-" : fps) + " fps | " + frames + " frames | " +
                      draws + " draws | " + dispatches + " dispatches";
            }
            else
            {
                perf = (fps == "" ? titleFps : fps) + " fps | " + frames + " frames | " +
                       draws + " draws | " + dispatches + " dispatches";
            }
            string mem = "coherent " + (coherent == "" ? "-" : coherent) +
                         " | sparse " + (sparse == "" ? "-" : sparse) + " MB";
            string proc = "jogo nao iniciado";
            if (title != null)
            {
                try
                {
                    proc = "PID " + title.Id + " | CPU " + title.TotalProcessorTime.TotalSeconds.ToString("0.0") + "s | WS " +
                           Mb(title.WorkingSet64) + " MB | " + (title.Responding ? "respondendo" : "sem resposta");
                }
                catch (Exception) { proc = "PID " + title.Id; }
            }
            string resolvedLogPath = ResolveRuntimeLogPath();
            string logName = string.IsNullOrEmpty(resolvedLogPath) ? "-" : Path.GetFileName(resolvedLogPath);
            telemetryRight.Text =
                "DESEMPENHO " + Clip(perf, 76) + Environment.NewLine +
                "          " + Clip(mem, 80) + Environment.NewLine +
                "PROCESSO  " + Clip(proc, 80) + Environment.NewLine +
                "LOG ATUAL " + Clip(logName, 80);
            openLogButton.Enabled = !string.IsNullOrEmpty(resolvedLogPath);

            if (title != null)
            {
                focusButton.Enabled = true;
                string showFps = fps == "" ? titleFps : fps;
                string s = "JOGO EM EXECUCAO" + (dred ? " (DRED ATIVO)" : "") + " | PID " + title.Id + " | " + showFps + " fps | " + frames + " frames" +
                           Environment.NewLine +
                            "Pipelines: " + draws + " draws | " + dispatches + " dispatches | manifest " + manifest + " | locks " + (v == null ? 0 : v.Locked.Count);
                if (s != lastStatusText)
                {
                    status.Text = s;
                    lastStatusText = s;
                }
                status.ForeColor = Color.FromArgb(126, 232, 168);
                status.BackColor = Color.FromArgb(20, 49, 43);
                if (focusPending)
                {
                    focusPending = false;
                    FocusProcess(title);
                }
            }
            else
            {
                focusButton.Enabled = false;
                if (!precompileActive && v != null) ShowValidation(v);
            }

            if (title != null) title.Dispose();
        }

        private void FocusGame()
        {
            Process title = FindTitleProcess();
            if (title == null)
            {
                Log("Game window not found yet; it will be focused as soon as it appears.");
                return;
            }
            try { FocusProcess(title); }
            finally { title.Dispose(); }
        }

        private void ChooseGameFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Selecione a pasta que contem game_scarlett_master.exe";
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(Paths.Game)) dialog.SelectedPath = Paths.Game;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string exe = Path.Combine(dialog.SelectedPath, "game_scarlett_master.exe");
                if (!File.Exists(exe))
                {
                    MessageBox.Show(this,
                        "A pasta selecionada nao contem game_scarlett_master.exe. Escolha a pasta raiz do jogo.",
                        "Arquivo do jogo nao encontrado", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    Paths.SetGameDirectory(dialog.SelectedPath);
                    runtimeLogPath = "";
                    lastValidation = null;
                    LoadSplash(FindSplash());
                    Log("Pasta do jogo selecionada: " + Paths.Game);
                    RefreshStatus(true);
                }
                catch (Exception e)
                {
                    MessageBox.Show(this, "Nao foi possivel salvar a pasta do jogo: " + e.Message,
                        "Scarlet Vanguard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OpenRuntimeLog()
        {
            string path = ResolveRuntimeLogPath();
            if (string.IsNullOrEmpty(path))
            {
                Log("Nenhum log de runtime esta disponivel. Inicie o jogo primeiro.");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Log("Nao foi possivel abrir o log: " + e.Message);
            }
        }

        private void CopyDiagnostics()
        {
            try
            {
                Validation v = lastValidation;
                string text = "Scarlet Vanguard diagnostic" + Environment.NewLine +
                    "Game folder: " + Paths.Game + Environment.NewLine +
                    "Runtime log: " + (string.IsNullOrEmpty(runtimeLogPath) ? "not started" : runtimeLogPath) + Environment.NewLine +
                    "Files: " + (v == null ? "not scanned" : v.FileCount.ToString()) + Environment.NewLine +
                    "Executable: " + (v != null && v.ExeOk ? "verified" : "not verified") + Environment.NewLine +
                    "Shaders: " + (v != null && v.RpfOk ? v.RpfMagic + " OK" : "not verified") + Environment.NewLine +
                    "Locks: " + (v == null ? "not scanned" : v.Locked.Count.ToString()) + Environment.NewLine +
                    "Manifest: " + (v != null && v.ManifestOk ? "valid" : "missing or stale") + Environment.NewLine +
                    "GPU: " + gpuInfo;
                Clipboard.SetText(text);
                Log("Diagnostico copiado para a area de transferencia.");
            }
            catch (Exception e)
            {
                Log("Nao foi possivel copiar o diagnostico: " + e.Message);
            }
        }

        private void FocusProcess(Process title)
        {
            try
            {
                ShowWindow(title.MainWindowHandle, 9);
                SetForegroundWindow(title.MainWindowHandle);
                Log("Game window focused. Title PID " + title.Id + ".");
            }
            catch (Exception e)
            {
                Log("Could not focus the game window: " + e.Message);
            }
        }

        private static Process FindTitleProcess()
        {
            foreach (Process process in Process.GetProcessesByName("xcl"))
            {
                try
                {
                    string text = process.MainWindowTitle;
                    if (!string.IsNullOrEmpty(text) &&
                        (text.IndexOf("Grand Theft Auto", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         text.StartsWith("XCL -", StringComparison.OrdinalIgnoreCase)))
                        return process;
                }
                catch (Exception) { }
                try { process.Dispose(); } catch (Exception) { }
            }
            return null;
        }

        private string ResolveRuntimeLogPath()
        {
            if (!string.IsNullOrEmpty(runtimeLogPath) && File.Exists(runtimeLogPath)) return runtimeLogPath;
            try
            {
                if (!Directory.Exists(Paths.Logs)) return "";
                FileInfo[] files = new DirectoryInfo(Paths.Logs).GetFiles("game-*.log");
                if (files.Length == 0) return "";
                Array.Sort(files, delegate(FileInfo a, FileInfo b)
                {
                    return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
                });
                return files[0].FullName;
            }
            catch (Exception) { return ""; }
        }

        private string ReadLogTail(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    path = ResolveRuntimeLogPath();
                    if (string.IsNullOrEmpty(path)) return "";
                }
                StringBuilder sb = new StringBuilder();
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int headLength = (int)Math.Min(stream.Length, 8192);
                    byte[] head = new byte[headLength];
                    stream.Seek(0, SeekOrigin.Begin);
                    int headRead = stream.Read(head, 0, headLength);
                    sb.Append(Encoding.ASCII.GetString(head, 0, headRead));
                    sb.Append(Environment.NewLine);
                    stream.Seek(Math.Max(0, stream.Length - 131072), SeekOrigin.Begin);
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        sb.Append(reader.ReadToEnd());
                    }
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        private static string DetectGpu()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, DriverVersion FROM Win32_VideoController WHERE Name LIKE '%NVIDIA%'"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        object name = item["Name"];
                        object driver = item["DriverVersion"];
                        string value = name == null ? "" : name.ToString();
                        string version = driver == null ? "" : driver.ToString();
                        item.Dispose();
                        if (!string.IsNullOrEmpty(value))
                            return value + (version.Length == 0 ? "" : " (driver " + version + ")");
                    }
                }
            }
            catch (Exception) { }
            return "GPU not detected";
        }

        private static string Grab(string text, string pattern, int group)
        {
            try
            {
                Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                return match.Success ? match.Groups[group].Value.Trim() : "";
            }
            catch (Exception) { return ""; }
        }

        private static string GrabLast(string text, string pattern, int group)
        {
            try
            {
                MatchCollection matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                return matches.Count == 0 ? "" : matches[matches.Count - 1].Groups[group].Value.Trim();
            }
            catch (Exception) { return ""; }
        }

        private static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max - 3) + "...";
        }

        private void UpdateShaders()
        {
            if (IsGameRunning())
            {
                MessageBox.Show(this, "Close the game before updating shaders.", "Scarlet Vanguard");
                return;
            }
            if (IsPrecompileRunning())
            {
                MessageBox.Show(this, "A shader precompile is already running.", "Scarlet Vanguard");
                return;
            }
            BeginPrecompileStatus(true);
            RunAsync<string>(delegate
            {
                Validation v = Scan();
                if (!v.ExeOk || !v.RpfOk) throw new InvalidOperationException("Game files failed validation.");
                return Precompile(true);
            }, delegate(string summary, Exception error)
            {
                precompileActive = false;
                precompileLaunchedHere = false;
                if (error != null) Log("Update failed: " + error.Message);
                else
                {
                    Log("Update OK: " + summary);
                    WriteManifest(lastValidation, summary);
                    RefreshStatus(false);
                }
            });
        }

        private void StartGame()
        {
            if (IsGameRunning())
            {
                MessageBox.Show(this, "The game is already running.", "Scarlet Vanguard");
                return;
            }
            RunAsync<Process>(delegate
            {
                Validation v = Scan();
                if (!v.ExeOk || !v.RpfOk) throw new InvalidOperationException("Game files failed validation.");
                if (v.Locked.Count > 0)
                {
                    throw new IOException("Exclusive locks remain: " + string.Join(", ", v.Locked.ToArray()) +
                        ". Wait for the application holding them to finish; this launcher never closes other apps.");
                }
                if (!v.ManifestOk)
                    throw new InvalidOperationException("A pre-compilacao completa esta ausente ou desatualizada. Clique em Atualizar shaders e aguarde o fim antes de iniciar.");
                if (!File.Exists(Paths.Xcl)) throw new FileNotFoundException("xcl.exe not found", Paths.Xcl);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string logPath = Path.Combine(Paths.Logs, "game-" + stamp + ".log");
                runtimeLogPath = logPath;
                return StartRuntime(Paths.Xcl, "run \"" + Paths.Game + "\"", logPath);
            }, delegate(Process p, Exception error)
            {
                if (error != null)
                {
                    Log("Start failed: " + error.Message);
                    return;
                }
                gameProcess = p;
                focusPending = true;
                Log("Game started. Runtime PID " + p.Id + ". CPU affinity is managed by Windows.");
                Log("Runtime output: " + runtimeLogPath);
                Log("The game window will be brought to the front as soon as it appears (or use Focar jogo).");
            });
        }

        private bool IsGameRunning()
        {
            try { return gameProcess != null && !gameProcess.HasExited; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool IsPrecompileRunning()
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName("xcl"))
                {
                    try
                    {
                        using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                            "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + process.Id))
                        {
                            using (ManagementObjectCollection results = searcher.Get())
                            {
                                foreach (ManagementObject item in results)
                                {
                                    object value = item["CommandLine"];
                                    string commandLine = value == null ? "" : value.ToString();
                                    item.Dispose();
                                    if (commandLine.Contains("--precompile")) return true;
                                }
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return false;
        }

        private Validation Scan()
        {
            Validation v = new Validation();
            if (!Directory.Exists(Paths.Game)) throw new DirectoryNotFoundException(Paths.Game);
            string[] files = Directory.GetFiles(Paths.Game, "*", SearchOption.TopDirectoryOnly);
            v.FileCount = files.Length;

            FileInfo exeInfo = new FileInfo(Paths.Exe);
            v.ExeBytes = exeInfo.Length;
            v.ExeHash = Sha256(Paths.Exe);

            FileInfo rpfInfo = new FileInfo(Paths.Shaders);
            v.RpfBytes = rpfInfo.Length;
            v.RpfHash = Sha256(Paths.Shaders);
            v.RpfMagic = ReadFour(Paths.Shaders);

            foreach (string file in files)
            {
                try
                {
                    using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        stream.Close();
                    }
                }
                catch (IOException)
                {
                    v.Locked.Add(Path.GetFileName(file));
                }
                catch (UnauthorizedAccessException)
                {
                    v.Locked.Add(Path.GetFileName(file));
                }
            }

            v.Splash = FindSplash();
            CountCache(v);
            LoadManifest(v);
            return v;
        }

        private void CountCache(Validation v)
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xcl", "dxil");
            if (!Directory.Exists(root)) return;
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                v.CacheFiles++;
                v.CacheBytes += new FileInfo(file).Length;
            }
        }

        private void LoadManifest(Validation v)
        {
            if (!File.Exists(Paths.Manifest)) return;
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(Paths.Manifest))
            {
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
            }
            string hash, bytes, summary, mode;
            v.ManifestOk =
                values.TryGetValue("shaders.sha256", out hash) &&
                string.Equals(hash, v.RpfHash, StringComparison.OrdinalIgnoreCase) &&
                values.TryGetValue("shaders.bytes", out bytes) &&
                bytes == v.RpfBytes.ToString() &&
                values.TryGetValue("precompile", out mode) &&
                string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase) &&
                values.TryGetValue("summary", out summary) &&
                summary.Contains("errors 0");
            if (values.TryGetValue("summary", out summary)) v.Summary = summary;
        }

        private void WriteManifest(Validation v, string summary)
        {
            Directory.CreateDirectory(Paths.Bin);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("game=" + Paths.Game);
            sb.AppendLine("exe.sha256=" + v.ExeHash);
            sb.AppendLine("shaders.sha256=" + v.RpfHash);
            sb.AppendLine("shaders.bytes=" + v.RpfBytes);
            sb.AppendLine("precompile=full");
            sb.AppendLine("summary=" + summary.Replace('\r', ' ').Replace('\n', ' '));
            sb.AppendLine("utc=" + DateTime.UtcNow.ToString("o"));
            File.WriteAllText(Paths.Manifest, sb.ToString(), Encoding.ASCII);
            if (lastValidation != null)
            {
                lastValidation.ManifestOk = true;
                lastValidation.Summary = summary;
            }
        }

        private string Precompile(bool force)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(Paths.Logs, "precompile-" + stamp + ".log");
            string args = "shaders \"" + Paths.Shaders + "\" --precompile --pipelines --windows 4";
            string summary = RunCaptured(Paths.Xcl, args, logPath, true);
            if (!summary.Contains("errors 0"))
                throw new InvalidOperationException("Shader precompile reported errors: " + summary);
            return summary;
        }

        private string WarmValidate()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(Paths.Logs, "shader-check-" + stamp + ".log");
            string args = "shaders \"" + Paths.Shaders + "\" --precompile --pipelines --windows 4 --limit 5";
            string summary = RunCaptured(Paths.Xcl, args, logPath);
            if (!summary.Contains("errors 0"))
                throw new InvalidOperationException("Shader check reported errors: " + summary);
            return summary;
        }

        private void BeginPrecompileStatus(bool launchedHere)
        {
            precompileActive = true;
            precompileLaunchedHere = launchedHere;
            precompileShadersDone = 0;
            precompileShadersTotal = 0;
            precompileElapsedSeconds = 0;
            precompilePipelines = 0;
            progress.Style = ProgressBarStyle.Continuous;
            progress.Minimum = 0;
            progress.Maximum = 100;
            progress.Value = 0;
            status.BackColor = Color.FromArgb(39, 49, 74);
            status.ForeColor = Color.FromArgb(161, 203, 255);
            status.Text = "ATUALIZANDO SHADERS  |  preparando cache DXIL e PSOs de driver" + Environment.NewLine +
                          "O inicio do jogo fica bloqueado ate a pre-compilacao completa terminar.";
            hint.Text = "A traducao mostra percentual real. A etapa de PSO mostra quantos pipelines o driver ja preparou.";
        }

        private void MonitorExternalPrecompile()
        {
            if (!precompileActive || precompileLaunchedHere) return;
            string process = FindPrecompileProcess();
            if (string.IsNullOrEmpty(process))
            {
                precompileActive = false;
                precompileLaunchedHere = false;
                Log("A pre-compilacao externa terminou. Validando o manifest...");
                RefreshStatus(false);
                return;
            }

            string text = ReadLatestPrecompileTail();
            if (!string.IsNullOrEmpty(text))
            {
                MatchCollection shaderMatches = Regex.Matches(text, @"\b\d+\s*/\s*\d+\s*\(\d+\s*s\)");
                if (shaderMatches.Count > 0) ReportPrecompileProgress(shaderMatches[shaderMatches.Count - 1].Value);
                MatchCollection pipelineMatches = Regex.Matches(text, @"pipelines:\s*\d+\s+made", RegexOptions.IgnoreCase);
                if (pipelineMatches.Count > 0) ReportPrecompileProgress(pipelineMatches[pipelineMatches.Count - 1].Value);
            }
            else
            {
                status.Text = "PRE-COMPILACAO EM ANDAMENTO  |  " + process + Environment.NewLine +
                              "O processo foi iniciado fora deste launcher e nao publicou um log de progresso ainda.";
            }
        }

        private static string FindPrecompileProcess()
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName("xcl"))
                {
                    try
                    {
                        using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                            "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + process.Id))
                        using (ManagementObjectCollection results = searcher.Get())
                        {
                            foreach (ManagementObject item in results)
                            {
                                object value = item["CommandLine"];
                                string commandLine = value == null ? "" : value.ToString();
                                item.Dispose();
                                if (commandLine.Contains("--precompile"))
                                {
                                    string age = "";
                                    try { age = " | decorrido " + (DateTime.Now - process.StartTime).ToString(@"hh\:mm\:ss"); }
                                    catch (Exception) { }
                                    return "PID " + process.Id + age;
                                }
                            }
                        }
                    }
                    catch (Exception) { }
                    finally { try { process.Dispose(); } catch (Exception) { } }
                }
            }
            catch (Exception) { }
            return "";
        }

        private static string ReadLatestPrecompileTail()
        {
            try
            {
                if (!Directory.Exists(Paths.Logs)) return "";
                FileInfo[] files = new DirectoryInfo(Paths.Logs).GetFiles("precompile-*.log");
                if (files.Length == 0) return "";
                Array.Sort(files, delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                using (FileStream stream = new FileStream(files[0].FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
                }
            }
            catch (Exception) { return ""; }
        }

        private void ReportPrecompileProgress(string line)
        {
            Match progressMatch = Regex.Match(line, @"\b(\d+)\s*/\s*(\d+)\s*\((\d+)\s*s\)");
            Match pipelineMatch = Regex.Match(line, @"pipelines:\s*(\d+)\s+made", RegexOptions.IgnoreCase);
            if (!progressMatch.Success && !pipelineMatch.Success) return;

            int done = progressMatch.Success ? int.Parse(progressMatch.Groups[1].Value) : -1;
            int total = progressMatch.Success ? int.Parse(progressMatch.Groups[2].Value) : -1;
            int seconds = progressMatch.Success ? int.Parse(progressMatch.Groups[3].Value) : -1;
            int pipelines = pipelineMatch.Success ? int.Parse(pipelineMatch.Groups[1].Value) : -1;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!precompileActive) return;
                    if (done >= 0) precompileShadersDone = done;
                    if (total > 0) precompileShadersTotal = total;
                    if (seconds >= 0) precompileElapsedSeconds = seconds;
                    if (pipelines >= 0) precompilePipelines = pipelines;

                    int percent = precompileShadersTotal == 0 ? 0 :
                        Math.Min(100, (int)Math.Round(100.0 * precompileShadersDone / precompileShadersTotal));
                    progress.Value = percent;
                    string eta = "";
                    if (precompileShadersDone > 0 && precompileElapsedSeconds > 0 && precompileShadersDone < precompileShadersTotal)
                    {
                        double remaining = precompileElapsedSeconds * (precompileShadersTotal - precompileShadersDone) / precompileShadersDone;
                        eta = " | estimativa " + TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm\:ss");
                    }
                    string stage = precompilePipelines == 0
                        ? "traduzindo e compilando DXIL"
                        : "gerando cache de PSO do driver: " + precompilePipelines + " concluidos";
                    status.Text = "ATUALIZANDO SHADERS  |  " + percent + "% (" + precompileShadersDone + "/" + precompileShadersTotal + ")" + eta + Environment.NewLine +
                                  "Etapa atual: " + stage + ". O jogo iniciara somente apos a conclusao.";
                }));
            }
            catch (ObjectDisposedException) { }
        }

        private string RunCaptured(string exe, string args, string logPath, bool showPrecompileProgress = false)
        {
            if (!File.Exists(exe)) throw new FileNotFoundException("Tool not found", exe);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = Paths.Repo;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            ApplyEnv(psi);

            // The driver PSO stage of --precompile exhausted memory at two
            // concurrent NVIDIA compiles (~22,600/26,503 in the last two runs).
            // Keep one there; the game itself still uses the profile's two.
            if (args.IndexOf("--precompile", StringComparison.OrdinalIgnoreCase) >= 0)
                psi.EnvironmentVariables["XCL_PIPELINE_THREADS"] = "1";

            Process process = new Process();
            process.StartInfo = psi;
            object gate = new object();
            StringBuilder all = new StringBuilder();
            StreamWriter writer = new StreamWriter(logPath, false);

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                lock (gate)
                {
                    all.AppendLine(e.Data);
                    writer.WriteLine(e.Data);
                    writer.Flush();
                }
                if (showPrecompileProgress) ReportPrecompileProgress(e.Data);
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                lock (gate)
                {
                    all.AppendLine(e.Data);
                    writer.WriteLine(e.Data);
                    writer.Flush();
                }
                if (showPrecompileProgress) ReportPrecompileProgress(e.Data);
            };

            if (!process.Start()) throw new InvalidOperationException("Failed to start " + exe);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            process.WaitForExit(1000);
            lock (gate) writer.Close();

            string text = all.ToString();
            Regex summary = new Regex(@"compiled\s+\d+,\s+already cached\s+\d+,\s+errors\s+\d+[^\r\n]*");
            Match match = summary.Match(text);
            if (!match.Success) throw new InvalidOperationException("No precompile summary in " + logPath);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("xcl shaders exited " + process.ExitCode + ". See " + logPath);
            return match.Value;
        }

        private Process StartRuntime(string exe, string args, string logPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = Paths.Repo;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            ApplyEnv(psi);

            Process process = new Process();
            process.StartInfo = psi;
            object gate = new object();
            StreamWriter writer = new StreamWriter(logPath, false);

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                lock (gate) { writer.WriteLine(e.Data); writer.Flush(); }
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                lock (gate) { writer.WriteLine(e.Data); writer.Flush(); }
            };

            if (!process.Start()) throw new InvalidOperationException("Failed to start " + exe);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Thread monitor = new Thread(() =>
            {
                process.WaitForExit();
                process.WaitForExit(1000);
                int code = process.ExitCode;
                lock (gate)
                {
                    writer.WriteLine("EXIT=" + code);
                    writer.Flush();
                    writer.Close();
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        gameProcess = null;
                        Log("Game exited with code " + code + ".");
                        RefreshStatus(false);
                    }));
                }
                catch (ObjectDisposedException) { }
            });
            monitor.IsBackground = true;
            monitor.Start();
            return process;
        }

        private void ApplyEnv(ProcessStartInfo psi)
        {
            StringDictionary e = psi.EnvironmentVariables;
            Set(e, "XCL_ADAPTER", "nvidia");
            Set(e, "XCL_RAYTRACING", "off");
            Set(e, "XCL_DATA_ROOT", Paths.XclData);
            if (Directory.Exists(Paths.DxcDir)) Set(e, "XCL_DXC_DIR", Paths.DxcDir); else e.Remove("XCL_DXC_DIR");
            Set(e, "XCL_HUGE_PSO_CONCURRENCY", "1");
            Set(e, "XCL_DXC_THREADS", "6");
            Set(e, "XCL_PIPELINE_THREADS", "2");
            Set(e, "XCL_DSTORAGE_PARALLEL_DECOMPRESS", "1");
            Set(e, "XCL_DSTORAGE_MEMORY_BUDGET_MIB", "32");
            Set(e, "XCL_DSTORAGE_WORKERS", "2");

            Set(e, "VICTORY_ADAPTER", "nvidia");
            Set(e, "VICTORY_CACHE_DIR", Paths.VictoryCache);
            Set(e, "VICTORY_DATA_DIR", Paths.VictoryData);
            Set(e, "VICTORY_DISPLAY_MODE", "1280x720");
            Set(e, "VICTORY_PIPELINE_WAIT_MS", "24");
            Set(e, "VICTORY_D3D12_BACKGROUND", "0");
            Set(e, "VICTORY_SHADER_OPT", "1");
            Set(e, "VICTORY_INPLACE", "0");
            Set(e, "VICTORY_BVH_BUILDER", "0");
            Set(e, "VICTORY_CARD_BUFFERS", "1");
            Set(e, "VICTORY_WIDE_BACK", "0");
            Set(e, "VICTORY_LATE_WAIT", "0");
            Set(e, "VICTORY_CARD_BUFFERS_MIN_MB", "1");
            Set(e, "VICTORY_CARD_BUFFERS_MB", "256");
            Set(e, "VICTORY_TEXTURE_BUDGET_MB", "3072");
            Set(e, "VICTORY_RELAY", "2");
            Set(e, "VICTORY_HOST_WAVE", "32");
            Set(e, "VICTORY_HOST_MAX_WAVE", "32");

            e.Remove("XCL_PSO_TRACE");
            if (dred) Set(e, "XCL_DRED", "1"); else e.Remove("XCL_DRED");
            e.Remove("XCL_FRAME_DUMP");
            e.Remove("VICTORY_GPU_TIMING");
            e.Remove("VICTORY_D3D12_DRED");
        }

        private static void Set(StringDictionary e, string name, string value)
        {
            e[name] = value;
        }

        private void LoadSplash(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    using (MemoryStream stream = new MemoryStream(bytes))
                    using (Image image = Image.FromStream(stream))
                    {
                        splash.Image = new Bitmap(image);
                    }
                    return;
                }
                if (File.Exists(Paths.BrandImage))
                {
                    byte[] bytes = File.ReadAllBytes(Paths.BrandImage);
                    using (MemoryStream stream = new MemoryStream(bytes))
                    using (Image image = Image.FromStream(stream))
                    {
                        splash.Image = new Bitmap(image);
                    }
                    return;
                }
            }
            catch (Exception) { }
            splash.Image = Placeholder();
        }

        private static string FindSplash()
        {
            if (string.IsNullOrEmpty(Paths.Game) || !Directory.Exists(Paths.Game)) return "";
            string[] names = new string[]
            {
                "xbs_splash.png", "xbs_logo_480.png", "xbs_logo_store.png", "xbs_logo_150.png",
                "splash.png", "logo.png", "logo.jpg", "logo.jpeg", "cover.png", "cover.jpg"
            };
            foreach (string name in names)
            {
                string path = Path.Combine(Paths.Game, name);
                if (File.Exists(path)) return path;
            }
            // Game packages vary.  Search only presentation folders and prefer files whose
            // basename says logo/splash/cover; this avoids scanning large archive directories.
            string[] roots = new string[] { "assets", "art", "content", "media", "images", "resources", "ui" };
            foreach (string root in roots)
            {
                string directory = Path.Combine(Paths.Game, root);
                if (!Directory.Exists(directory)) continue;
                try
                {
                    foreach (string path in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(path).ToLowerInvariant();
                        string file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                        if ((ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp") &&
                            (file.Contains("logo") || file.Contains("splash") || file.Contains("cover"))) return path;
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return "";
        }

        private static Bitmap Placeholder()
        {
            Bitmap bitmap = new Bitmap(1024, 300);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                using (LinearGradientBrush brush = new LinearGradientBrush(rect, Color.FromArgb(16, 20, 32), Color.FromArgb(48, 18, 32), LinearGradientMode.Horizontal))
                    g.FillRectangle(brush, rect);
                using (Font font = new Font("Segoe UI", 30F, FontStyle.Bold))
                {
                    g.DrawString("SCARLET VANGUARD", font, Brushes.White, 48, 110);
                }
                using (Font small = new Font("Segoe UI", 13F))
                {
                    g.DrawString("Experimental Xbox Series compatibility runtime", small, Brushes.LightGray, 50, 165);
                }
            }
            return bitmap;
        }

        private static string ReadFour(string path)
        {
            byte[] bytes = new byte[4];
            using (FileStream stream = File.OpenRead(path))
            {
                int read = stream.Read(bytes, 0, 4);
                if (read < 4) return "";
            }
            return Encoding.ASCII.GetString(bytes);
        }

        private static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToUpperInvariant();
            }
        }

        private static string Mb(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0");
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm form = new MainForm();
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, "--start", StringComparison.OrdinalIgnoreCase))
                    form.QueueAutoStart();
                else if (string.Equals(arg, "--dred", StringComparison.OrdinalIgnoreCase))
                    form.EnableDred();
            }
            Application.Run(form);
        }
    }
}
