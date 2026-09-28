using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using SshManager.Pty;
using SshManager.Storage;

namespace SshManager;
internal static class Log
{
    private static readonly object _lock = new object();
    public static string FileLocation { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshManager.log");

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
	private readonly Dictionary<string, ConPty> _sessions = new();
	private readonly ConnectionStore _store = new();
	private readonly FolderStore _folders = new();
	private readonly SnippetStore _snippets = new();
	private readonly PrefsStore _prefs = new();

	public MainWindow()
	{
		InitializeComponent();
		_store.Load();
		_folders.Load();
		_snippets.Load();
		_prefs.Load();
		Loaded += OnLoaded;
		Closed += (_, _) => { foreach (var s in _sessions.Values) s.Dispose(); };
		// Corrige o maximizar de janela borderless (WindowStyle=None): sem isto
		// o conteudo extrapola a tela e o rodape (nova conexao/status) some
		SourceInitialized += (_, _) =>
		{
			var handle = new WindowInteropHelper(this).Handle;
			HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
		};
		// A margem que expoe as bordas para resize so faz sentido no estado Normal
		// maximizado ela viraria uma borda vazia em volta do conteudo
		StateChanged += (_, _) =>
			RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(6);
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
		=> WindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState.Maximized;

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
			MessageBox.Show(
				"Falha ao inicializar o WebView2:\r\n\r\n" + ex.Message +
				"\r\n\r\nVerifique se o WebView2 Runtime esta instalado",
				"RTermius",
				MessageBoxButton.OK,
				MessageBoxImage.Error
				);
			Close();
		};
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
			var fallback = Path.Combine(baseUserData, "instances",
				Environment.ProcessId.ToString());
			env = await CoreWebView2Environment.CreateAsync(null, fallback);
			await Web.EnsureCoreWebView2Async(env);
		}

		Web.CoreWebView2.WebMessageReceived += OnWebMessage;
		// Abrir DevTools automaticamente para depuração (remover após diagnosticar)
		try { Web.CoreWebView2.OpenDevToolsWindow(); } catch { }


		Web.CoreWebView2.Settings.AreDevToolsEnabled = true;


        // Concede automaticamente o acesso ao clipboard para a origem local
        // (necessario para o paste com botao direito via navigator.clipboard)
        Web.CoreWebView2.PermissionRequested += (_, args) =>
		{
			if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
				args.State = CoreWebView2PermissionState.Allow;
		};

		var webDir = Path.Combine(AppContext.BaseDirectory, "web");
		Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
			"app.local", webDir, CoreWebView2HostResourceAccessKind.Allow);

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

			switch (type)
			{
				case "start": StartSession(id, root); break;
				case "startSsh": StartSsh(id, root); break;
				case "input":
					{
						var data = root.GetProperty("data").GetString() ?? "";
						Console.WriteLine($"[ws->host] input id={id} len={data.Length}");
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
					break;

				case "loadConns": SendConns(); break;
				case "saveConn": SaveConn(root); break;
				case "deleteConn": _store.Remove(root.GetProperty("connId").GetString() ?? ""); SendConns(); break;
				case "pickKey": PickKey(); break;

				case "loadFolders": SendFolders(); break;
				case "addFolder": _folders.Add(root.GetProperty("path").GetString() ?? ""); SendFolders(); break;
				case "removeFolder": _folders.Remove(root.GetProperty("path").GetString() ?? ""); SendFolders(); break;

				case "loadSnippets": SendSnippets(); break;
				case "saveSnippet": SaveSnippet(root); break;
				case "deleteSnippet": _snippets.Remove(root.GetProperty("snippetId").GetString() ?? ""); SendSnippets(); break;

				case "loadPrefs": SendPrefs(); break;
			case "requestPaste":
			{
				string txt = "";
				try
				{
					Dispatcher.Invoke(() => { if (Clipboard.ContainsText()) txt = Clipboard.GetText(); });
				}
				catch { }
				var msg = $"[requestPaste] id={id} len={(txt ?? "").Length}";
				Console.WriteLine(msg);
			// If the session is active on the host, write directly to the pty to avoid extra roundtrips.
			if (_sessions.TryGetValue(id, out var pty))
			{
				try
				{
					pty.Write(txt ?? "");
					Console.WriteLine($"[requestPaste] wrote { (txt ?? "").Length } bytes directly to session {id}");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[requestPaste] failed to write to session {id}: {ex.Message}");
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
				case "savePrefs": SavePrefs(root); break;
			}
		}
		catch (Exception ex)
		{
			PostToWeb(new { type = "error", message = ex.Message });
		}
	}

	private void StartSession(string id, JsonElement root)
	{
		var bash = GitBash.Find();
		if (bash is null)
		{
			const string esc = "<-";
			PostToWeb(new { type = "data", id, data =
				$"\r\n{esc}[31mGit Bash (bash.exe) nao encontrado {esc}[0m\r\n" +
				"Instale o Git for Windows ou ajuste GitBash.Find().\r\n" });
			return;
		}

		var (cols, rows) = ReadSize(root);
		var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.CurrentDirectory;
		Spawn(id, $"\"{bash}\" --login -i", home, cols, rows);
	}

	private void StartSsh(string id, JsonElement root)
	{
		var conn = _store.Get(root.GetProperty("connId").GetString() ?? "" );
		if (conn is null) { PostToWeb(new { type = "error", message = "Conexao nao encontrada." }); return; }

		var ssh = GitBash.FindSsh();
		if (ssh is null)
		{
			const string esc = "<-";
			PostToWeb(new { type = "data", id, data =
				$"\r\n{esc}[31ssh (ssh.exe) nao encontrado {esc}[0m\r\n" +
				"Instale o Git for Windows (OpenSSH) ou OpenSSH do Windows ou ajuste GitBash.FindSsh().\r\n" });
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
		Spawn(id, $"\"{ssh}\" {args}", home, cols, rows);
	}

	private void Spawn(string id, string command, string cwd, short cols, short rows)
	{
		var pty = new ConPty();
		pty.Output += data => PostToWeb(new { type = "data", id, data });
		pty.Exited += code =>
		{
			Log.Write($"[{id}] processo encerrado, codigo={code}\n");
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
			var msg = $"Falha Iniciar processo: {ex.Message}{native} Command={command})";
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
			c.Id, c.Name, c.Host, c.Port, c.User, c.AuthMethod, c.KeyPath, c.Group, c.Theme,
			hasPassword = !string.IsNullOrEmpty(c.PasswordEnc),
		});
		PostToWeb(new { type = "conns", items = list });
	}

	private void SendFolders() => PostToWeb(new { type = "folders", items = _folders.Items });

	private void SendSnippets() => PostToWeb(new { type = "snippets", items = _snippets.Items });

	private void SendPrefs() => PostToWeb(new { type = "prefs", theme = _prefs.Current.Theme, fontSize = _prefs.Current.FontSize });

	private void SavePrefs(JsonElement root)
	{
		if (root.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String)
			_prefs.Current.Theme = t.GetString() ?? "default";
		if (root.TryGetProperty("fontSize", out var f) && f.TryGetDouble(out var fv))
			_prefs.Current.FontSize = fv;
		_prefs.Save();
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
	}

	private void PickKey()
	{
		var dlg = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Selecione a chave Privada SSH",
			Filter = "Chaves (id_*, *.pem, *.key)|id_*;*.pem;*.key|Todos os arquivos (*.*)|*.*",
			InitialDirectory = Path.Combine(
				Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".ssh"),
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