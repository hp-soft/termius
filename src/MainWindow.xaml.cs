using Microsoft.Web.WebView2.Core;
using SshManager.Pty;
using SshManager.Storage;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;

namespace SshManager;

// Class Log
internal static class Log
{
    private static readonly object _lock = new object();
    public static string FileLocation { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshManager.log");

    public static void Debug(string message)
    {
        try
        {
#if DEBUG
			Write($"[DEBUG] {message}");
			Console.WriteLine($"[DEBUG] {message}");
#endif
        }
        catch { }
    }
    public static void Write(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(FileLocation, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Falha no log não deve quebrar a aplicação — opcional: tratar/expôr erro
        }
    }
}

public partial class MainWindow : Window
{
    private readonly object _sftpUploadLock = new();
    private readonly Dictionary<string, System.Threading.CancellationTokenSource> _sftpUploadCts = new();

    private readonly Dictionary<string, ConPty> _sessions = new();
    private readonly ConnectionStore _store = new();
    private readonly FolderStore _folders = new();
    private readonly SnippetStore _snippets = new();
    private readonly PrefsStore _prefs = new();

    private sealed class SessionLog
    {
        public required FileStream Stream;
        public required string Path;
        public required Decoder Decoder;
        public required System.Text.StringBuilder Buffer;
        public int Cursor;
        public int ConsecutiveBlankLines;
    }

    private sealed class ClipboardCapture
    {
        public required Decoder Decoder;
        public required System.Text.StringBuilder FullText;
        public required System.Text.StringBuilder LineBuffer;
        public int Cursor;
        public int ConsecutiveBlankLines;
    }

    private readonly Dictionary<string, SessionLog> _logs = new();
    private readonly object _logLock = new();

    private readonly Dictionary<string, ClipboardCapture> _clipCaptures = new();
    private readonly object _clipLock = new();


    // Regex to strip common ANSI CSI sequences and control chars (preserve CR/LF and keep BS '\b' for processing)
    private static readonly Regex _ansiRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    // keep backspace (\x08) so we can process it; remove other C0 except CR(0x0D) and LF(0x0A)
    private static readonly Regex _ctrlRegex = new(@"[\x00-\x07\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        _store.Load();
        _folders.Load();
        _snippets.Load();
        _prefs.Load();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            foreach (var s in _sessions.Values) s.Dispose();
            lock (_logLock)
            {
                foreach (var l in _logs.Values) { try { l.Stream.Flush(); l.Stream.Dispose(); } catch { } }
                _logs.Clear();
            }
        };
        // Corrige o maximizar de janela borderless (WindowStyle=None): sem isto
        // o conteudo extrapola a tela e o rodape (nova conexao/status) some
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        };
        // A margem que expoe as bordas para resize so faz sentido no estado Normal
        // maximizado ela viraria uma borda vazia em volta do conteudo
        StateChanged += (_, _) => RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(6);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            const int MONITOR_DEFAULTTONEAREST = 0x2;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    // Tudo em pixels FISICOS do monitor (com PerMonitorV2 no manifest
                    // eses valores batem com o espaço de coordenadas da Janela
                    var work = mi.rcWork;       // Area Util (exclui barra de tarefas)
                    var area = mi.rcMonitor;    // Monitor Inteiro
                    mmi.ptMaxPosition.X = work.Left - area.Left;
                    mmi.ptMaxPosition.Y = work.Top - area.Top;
                    mmi.ptMaxSize.X = work.Right - work.Left;
                    mmi.ptMaxSize.Y = work.Bottom - work.Top;
                    mmi.ptMaxTrackSize.X = work.Right - work.Left;
                    mmi.ptMaxTrackSize.Y = work.Bottom - work.Top;
                    mmi.ptMinTrackSize.X = 640;
                    mmi.ptMinTrackSize.Y = 420;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        //var userData = Path.Combine(Path.GetTempPath(), "SshManager.WebView2");
        //var env = await CoreWebView2Environment.CreateAsync(null, userData);
        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"[error] Failed to initialize WebView2: {ex.Message}");
            MessageBox.Show(
                "Failed to Start WebView2:\r\n\r\n" + ex.Message +
                "\r\n\r\nCheck WebView2 Runtime installed",
                "RTermius",
                MessageBoxButton.OK,
                MessageBoxImage.Error
                );
            Close();
        }
        ;
    }
    private async Task InitializeWebViewAsync()
    {
        var baseUserData = Path.Combine(Path.GetTempPath(), "SshManager.WebView2");
        CoreWebView2Environment env;

        try
        {
            env = await CoreWebView2Environment.CreateAsync(null, baseUserData);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (COMException)
        {
            var fallback = Path.Combine(baseUserData, "instances", Environment.ProcessId.ToString());
            env = await CoreWebView2Environment.CreateAsync(null, fallback);
            await Web.EnsureCoreWebView2Async(env);
        }

        Web.CoreWebView2.WebMessageReceived += OnWebMessage;
#if DEBUG
		// Abrir DevTools automaticamente para depuração (remover após diagnosticar)
		try { Web.CoreWebView2.OpenDevToolsWindow(); } catch { }


		Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
#endif
        // Concede automaticamente o acesso ao clipboard para a origem local
        // (necessario para o paste com botao direito via navigator.clipboard)
        Web.CoreWebView2.PermissionRequested += (_, args) =>
        {
            if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                args.State = CoreWebView2PermissionState.Allow;
        };

        var webDir = Path.Combine(AppContext.BaseDirectory, "web");
        Web.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", webDir, CoreWebView2HostResourceAccessKind.Allow);

        Web.CoreWebView2.Navigate("https://app.local/index.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";

            Log.Debug($"[ws->host] type={type} id={id} json={root}");

            switch (type)
            {
                case "start": StartSession(id, root); break;
                case "startSsh": StartSsh(id, root); break;
                case "openSftp": OpenSftp(root); break;
                case "sftpListLocal": HandleSftpListLocal(root); break;
                case "sftpListRemote": HandleSftpListRemote(root); break;
                case "sftpUpload": HandleSftpUpload(root); break;
                case "sftpDownload": HandleSftpDownload(root); break;
                case "sftpMkdir": HandleSftpMkdir(root); break;
                case "sftpDelete": HandleSftpDelete(root); break;
                case "sftpChmod": HandleSftpChmod(root); break;
                case "sftpRenameRemote": HandleSftpRenameRemote(root); break;
                case "sftpZipRemote": HandleSftpZipRemote(root); break;
                case "localDelete": HandleLocalDelete(root); break;
                case "localRename": HandleLocalRename(root); break;
                case "localMkdir": HandleLocalMkdir(root); break;
                case "localZip": HandleLocalZip(root); break;
                case "sftpCancelUpload":
                    {
                        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
                        lock (_sftpUploadLock)
                        {
                            if (_sftpUploadCts.ContainsKey(tabId))
                            {
                                try { _sftpUploadCts[tabId].Cancel(); } catch { }
                                _sftpUploadCts.Remove(tabId);
                            }
                        }
                        break;
                    }
                case "input":
                    {
                        var data = root.GetProperty("data").GetString() ?? "";
                        Log.Debug($"[ws->host] input id={id} len={data.Length}");
                        if (_sessions.TryGetValue(id, out var p)) p.Write(data);
                        break;
                    }
                case "resize":
                    {
                        if (_sessions.TryGetValue(id, out var r))
                            r.Resize((short)root.GetProperty("cols").GetInt32(), (short)root.GetProperty("rows").GetInt32());
                        break;
                    }
                case "close":
                    if (_sessions.Remove(id, out var c)) c.Dispose();
                    CloseLog(id);
                    StopClipCapture(id, copyToClipboard: false);
                    break;

                case "startLog":
                    var label = root.TryGetProperty("label", out var lEl) ? lEl.GetString() : null;
                    var dir = root.TryGetProperty("dir", out var dEl) ? dEl.GetString() : null;
                    StartLog(id, label, dir);
                    break;

                case "stopLog":
                    CloseLog(id);
                    PostToWeb(new { type = "logStatus", id, active = false, path = (string?)null });
                    break;

                case "startClipCapture":
                    StartClipCapture(id);
                    break;

                case "stopClipCapture":
                    StopClipCapture(id, copyToClipboard: true);
                    break;

                case "pickLogDir":
                    PickLogDir();
                    break;

                case "openLogDir":
                    OpenLogDir();
                    break;

                case "loadConns":
                    SendConns();
                    break;

                case "saveConn":
                    SaveConn(root);
                    break;

                case "deleteConn":
                    _store.Remove(root.GetProperty("connId").GetString() ?? "");
                    SendConns();
                    break;

                case "pickKey":
                    PickKey();
                    break;

                case "loadFolders":
                    SendFolders();
                    break;

                case "addFolder":
                    _folders.Add(root.GetProperty("path").GetString() ?? "");
                    SendFolders();
                    break;

                case "removeFolder":
                    _folders.Remove(root.GetProperty("path").GetString() ?? "");
                    SendFolders();
                    break;

                case "loadSnippets":
                    SendSnippets();
                    break;

                case "saveSnippet":
                    SaveSnippet(root);
                    break;

                case "deleteSnippet":
                    _snippets.Remove(root.GetProperty("snippetId").GetString() ?? "");
                    SendSnippets();
                    break;

                case "loadPrefs":
                    SendPrefs();
                    break;

                case "requestPaste":
                    {
                        string txt = "";
                        try
                        {
                            Dispatcher.Invoke(() => { if (Clipboard.ContainsText()) txt = Clipboard.GetText(); });
                        }
                        catch { }
                        Log.Debug($"[requestPaste] id={id} len={(txt ?? "").Length}");

                        // If the session is active on the host, write directly to the pty to avoid extra roundtrips.
                        if (_sessions.TryGetValue(id, out var pty))
                        {
                            try
                            {
                                pty.Write(txt ?? "");
                                Log.Debug($"[requestPaste] wrote {(txt ?? "").Length} bytes directly to session {id}");
                            }
                            catch (Exception ex)
                            {
                                Log.Debug($"[requestPaste] failed to write to session {id}: {ex.Message}");
                                // fallback to sending to web UI so it can forward
                                PostToWeb(new { type = "paste", id = id, data = txt });
                            }
                        }
                        else
                        {
                            // session not present on host side, forward to UI which may handle routing
                            PostToWeb(new { type = "paste", id = id, data = txt });
                        }
                        break;
                    }

                case "savePrefs":
                    SavePrefs(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[error]" + ex.Message);
            PostToWeb(new { type = "error", message = ex.Message });
        }
    }

    private void StartSession(string id, JsonElement root)
    {
        var bash = GitBash.Find();
        if (bash is null)
        {
            Log.Write("Git Bash not found. Install Git for Windows or adjust GitBash.Find().");
            PostToWeb(new
            {
                type = "data",
                id,
                data =
                $"\r\nGit Bash (bash.exe) not found\r\n" +
                "Install Git for Windows or adjust GitBash.Find().\r\n"
            });
            return;
        }

        var (cols, rows) = ReadSize(root);
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.CurrentDirectory;
        Log.Debug($"\"{bash}\" --login -i ->{home} {cols}x{rows}");
        Spawn(id, $"\"{bash}\" --login -i", home, cols, rows);
    }
    private void StartSsh(string id, JsonElement root)
    {
        var conn = _store.Get(root.GetProperty("connId").GetString() ?? "");
        if (conn is null) { PostToWeb(new { type = "error", message = "Connection not found." }); return; }

        var ssh = GitBash.FindSsh();
        if (ssh is null)
        {
            Log.Write("ssh not found");
            PostToWeb(new
            {
                type = "data",
                id,
                data =
                $"ssh (ssh.exe) not found\r\n" +
                "Install Git for Windows (OpenSSH) or OpenSSH for Windows or adjust GitBash.FindSsh().\r\n"
            });
            return;
        }

        // Monta a linha de comando do ssh. A senha (se houver) nao e injetada
        // o proprio ssh vai pedir no terminal
        var args = $"-p {conn.Port} -o StrictHostKeyChecking=accept-new";
        if (conn.AuthMethod == "key" && !string.IsNullOrWhiteSpace(conn.KeyPath))
            args += $" -i \"{conn.KeyPath}\"";
        args += $" {conn.User}@{conn.Host}";

        var (cols, rows) = ReadSize(root);
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.CurrentDirectory;
        Log.Debug($"\"{ssh}\" {args} -> {home} {cols}x{rows}");
        Spawn(id, $"\"{ssh}\" {args}", home, cols, rows);
    }

    private void OpenSftp(JsonElement root)
    {
        // Expect: { tabId, connId }
        try
        {
            var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
            var connId = root.TryGetProperty("connId", out var c) ? c.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(connId))
            {
                PostToWeb(new { type = "openSftpCreated", tabId, ok = false, message = "No connection specified" });
                return;
            }
            var conn = _store.Get(connId);
            if (conn == null)
            {
                PostToWeb(new { type = "openSftpCreated", tabId, ok = false, message = "Connection not found" });
                return;
            }
            // Determine remote home directory and reply with connection info
            string remoteHome = "/";
            try
            {
                using var tmp = new SftpService(conn.Host, conn.Port, conn.User, conn.AuthMethod == "password" ? conn.Password : null, conn.AuthMethod == "key" ? conn.KeyPath : null);
                remoteHome = tmp.GetWorkingDirectory() ?? "/";
            }
            catch (Exception ex)
            {
                Log.Write($"[sftp] failed to detect remote home: {ex.Message}");
            }
            PostToWeb(new
            {
                type = "openSftpCreated",
                tabId,
                ok = true,
                conn = new { conn.Id, conn.Name, conn.Host, conn.Port, conn.User, conn.AuthMethod },
                remoteHome
            });
        }
        catch (Exception ex)
        {
            Log.Write($"[error] openSftp failed: {ex.Message}");
            PostToWeb(new { type = "openSftpCreated", tabId = "", ok = false, message = ex.Message });
        }
    }

    // SFTP operations requested from the web UI
    private void HandleSftpListLocal(JsonElement root)
    {
        try
        {
            var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
            var path = root.TryGetProperty("path", out var p) ? p.GetString() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var list = new List<object>();
            try
            {
                foreach (var fi in new DirectoryInfo(path).GetFileSystemInfos())
                {
                    var isDir = (fi.Attributes & FileAttributes.Directory) != 0;
                    long sz = (!isDir && fi is FileInfo f) ? f.Length : 0;
                    list.Add(new { name = fi.Name, fullName = fi.FullName, isDirectory = isDir, size = sz, lastWriteTime = fi.LastWriteTime });
                }
            }
            catch (Exception ex) { Log.Write($"[sftp] local list failed: {ex.Message}"); }
            PostToWeb(new { type = "sftpLocalListResult", tabId, path, items = list });
        }
        catch (Exception ex)
        {
            Log.Write($"[error] HandleSftpListLocal: {ex.Message}");
        }
    }

    private void HandleSftpListRemote(JsonElement root)
    {
        try
        {
            var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
            var connId = root.TryGetProperty("connId", out var c) ? c.GetString() ?? "" : "";
            var path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
            var conn = _store.Get(connId);
            if (conn == null) { PostToWeb(new { type = "sftpRemoteListResult", tabId, ok = false, message = "Connection not found" }); return; }
            try
            {
                using var sftp = new SftpService(conn.Host, conn.Port, conn.User, conn.AuthMethod == "password" ? conn.Password : null, conn.AuthMethod == "key" ? conn.KeyPath : null);
                var items = new List<object>();
                foreach (var it in sftp.ListDirectory(path))
                {
                    items.Add(new
                    {
                        name = it.Name,
                        fullName = it.FullName,
                        isDirectory = it.IsDirectory,
                        size = it.Size,
                        lastWriteTime = it.LastWriteTime,
                        permissions = it.Permissions,
                        owner = it.Owner,
                        group = it.Group
                    });
                }
                PostToWeb(new { type = "sftpRemoteListResult", tabId, connId, ok = true, path, items });
            }
            catch (Exception ex) { Log.Write($"[sftp] remote list failed: {ex.Message}"); PostToWeb(new { type = "sftpRemoteListResult", tabId, ok = false, message = ex.Message }); }
        }
        catch (Exception ex)
        {
            Log.Write($"[error] HandleSftpListRemote: {ex.Message}");
        }
    }

    private void HandleSftpUpload(JsonElement root)
    {
        try
        {
            var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
            var connId = root.TryGetProperty("connId", out var c) ? c.GetString() ?? "" : "";
            // localPath may be a string or array of strings
            List<string> localPaths = new();
            if (root.TryGetProperty("localPath", out var lp))
            {
                if (lp.ValueKind == JsonValueKind.String)
                {
                    localPaths.Add(lp.GetString() ?? "");
                }
                else if (lp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in lp.EnumerateArray()) if (el.ValueKind == JsonValueKind.String) localPaths.Add(el.GetString() ?? "");
                }
            }
            var remoteDir = root.TryGetProperty("remoteDir", out var rd) ? rd.GetString() ?? "/" : "/";

            var conn = _store.Get(connId);
            if (conn == null) { PostToWeb(new { type = "sftpUploadResult", tabId, ok = false, message = "Connection not found" }); return; }

            // Start async upload queue for this tabId with cancellation support
            var localList = localPaths.ToArray();
            var cts = new System.Threading.CancellationTokenSource();
            lock (_sftpUploadLock)
            {
                if (_sftpUploadCts.ContainsKey(tabId))
                {
                    try { _sftpUploadCts[tabId].Cancel(); } catch { }
                    _sftpUploadCts[tabId] = cts;
                }
                else
                {
                    _sftpUploadCts[tabId] = cts;
                }
            }

            PostToWeb(new { type = "sftpUploadStarted", tabId, files = localList, remoteDir });

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                var token = cts.Token;
                try
                {
                    foreach (var lpPath in localList)
                    {
                        if (token.IsCancellationRequested)
                        {
                            PostToWeb(new { type = "sftpUploadCanceled", tabId });
                            break;
                        }
                        try
                        {
                            if (Directory.Exists(lpPath)) { throw new InvalidOperationException($"Cannot upload a directory: {lpPath}"); }
                            if (!File.Exists(lpPath)) { throw new FileNotFoundException($"Local file not found: {lpPath}", lpPath); }

                            var remotePathFull = remoteDir.EndsWith("/") ? remoteDir + Path.GetFileName(lpPath) : remoteDir + "/" + Path.GetFileName(lpPath);

                            long total = new FileInfo(lpPath).Length;
                            PostToWeb(new { type = "sftpUploadProgress", tabId, file = lpPath, uploaded = 0L, total = total });
                            using (var client = new SftpService(conn.Host, conn.Port, conn.User, conn.AuthMethod == "password" ? conn.Password : null,
                                conn.AuthMethod == "key" ? conn.KeyPath : null))
                            {
                                client.UploadFile(lpPath, remoteDir);
                            }
                            PostToWeb(new { type = "sftpUploadFinished", tabId, file = lpPath, remotePath = remotePathFull, ok = true });
                            PostToWeb(new { type = "sftpUploadProgress", tabId, connId, path = remoteDir });
                        }
                        catch (Exception ex)
                        {
                            Log.Write($"[sftp] upload failed: {ex.Message}");
                            PostToWeb(new { type = "sftpUploadFinished", tabId, file = lpPath, ok = false, message = ex.Message });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"[sftp] upload queue failed: {ex.Message}");
                    PostToWeb(new { type = "sftpUploadFailed", tabId, message = ex.Message });
                }
                finally
                {
                    lock (_sftpUploadLock) { if (_sftpUploadCts.ContainsKey(tabId)) _sftpUploadCts.Remove(tabId); }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Write($"[error] HandleSftpUpload: {ex.Message}");
        }
    }

    private void HandleSftpDownload(JsonElement root)
    {
        try
        {
            var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
            var connId = root.TryGetProperty("connId", out var c) ? c.GetString() ?? "" : "";
            var remotePath = root.TryGetProperty("remotePath", out var rp) ? rp.GetString() ?? "" : "";
            var localDir = root.TryGetProperty("localDir", out var ld) ? ld.GetString() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // If no localDir specified, prefer Downloads folder
            if (string.IsNullOrWhiteSpace(localDir))
            {
                localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
            }
            var conn = _store.Get(connId);
            if (conn == null) { PostToWeb(new { type = "sftpDownloadResult", tabId, ok = false, message = "Connection not found" }); return; }
            try
            {
                var target = System.IO.Path.Combine(localDir, System.IO.Path.GetFileName(remotePath));
                using var sftp = new SftpService(conn.Host, conn.Port, conn.User, conn.AuthMethod == "password" ? conn.Password : null, conn.AuthMethod == "key" ? conn.KeyPath : null);
                sftp.DownloadFile(remotePath, target);
                PostToWeb(new { type = "sftpDownloadResult", tabId, ok = true, remotePath, localPath = target });
                PostToWeb(new { type = "sftpRefreshLocal", tabId, path = localDir });
            }
            catch (Exception ex) { Log.Write($"[sftp] download failed: {ex.Message}"); PostToWeb(new { type = "sftpDownloadResult", tabId, ok = false, message = ex.Message }); }
        }
        catch (Exception ex)
        {
            Log.Write($"[error] HandleSftpDownload: {ex.Message}");
        }
    }

    private SftpService NewSftp(Storage.Connection conn)
        => new SftpService(conn.Host, conn.Port, conn.User, conn.AuthMethod == "password" ? conn.Password : null, conn.AuthMethod == "key" ? conn.KeyPath : null);

    private void HandleSftpMkdir(JsonElement root)
    {
        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
        var connId = root.TryGetProperty("connId", out var c) ? c.GetString() ?? "" : "";
        var path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var conn = _store.Get(connId);
        if (conn == null) { PostToWeb(new { type = "sftpOpResult", op = "mkdir", tabId, ok = false, message = "Connection not found" }); return; }
        if (string.IsNullOrWhiteSpace(name)) { PostToWeb(new { type = "sftpOpResult", op = "mkdir", tabId, ok = false, message = "Directory name is required" }); return; }
        var full = (path.EndsWith("/") ? path : path + "/") + name;
        try
        {
            using var sftp = NewSftp(conn);
            sftp.CreateDirectory(full);
            PostToWeb(new { type = "sftpOpResult", op = "mkdir", tabId, ok = true });
            PostToWeb(new { type = "sftpRefreshRemote", tabId, connId, path });
        }
        catch (Exception ex)
        {
            Log.Write($"[sftp] mkdir failed: {ex.Message}");
            PostToWeb(new { type = "sftpOpResult", op = "mkdir", tabId, ok = false, message = ex.Message });
        }
    }

    private void HandleSftpDelete(JsonElement root)
    {
        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
        var connId = root.TryGetProperty("connId", out var c) ? c.GetString() ?? "" : "";
        var remotePath = root.TryGetProperty("remotePath", out var rp) ? rp.GetString() ?? "" : "";
        var parentPath = root.TryGetProperty("path", out var pp) ? pp.GetString() ?? "/" : "/";
        var isDir = root.TryGetProperty("isDirectory", out var d) && d.ValueKind == JsonValueKind.True;
        var conn = _store.Get(connId);
        if (conn == null)
        {
            PostToWeb(new { type = "sftpOpResult", op = "delete", tabId, ok = false, message = "Connection not found" });
            return;
        }
        if (string.IsNullOrWhiteSpace(remotePath)) { PostToWeb(new { type = "sftpOpResult", op = "delete", tabId, ok = false, message = "No path specified" }); return; }
        try
        {
            using var sftp = NewSftp(conn);
            if (isDir) sftp.RemoveDirectory(remotePath); else sftp.RemoveFile(remotePath);
            PostToWeb(new { type = "sftpOpResult", op = "delete", tabId, ok = true });
            PostToWeb(new { type = "sftpRefreshRemote", tabId, connId, path = parentPath });
        }
        catch (Exception ex)
        {
            Log.Write($"[sftp] delete failed: {ex.Message}");
            PostToWeb(new { type = "sftpOpResult", op = "delete", tabId, ok = false, message = ex.Message });
        }
    }

    private void HandleSftpChmod(JsonElement root)
    {
        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
        var connId = root.TryGetProperty("connId", out var c) ? c.GetString() ?? "" : "";
        var remotePath = root.TryGetProperty("remotePath", out var rp) ? rp.GetString() ?? "" : "";
        var parentPath = root.TryGetProperty("path", out var pp) ? pp.GetString() ?? "/" : "/";
        var mode = root.TryGetProperty("mode", out var md) ? md.GetString() ?? "" : "";

        var conn = _store.Get(connId);
        if (conn == null)
        {
            PostToWeb(new { type = "sftpOpResult", op = "chmod", tabId, ok = false, message = "Connection not found" });
            return;
        }
        if (string.IsNullOrWhiteSpace(remotePath)) { PostToWeb(new { type = "sftpOpResult", op = "chmod", tabId, ok = false, message = "No path specified" }); return; }
        try
        {
            using var sftp = NewSftp(conn);
            sftp.ChangePermissions(remotePath, mode);
            PostToWeb(new { type = "sftpOpResult", op = "chmod", tabId, ok = true });
            PostToWeb(new { type = "sftpRefreshRemote", tabId, connId, path = parentPath });
        }
        catch (Exception ex)
        {
            Log.Write($"[sftp] chmod failed: {ex.Message}");
            PostToWeb(new { type = "sftpOpResult", op = "chmod", tabId, ok = false, message = ex.Message });
        }
    }
    private void HandleSftpRenameRemote(JsonElement root)
    {
        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
        var connId = root.TryGetProperty("connId", out var c) ? c.GetString() ?? "" : "";
        var remotePath = root.TryGetProperty("remotePath", out var rp) ? rp.GetString() ?? "" : "";
        var parentPath = root.TryGetProperty("path", out var pp) ? pp.GetString() ?? "/" : "/";
        var newName = root.TryGetProperty("newName", out var nn) ? nn.GetString() ?? "" : "";

        var conn = _store.Get(connId);
        if (conn == null)
        {
            PostToWeb(new { type = "sftpOpResult", op = "rename", tabId, ok = false, message = "Connection not found" });
            return;
        }
        if (string.IsNullOrWhiteSpace(remotePath) || string.IsNullOrWhiteSpace(newName)) { PostToWeb(new { type = "sftpOpResult", op = "rename", tabId, ok = false, message = "Path and New name are required" }); return; }
        try
        {
            var dir = parentPath.EndsWith("/") ? parentPath : parentPath + "/";
            using var sftp = NewSftp(conn);
            sftp.RenameRemote(remotePath, dir + newName);
            PostToWeb(new { type = "sftpOpResult", op = "rename", tabId, ok = true });
            PostToWeb(new { type = "sftpRefreshRemote", tabId, connId, path = parentPath });
        }
        catch (Exception ex)
        {
            Log.Write($"[sftp] rename failed: {ex.Message}");
            PostToWeb(new { type = "sftpOpResult", op = "rename", tabId, ok = false, message = ex.Message });
        }
    }

    private void HandleSftpZipRemote(JsonElement root)
    {
        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
        var connId = root.TryGetProperty("connId", out var c) ? c.GetString() ?? "" : "";
        var remotePath = root.TryGetProperty("remotePath", out var rp) ? rp.GetString() ?? "" : "";
        var parentPath = root.TryGetProperty("path", out var pp) ? pp.GetString() ?? "/" : "/";

        var conn = _store.Get(connId);
        if (conn == null)
        {
            PostToWeb(new { type = "sftpOpResult", op = "zip", tabId, ok = false, message = "Connection not found" });
            return;
        }
        if (string.IsNullOrWhiteSpace(remotePath)) { PostToWeb(new { type = "sftpOpResult", op = "zip", tabId, ok = false, message = "No path specified" }); return; }
        try
        {
            using var sftp = NewSftp(conn);
            sftp.GzipRemote(remotePath);
            PostToWeb(new { type = "sftpOpResult", op = "zip", tabId, ok = true });
            PostToWeb(new { type = "sftpRefreshRemote", tabId, connId, path = parentPath });
        }
        catch (Exception ex)
        {
            Log.Write($"[sftp] zip failed: {ex.Message}");
            PostToWeb(new { type = "sftpOpResult", op = "zip", tabId, ok = false, message = ex.Message });
        }
    }

    private void HandleLocalDelete(JsonElement root)
    {
        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
        var fullName = root.TryGetProperty("fullName", out var rp) ? rp.GetString() ?? "" : "";
        var parentPath = root.TryGetProperty("path", out var pp) ? pp.GetString() ?? "/" : "/";
        var isDir = root.TryGetProperty("isDirectory", out var d) && d.ValueKind == JsonValueKind.True;
        try
        {
            if (string.IsNullOrWhiteSpace(fullName)) throw new ArgumentException("No path specified");

            if (isDir) Directory.Delete(fullName, true); else File.Delete(fullName);
            PostToWeb(new { type = "sftpOpResult", op = "delete", tabId, ok = true });
            PostToWeb(new { type = "sftpRefreshLocal", tabId, path = parentPath });
        }
        catch (Exception ex)
        {
            Log.Write($"[local] delete failed: {ex.Message}");
            PostToWeb(new { type = "sftpOpResult", op = "delete", tabId, ok = false, message = ex.Message });
        }
    }

    private void HandleLocalRename(JsonElement root)
    {
        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
        var fullName = root.TryGetProperty("fullName", out var rp) ? rp.GetString() ?? "" : "";
        var parentPath = root.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
        var newName = root.TryGetProperty("newName", out var nn) ? nn.GetString() ?? "" : "";
        try
        {
            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Path and new name are required");
            var dir = Path.GetDirectoryName(fullName) ?? parentPath;
            var dest = Path.Combine(dir, newName);
            if (Directory.Exists(fullName)) Directory.Move(fullName, dest); else File.Move(fullName, dest);
            PostToWeb(new { type = "sftpOpResult", op = "rename", tabId, ok = true });
            PostToWeb(new { type = "sftpRefreshLocal", tabId, path = parentPath });
        }
        catch (Exception ex)
        {
            Log.Write($"[local] delete failed: {ex.Message}");
            PostToWeb(new { type = "sftpOpResult", op = "delete", tabId, ok = false, message = ex.Message });
        }
    }

    private void HandleLocalMkdir(JsonElement root)
    {
        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
        var path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Directory name is required");
            Directory.CreateDirectory(Path.Combine(path, name));
            PostToWeb(new { type = "sftpOpResult", op = "mkdir", tabId, ok = true });
            PostToWeb(new { type = "sftpRefreshLocal", tabId, path });
        }
        catch (Exception ex)
        {
            Log.Write($"[local] mkdir failed: {ex.Message}");
            PostToWeb(new { type = "sftpOpResult", op = "mkdir", tabId, ok = false, message = ex.Message });
        }
    }

    private void HandleLocalZip(JsonElement root)
    {
        var tabId = root.TryGetProperty("tabId", out var t) ? t.GetString() ?? "" : "";
        var fullName = root.TryGetProperty("fullName", out var p) ? p.GetString() ?? "" : "";
        var parentPath = root.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
        try
        {
            if (string.IsNullOrWhiteSpace(fullName)) throw new ArgumentException("No path specified");
            if (!File.Exists(fullName)) throw new InvalidOperationException("Only files can be gzipped");
            var dest = fullName + ".gz";
            using (var src = File.OpenRead(fullName))
            using (var outfs = File.Create(dest))
            using (var gz = new System.IO.Compression.GZipStream(outfs, System.IO.Compression.CompressionLevel.Optimal))
            {
                src.CopyTo(gz);
            }
            PostToWeb(new { type = "sftpOpResult", op = "zip", tabId, ok = true });
            PostToWeb(new { type = "sftpRefreshLocal", tabId, path = parentPath });
        }
        catch (Exception ex)
        {
            Log.Write($"[local] gzip failed: {ex.Message}");
            PostToWeb(new { type = "sftpOpResult", op = "zip", tabId, ok = false, message = ex.Message });
        }
    }

    private void Spawn(string id, string command, string cwd, short cols, short rows)
    {
        var pty = new ConPty();
        pty.Output += data => PostToWeb(new { type = "data", id, data });
        pty.RawOutput += bytes => AppendLog(id, bytes);
        pty.Exited += code =>
        {
            Log.Write($"[{id}] process ended, code={code}\n");
            CloseLog(id);
            PostToWeb(new { type = "exit", id, code });
        };
        Log.Write($"[{id}] Spawn: {command} (cwd={cwd} cols={cols} rows={rows})");
        try
        {
            pty.Start(command, cwd, cols, rows);
            _sessions[id] = pty;
        }
        catch (Exception ex)
        {
            // Report detailed error to the frontend and console for diagnostics
            var native = ex is Win32Exception w ? $" NativeErrorCode={w.NativeErrorCode}" : "";
            var msg = $"Failed to start process: {ex.Message}{native} Command={command})";
            PostToWeb(new { type = "error", message = msg });
            const string esc = "\x1b";
            try { PostToWeb(new { type = "data", id, data = $"\r\n{esc}[31m[error] {msg}{esc}[0m\r\n" }); } catch { }
            Log.Write(msg);
        }
    }

    private static (short cols, short rows) ReadSize(JsonElement root)
    {
        short cols = (short)(root.TryGetProperty("cols", out var cEl) ? cEl.GetInt32() : 80);
        short rows = (short)(root.TryGetProperty("rows", out var rEl) ? rEl.GetInt32() : 24);
        return (cols, rows);
    }

    private void SendConns()
    {
        var list = _store.Items.Select(c => new
        {
            c.Id,
            c.Name,
            c.Host,
            c.Port,
            c.User,
            c.AuthMethod,
            c.KeyPath,
            c.Group,
            c.Theme,
            hasPassword = !string.IsNullOrEmpty(c.PasswordEnc),
        });
        PostToWeb(new { type = "conns", items = list });
    }

    private void SendFolders() => PostToWeb(new { type = "folders", items = _folders.Items });

    private void SendSnippets() => PostToWeb(new { type = "snippets", items = _snippets.Items });

    private void SendPrefs() => PostToWeb(new
    {
        type = "prefs",
        theme = _prefs.Current.Theme,
        fontSize = _prefs.Current.FontSize,
        logDir = ResolveLogDir()
    });

    private void SavePrefs(JsonElement root)
    {
        if (root.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String)
            _prefs.Current.Theme = t.GetString() ?? "default";
        if (root.TryGetProperty("fontSize", out var f) && f.TryGetDouble(out var fv))
            _prefs.Current.FontSize = fv;
        if (root.TryGetProperty("logDir", out var d) && d.ValueKind == JsonValueKind.String)
            _prefs.Current.LogDir = d.GetString() ?? "";
        _prefs.Save();
    }

    private string ResolveLogDir()
    {
        var dir = _prefs.Current.LogDir;
        if (!string.IsNullOrWhiteSpace(dir)) return dir;
        dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "termius-logs");
        return dir;
    }

    private void StartLog(string id, string? label, string? dirOverrride)
    {
        CloseLog(id);
        if (!_sessions.TryGetValue(id, out var pty))
        {
            PostToWeb(new { type = "error", message = $"Session not found: {id}" });
            return;
        }
        var dir = string.IsNullOrWhiteSpace(dirOverrride) ? ResolveLogDir() : dirOverrride;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            Log.Write($"[error] Failed to create log directory: {dir}. Error: {ex.Message}");
            PostToWeb(new { type = "error", message = $"Failed to create log directory: {dir}. Error: {ex.Message}" });
            return;
        }
        var tab = string.IsNullOrWhiteSpace(label) ? id : label;
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var ch in invalid) tab = tab.Replace(ch, '_');
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(dir, $"{tab}_{stamp}.log");

        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex)
        {
            PostToWeb(new { type = "error", message = $"Failed to create log file: {path}. Error: {ex.Message}" });
            return;
        }

        var separator = System.Text.Encoding.UTF8.GetBytes(
            $"{Environment.NewLine}---- session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ----{Environment.NewLine}");
        try { fs.Write(separator, 0, separator.Length); fs.Flush(); } catch { }
        ;

        var entry = new SessionLog { Stream = fs, Path = path, Decoder = Encoding.UTF8.GetDecoder(), Buffer = new System.Text.StringBuilder(), Cursor = 0, ConsecutiveBlankLines = 0 };
        lock (_logLock) _logs[id] = entry;

        //pty.RawOutput += bytes => AppendLog(id, bytes);

        PostToWeb(new { type = "logStatus", id, active = true, path });
        Log.Write($"[{id}] logging started -> {path}");
    }

    private void AppendLog(string id, byte[] bytes)
    {
        SessionLog? entry;
        lock (_logLock) { _logs.TryGetValue(id, out entry); }
        ClipboardCapture? cap;
        lock (_clipLock) { _clipCaptures.TryGetValue(id, out cap); }

        if (entry == null && cap == null) return;
        if (entry != null) ProcessOutputToLog(entry, bytes);
        if (cap != null) ProcessOutputToClipboard(cap, bytes);
    }

    private static void ProcessOutputToLog(SessionLog entry, byte[] bytes)
    {
        Log.Debug($"AppendLog id={entry.Path} bytes={bytes.Length}");
        lock (entry)
        {
            try
            {
                // Decode bytes using the per-session Decoder to handle multibyte sequences split across chunks
                int maxChars = entry.Decoder.GetCharCount(bytes, 0, bytes.Length);
                char[] chars = new char[maxChars];
                int charCount = entry.Decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
                string raw = new string(chars, 0, charCount);
                // Remove ANSI escape sequences but keep CR/LF and backspace
                string cleaned = _ansiRegex.Replace(raw, "");
                cleaned = _ctrlRegex.Replace(cleaned, "");

                // Process characters to handle CR (\r), LF (\n) and backspace (\b)
                for (int i = 0; i < cleaned.Length; i++)
                {
                    char c = cleaned[i];
                    if (c == '\r')
                    {
                        // carriage return -> move cursor to line start
                        entry.Cursor = 0;
                        continue;
                    }
                    else if (c == '\n')
                    {
                        // newline -> flush current buffer + newline to stream
                        var line = entry.Buffer.ToString();
                        bool isBlank = string.IsNullOrWhiteSpace(line);
                        // Append non-blank lines always. For blank lines, append only once (avoid consecutive blanks).
                        if (!isBlank || entry.ConsecutiveBlankLines == 0)
                        {
                            var toWrite = line + Environment.NewLine;
                            var outBytes = Encoding.UTF8.GetBytes(toWrite);
                            lock (entry.Stream)
                            {
                                entry.Stream.Write(outBytes, 0, outBytes.Length);
                                entry.Stream.Flush();
                            }
                        }
                        entry.ConsecutiveBlankLines = isBlank ? 1 : 0;
                        entry.Buffer.Clear();
                        entry.Cursor = 0;
                        continue;
                    }
                    else if (c == '\b')
                    {
                        // backspace -> remove previous char if any
                        if (entry.Cursor > 0)
                        {
                            entry.Buffer.Remove(entry.Cursor - 1, 1);
                            entry.Cursor--;
                        }
                        continue;
                    }
                    else
                    {
                        // printable char -> write/overwrite at cursor
                        if (entry.Cursor < entry.Buffer.Length)
                        {
                            entry.Buffer[entry.Cursor] = c;
                        }
                        else
                        {
                            entry.Buffer.Append(c);
                        }
                        entry.Cursor++;
                    }
                }
            }
            catch { }
        }
    }

    private static void ProcessOutputToClipboard(ClipboardCapture cap, byte[] bytes)
    {
        lock (cap)
        {
            try
            {
                int maxChars = cap.Decoder.GetCharCount(bytes, 0, bytes.Length);
                char[] chars = new char[maxChars];
                int charCount = cap.Decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
                string raw = new string(chars, 0, charCount);
                string cleaned = _ansiRegex.Replace(raw, "");
                cleaned = _ctrlRegex.Replace(cleaned, "");
                for (int i = 0; i < cleaned.Length; i++)
                {
                    char c = cleaned[i];
                    if (c == '\r')
                    {
                        cap.Cursor = 0;
                        continue;
                    }
                    else if (c == '\n')
                    {
                        var line = cap.LineBuffer.ToString();
                        bool isBlank = string.IsNullOrWhiteSpace(line);
                        // Append non-blank lines always. For blank lines, append only once to avoid consecutive blanks.
                        if (!isBlank || cap.ConsecutiveBlankLines == 0)
                        {
                            cap.FullText.AppendLine(line);
                        }
                        cap.ConsecutiveBlankLines = isBlank ? 1 : 0;
                        cap.LineBuffer.Clear();
                        cap.Cursor = 0;
                        continue;
                    }
                    else if (c == '\b')
                    {
                        if (cap.Cursor > 0)
                        {
                            cap.LineBuffer.Remove(cap.Cursor - 1, 1);
                            cap.Cursor--;
                        }
                        continue;
                    }
                    else
                    {
                        if (cap.Cursor < cap.LineBuffer.Length)
                        {
                            cap.LineBuffer[cap.Cursor] = c;
                        }
                        else
                        {
                            cap.LineBuffer.Append(c);
                        }
                        cap.Cursor++;
                    }
                }
            }
            catch { }
        }
    }

    private void StartClipCapture(string id)
    {
        StopClipCapture(id, copyToClipboard: false);
        if (!_sessions.ContainsKey(id))
        {
            Log.Write($"[error] Session not found for clipboard capture: {id}");
            PostToWeb(new { type = "error", message = $"Session not found: {id}" });
            return;
        }

        var cap = new ClipboardCapture
        {
            Decoder = Encoding.UTF8.GetDecoder(),
            FullText = new System.Text.StringBuilder(),
            LineBuffer = new System.Text.StringBuilder(),
            Cursor = 0,
            ConsecutiveBlankLines = 0
        };
        lock (_clipLock) { _clipCaptures[id] = cap; }
        PostToWeb(new { type = "clipCaptureStatus", id, active = true });
        Log.Write($"[{id}] clipboard capture started");
    }

    private void StopClipCapture(string id, bool copyToClipboard = true)
    {
        ClipboardCapture? cap;
        lock (_clipLock) { _clipCaptures.TryGetValue(id, out cap); if (cap != null) _clipCaptures.Remove(id); }
        if (cap == null) return;

        string capturedText = "";
        int linesCount = 0;

        lock (cap)
        {
            if (cap.LineBuffer.Length > 0)
            {
                cap.FullText.AppendLine(cap.LineBuffer.ToString());
                cap.LineBuffer.Clear();
                cap.Cursor = 0;
            }
            capturedText = cap.FullText.ToString().TrimEnd();
        }
        if (copyToClipboard && !string.IsNullOrEmpty(capturedText))
        {
            linesCount = capturedText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).Length;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        Clipboard.SetDataObject(capturedText, true);
                    }
                    catch { }
                });
            }
            catch { }
        }

        PostToWeb(new { type = "clipCaptureStatus", id, active = false, linesCount, charsCount = capturedText.Length });
        Log.Write($"[{id}] clipboard capture stopped");
    }

    private void CloseLog(string id)
    {
        SessionLog? entry;
        lock (_logLock) { _logs.TryGetValue(id, out entry); if (entry != null) _logs.Remove(id); }
        if (entry == null) return;
        try
        {
            lock (entry.Stream)
            {
                // Flush any pending decoder state (remaining chars) to the log
                try
                {
                    // flush any remaining decoder buffer (if any) - attempt decode of zero-length to flush state
                    try
                    {
                        char[] remBuf = new char[1024];
                        int remCount = entry.Decoder.GetChars(Array.Empty<byte>(), 0, 0, remBuf, 0);
                        if (remCount > 0)
                        {
                            var remStr = new string(remBuf, 0, remCount);
                            remStr = _ansiRegex.Replace(remStr, "");
                            remStr = _ctrlRegex.Replace(remStr, "");
                            var remBytes = Encoding.UTF8.GetBytes(remStr);
                            entry.Stream.Write(remBytes, 0, remBytes.Length);
                        }
                    }
                    catch { }

                    // flush any remaining buffered line (no terminating newline)
                    if (entry.Buffer.Length > 0)
                    {
                        var remLine = entry.Buffer.ToString();
                        var remBytes2 = Encoding.UTF8.GetBytes(remLine + Environment.NewLine);
                        entry.Stream.Write(remBytes2, 0, remBytes2.Length);
                        entry.Buffer.Clear();
                        entry.Cursor = 0;
                    }
                }
                catch { }
                entry.Stream.Flush();
                entry.Stream.Dispose();
            }
            Log.Write($"[{id}] logging stopped -> {entry.Path}");
        }
        catch { }
        ;
    }

    private void OpenLogDir()
    {
        try
        {
            var dir = ResolveLogDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write($"[error] Failed to open log directory: {ex.Message}");
            PostToWeb(new { type = "error", message = $"Failed to open log directory: {ex.Message}" });
        }
    }

    private void PickLogDir()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Select Logs Folder",
            FileName = "Select Folder",
            Filter = "Folders|*.folder",
            InitialDirectory = ResolveLogDir(),
            OverwritePrompt = false,
            CheckPathExists = true,
        };
        if (dlg.ShowDialog() == true)
        {
            var selectedDir = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrWhiteSpace(selectedDir))
            {
                _prefs.Current.LogDir = selectedDir;
                _prefs.Save();
                Log.Write($"Log directory set to: {selectedDir}");
                PostToWeb(new { type = "logDirPicked", path = selectedDir });
                SendPrefs();
            }
        }
    }

    private void SaveSnippet(JsonElement root)
    {
        var s = root.GetProperty("snippet");
        var id = s.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var snip = (!string.IsNullOrEmpty(id) ? _snippets.Items.FirstOrDefault(x => x.Id == id) : null)
                   ?? new Snippet();
        snip.Name = Str(s, "name");
        snip.Commands = Str(s, "commands");
        _snippets.Upsert(snip);
        Log.Write($"Snippet saved: {snip.Name}");
        SendSnippets();
    }

    private void SaveConn(JsonElement root)
    {
        var c = root.GetProperty("conn");
        var connId = c.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var conn = (!string.IsNullOrEmpty(connId) ? _store.Get(connId) : null) ?? new Connection();

        conn.Name = Str(c, "name");
        conn.Host = Str(c, "host");
        conn.Port = c.TryGetProperty("port", out var pEl) && pEl.TryGetInt32(out var pv) ? pv : 22;
        conn.User = Str(c, "user");
        conn.AuthMethod = Str(c, "authMethod", "password");
        conn.KeyPath = Str(c, "keyPath");
        conn.Group = Str(c, "group", "SSH");
        conn.Theme = Str(c, "theme", "default");

        // Senha
        if (c.TryGetProperty("password", out var pwEl))
            ConnectionStore.SetPassword(conn, pwEl.GetString());

        _store.Upsert(conn);
        PostToWeb(new { type = "connSaved", id = conn.Id });
        SendConns();
        Log.Write($"Connection saved: {conn.Name}");
    }

    private void PickKey()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select SSH Private Key",
            Filter = "Keys (id_*, *.pem, *.key)|id_*;*.pem;*.key|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".ssh"),
        };
        if (dlg.ShowDialog(this) == true)
            PostToWeb(new { type = "keyPicked", path = dlg.FileName });
    }

    private static string Str(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() ?? fallback : fallback;

    private void PostToWeb(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Web.CoreWebView2?.PostWebMessageAsJson(json));
            return;
        }
        Web.CoreWebView2?.PostWebMessageAsJson(json);
    }
}
