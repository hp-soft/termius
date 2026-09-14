using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SshManager.Pty;

public sealed class ConPty : IDisposable
{
	private IntPtr _hPC = IntPtr.Zero;
	private SafeFileHandle? _inputWrite;       // aqui vira stdin do shell
	private SafeFileHandle? _outputRead;       // lemos aqui <- stdout/stderr do shell
	private SafeFileHandle? _inputRead;
	private SafeFileHandle? _outputWrite;
		
	private FileStream? _writer;
	private FileStream? _reader;
	private PROCESS_INFORMATION _procInfo;
	private Thread? _readThread;
	private volatile bool _disposed;
		
	// Disparado (em thread background) quando shell produz saída
	public event Action<string>? Output;
		
	// Disparado quando o processo do shell termina
	public event Action<int>? Exited;
		
	public void Start(string command, string cwd, short cols, short rows)
	{
		// Pipes (InputRead/Writer) e (outputRead/Writer)
		if(!CreatePipe(out _inputRead, out _inputWrite, IntPtr.Zero, 0 ))
		{
			var err = Marshal.GetLastWin32Error();
			try { Output?.Invoke($"[log] CreatePipe(input) failed, GetLastWin32Error={err}\r\n"); } catch { }
			throw new Win32Exception(err,"CreatePipe (input) failed");
		}
		if(!CreatePipe(out _outputRead, out _outputWrite, IntPtr.Zero, 0 ))
		{
			var err = Marshal.GetLastWin32Error();
			try { Output?.Invoke($"[log] CreatePipe(output) failed, GetLastWin32Error={err}\r\n"); } catch { }
			throw new Win32Exception(err,"CreatePipe (output) failed");
		}
				
		// Cria o pseudo-console ligando as pontas "read do input" e "write do output"
		var size = new COORD { X = cols, Y = rows };
		int hr = CreatePseudoConsole(size, _inputRead!.DangerousGetHandle(),
							_outputWrite!.DangerousGetHandle(), 0, out _hPC);
		try { Output?.Invoke($"[log] CreatePseudoConsole hr={hr} hPC=0x{_hPC.ToString("X")}" + "\r\n"); } catch { }
		if(hr != 0)
			throw new Win32Exception(hr, "CreatePseudoConsole");
				
		// STARTUPINFOEX com o atributo que anexa o processo ao pseudo-console
		var siEx = new STARTUPINFOEX();
		siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
				
		var attrSize = IntPtr.Zero;
		var initOk = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
		try { Output?.Invoke($"[log] InitializeProcThreadAttributeList(query) returned {initOk}, attrSize={attrSize}\r\n"); } catch { }
		siEx.lpAttributeList = Marshal.AllocHGlobal(attrSize);
		initOk = InitializeProcThreadAttributeList(siEx.lpAttributeList, 1, 0, ref attrSize);
		if (!initOk)
		{
			var err = Marshal.GetLastWin32Error();
			try { Output?.Invoke($"[log] InitializeProcThreadAttributeList(init) failed, GetLastWin32Error={err}\r\n"); } catch { }
			throw new Win32Exception(err,"InitializeProcThreadAttributeList failed");
		}
		try { Output?.Invoke($"[log] InitializeProcThreadAttributeList(init) succeeded, lpAttributeList=0x{siEx.lpAttributeList.ToString("X")}\r\n"); } catch { }
				
		// Update the attribute list with a pointer to the pseudo-console handle.
		// The API expects a pointer to the handle, not the handle value itself.
		IntPtr pValue = IntPtr.Zero;
		try
		{
			pValue = Marshal.AllocHGlobal(IntPtr.Size);
			Marshal.WriteIntPtr(pValue, _hPC);
			if (!UpdateProcThreadAttribute(siEx.lpAttributeList, 0,
				(IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, pValue, (IntPtr)IntPtr.Size,
				IntPtr.Zero, IntPtr.Zero))
				throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
		}
		finally
		{
			if (pValue != IntPtr.Zero) Marshal.FreeHGlobal(pValue);
		}
					
		// Cria processo shell
		var pSec = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
		var tSec = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
		// Log command about to be spawned for diagnostics
		try { Output?.Invoke($"\r\n[log {DateTime.Now:O}] CreateProcess request: {command} cwd={cwd}\r\n"); } catch { }

		// Parse command into application and arguments to pass lpApplicationName separately
		string? exePath = null;
		string? args = null;
		if (!string.IsNullOrWhiteSpace(command))
		{
			var cmd = command.Trim();
			if (cmd.StartsWith("\""))
			{
				var end = cmd.IndexOf('\"', 1);
				if (end > 1)
				{
					exePath = cmd.Substring(1, end - 1);
					args = cmd.Substring(end + 1).Trim();
				}
			}
			else
			{
				var parts = cmd.Split(new[] { ' ' }, 2);
				exePath = parts[0];
				args = parts.Length > 1 ? parts[1] : string.Empty;
			}
		}
		if (string.IsNullOrWhiteSpace(exePath))
		{
			// fallback: treat whole command as application
			exePath = command;
			args = string.Empty;
		}

		try { Output?.Invoke($"[log] exe={exePath} args={args}\r\n"); } catch { }

		var argsBuilder = new System.Text.StringBuilder(args ?? string.Empty);

		bool ok = false;
		try
		{
			ok = CreateProcess(exePath, argsBuilder, ref pSec, ref tSec, false,
				EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero,
				string.IsNullOrWhiteSpace(cwd) ? null : cwd,
				ref siEx, out _procInfo);
			if (!ok)
				throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
		}
		catch (Exception ex)
		{
			// log detailed error
			try { Output?.Invoke($"[error] CreateProcess exception: {ex}\r\n"); } catch { }
			Console.WriteLine($"CreateProcess exception: {ex}");
			throw;
		}
		finally
		{
			// free the attribute list allocated earlier
			try { DeleteProcThreadAttributeList(siEx.lpAttributeList); } catch { }
			try { Marshal.FreeHGlobal(siEx.lpAttributeList); } catch { }
		}
				
		// As pontas que agora pertencem ao pseudo-console/filho podem ser fechadas do nosso lado
		_inputRead.Dispose(); _inputRead = null;
		_outputWrite.Dispose(); _outputWrite = null;
				
		_writer = new FileStream(_inputWrite!, FileAccess.Write);
		_reader = new FileStream(_outputRead!, FileAccess.Read);
				
		// Thread de Leitura
		_readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPty-Read" };
		_readThread.Start();

		// Observa o fim do processo para notificar UI
		ThreadPool.QueueUserWorkItem(_ =>
		{
			WaitForSingleObject(_procInfo.hProcess, INFINITE);
			GetExitCodeProcess(_procInfo.hProcess, out uint code);
			Exited?.Invoke((int)code);
		});
	}
		
	private void ReadLoop()
	{
		var buffer = new byte[4096];
		var decoder = System.Text.Encoding.UTF8.GetDecoder();
		var chars = new char[4096 * 2];
		try 
		{
				int read;
				while (!_disposed && _reader != null && (read = _reader.Read(buffer,0,buffer.Length)) > 0)
				{
						int n = decoder.GetChars(buffer, 0, read, chars, 0);
						if (n > 0) Output?.Invoke(new string(chars, 0, n));
				}
		}
		catch (Exception)
		{
				// Pipe fechado ao encerrar; ignoramos
		}
	}
		
	public void Write( string data )
	{
		if(_disposed || _writer == null) return;
		var bytes = System.Text.Encoding.UTF8.GetBytes(data);
		_writer.Write(bytes, 0, bytes.Length);
		_writer.Flush();
	}
		
	public void Resize( short cols, short rows )
	{
		if(_disposed || _hPC == IntPtr.Zero) return;
		ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
	}
		
	public void Dispose()
	{
		if(_disposed) return;
		_disposed = true;
			
		try { if (_hPC != IntPtr.Zero) ClosePseudoConsole(_hPC); } catch { }
		try { _writer?.Dispose(); } catch { }
		try { _reader?.Dispose(); } catch { }
		try { _inputWrite?.Dispose(); } catch { }
		try { _outputRead?.Dispose(); } catch { }
			
		try
		{
			if(_procInfo.hThread != IntPtr.Zero) CloseHandle(_procInfo.hThread);
			if(_procInfo.hProcess != IntPtr.Zero) CloseHandle(_procInfo.hProcess);
		}
		catch { }
	}
		
	private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
	private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
	private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
	private struct COORD {  public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
	private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

	[StructLayout(LayoutKind.Sequential)]
	private struct STARTUPINFO
	{
		public int cb;
		public string? lpReserved;
		public string? lpDesktop;
		public string? lpTitle;
		public int dwX, dwY, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
		public short wShowWindow, cbReserverd2;
		public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
	}
	
	[StructLayout(LayoutKind.Sequential)]
	private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
		
	[StructLayout(LayoutKind.Sequential)]
	private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
		
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);
		
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int CreatePseudoConsole( COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr phPC);
		
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
		
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern void ClosePseudoConsole(IntPtr hPC);
		
	[DllImport("kernel32.dll", SetLastError = true, EntryPoint = "InitializeProcThreadAttributeList")]
	private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

	[DllImport("kernel32.dll", SetLastError = false)]
	private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool CreateProcess ( string? lpApplicationName, System.Text.StringBuilder lpCommandLine,
		ref SECURITY_ATTRIBUTES lpProcessAttributes, ref SECURITY_ATTRIBUTES lpThreadAttributes,
		bool bInheritHandles, int dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
		ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation );

    [DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds );

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);
}	
