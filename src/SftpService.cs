using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SshManager.Pty;

namespace SshManager
{
    public sealed class SftpFileEntry { public string Name { get; set; } = ""; public string FullName { get; set; } = ""; public bool IsDirectory { get; set; } public long Size { get; set; } public DateTime LastWriteTime { get; set; } public string Permissions { get; set; } = ""; public string Owner { get; set; } = ""; public string Group { get; set; } = ""; }

    public class SftpService : IDisposable
    {
        public const string AskpassPwEnv = "SSHMGR_ASKPASS_PW";
        public const string AskpassModeEnv = "SSHMGR_ASKPASS";

        private readonly string _host;
        private readonly int _port;
        private readonly string _user;
        private readonly string? _password;
        private readonly string? _keyPath;
        private readonly string _sftpExe;

        private static readonly Regex LsLine = new(
            @"^([dlbcps\-][rwxstST\-]{9})[\+\.@]?\s+\d+\s+(.+?)\s+(\d+)\s+([A-Za-z]{3}\s+\d{1,2}\s+[\d:]+)\s+(.*)$",
            RegexOptions.Compiled);
        
        public SftpService(string host, int port, string user, string? password = null, string? keyPath = null)
        {
            _host = host;
            _port = port;
            _user = user;
            _password = password;
            _keyPath = keyPath;
            _sftpExe = GitBash.FindSftp()
                ?? throw new InvalidOperationException("sftp.exe not found...");
        }

        public IEnumerable<SftpFileEntry> ListDirectory(string path)
        {
            var p = string.IsNullOrEmpty(path) ? "." : path;
            var output = RunBatch($"ls -la {Quote(p)}");
            var basePath = p.EndsWith("/") ? p : p + "/";
            var result = new List<SftpFileEntry>();

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var m = LsLine.Match(line);
                if (!m.Success) continue;
                var name = m.Groups[5].Value.Trim();
                var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) name = name.Substring(0, arrow);
                if (name == "." || name == "..") continue;

                var perms = m.Groups[1].Value;
                long.TryParse(m.Groups[3].Value, out var size);
                DateTime.TryParse(m.Groups[4].Value, out var mtime);
                var ownerGroup = m.Groups[2].Value.Trim();
                var sp = ownerGroup.IndexOf(' ');
                var owner = sp < 0 ? ownerGroup : ownerGroup.Substring(0, sp);
                var grp = sp < 0 ? "" : ownerGroup.Substring(sp + 1).Trim();
                result.Add(new SftpFileEntry
                {
                    Name = name,
                    FullName = basePath + name,
                    IsDirectory = perms.Length > 0 && perms[0] == 'd',
                    Size = size,
                    LastWriteTime = mtime,
                    Permissions = perms,
                    Owner = owner,
                    Group = grp
                }
                );
            }
            return result;
        }

        public string GetWorkingDirectory()
        {
            try
            {
                var output = RunBatch("pwd");
                foreach (var raw in output.Split('\n'))
                {
                    var line = raw.Trim();
                    var idx = line.IndexOf(":");
                    if (line.StartsWith("Remote working directory", StringComparison.OrdinalIgnoreCase ) && idx >= 0) 
                        return line.Substring(idx + 1).Trim();
                }
            }
            catch { }
            return "/";
        }

        public void DownloadFile(string remotePath, string localPath)
        {
            RunBatch($"get {Quote(remotePath)} {QuoteLocal(localPath)}");
        }

        public void UploadFile(string localPath, string remoteDir)
        {
            var name = System.IO.Path.GetFileName(localPath);
            var remotePath = remoteDir.EndsWith("/") ? remoteDir + name : remoteDir + "/" + name;
            RunBatch($"put {QuoteLocal(localPath)} {Quote(remotePath)}");
        }

        public void CreateDirectory(string remotePath)
        {
            RunBatch($"mkdir {Quote(remotePath)}");
        }

        public void RemoveFile(string remotePath)
        {
            RunBatch($"rm {Quote(remotePath)}");
        }

        public void RemoveDirectory(string remotePath)
        {
            RunBatch($"rmdir {Quote(remotePath)}");
        }

        public void ChangePermissions(string remotePath, string mode)
        {
            var m = mode.Trim();
            if (string.IsNullOrEmpty(m) || !System.Text.RegularExpressions.Regex.IsMatch(m, @"^[0-7]{3,4}$"))
                throw new ArgumentException($"Invalid octal permission mode: '{mode}'");
            RunBatch($"chmod {m} {Quote(remotePath)}");
        }

        public void RenameRemote(string oldPath, string newPath)
        {
            RunBatch($"rename {Quote(oldPath)} {Quote(newPath)}");
        }
        public void GzipRemote(string remotePath)
        {
            RunSsh($"gzip -f -- {ShellQuote(remotePath)}");
        }

        private string RunBatch(string commands)
        {
            var args = new StringBuilder();
            args.Append($"-P {_port} -o StrictHostKeyChecking=accept-new -o BatchMode=no");
            if (!string.IsNullOrWhiteSpace(_keyPath))
                args.Append($" -i \"{_keyPath}\"");
            args.Append($" -b - {_user}@{_host}");

            var psi = new ProcessStartInfo(_sftpExe, args.ToString())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (string.IsNullOrWhiteSpace(_keyPath) && !string.IsNullOrEmpty(_password))
            {
                psi.Environment[AskpassModeEnv] = "1";
                psi.Environment[AskpassPwEnv] = _password;
                psi.Environment["SSH_ASKPASS"] = Environment.ProcessPath ?? _sftpExe;
                psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
                psi.Environment["DISPLAY"] = "localhost:0";
                psi.Environment["SSH_ASKPASS_DISPLAY"] = "localhost:0";
            }

            using var proc = new Process{StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.StandardInput.WriteLine(commands);
            proc.StandardInput.WriteLine("bye");
            proc.StandardInput.Close();
            proc.WaitForExit();

            if( proc.ExitCode != 0)
            {
                var err = stderr.ToString().Trim();
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(err) ? $"sftp exited code {proc.ExitCode}" : err
                    );
            }
            return stdout.ToString();
        }

        private string RunSsh(string remoteCommand)
        {
            var ssh = GitBash.FindSsh() ??
                throw new InvalidOperationException("ssh.exe not found");

            var args = new StringBuilder();
            args.Append($"-p {_port} -o StrictHostKeyCheckinf=accept-new -o BatchMode=no");
            if (!string.IsNullOrWhiteSpace(_keyPath))
                args.Append($" -i \"{_keyPath}\"");
            args.Append($" {_user}@{_host} {remoteCommand}");

            var psi = new ProcessStartInfo(ssh, args.ToString())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ApplyAskpass(psi);

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                var err = stderr.ToString().Trim();
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(err) ? $"ssh exited code {proc.ExitCode}" : err);
            }
            return stdout.ToString();
        }

        private void ApplyAskpass(ProcessStartInfo psi)
        {
            if(string.IsNullOrWhiteSpace(_keyPath) && !string.IsNullOrEmpty(_password))
            {
                psi.Environment[AskpassModeEnv] = "1";
                psi.Environment[AskpassPwEnv] = _password;
                psi.Environment["SSH_ASKPASS"] = Environment.ProcessPath ?? _sftpExe;
                psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
                psi.Environment["DISPLAY"] = "localhost:0";
                psi.Environment["SSH_ASKPASS_DISPLAY"] = "localhost:0";
            }
        }
        private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

        private static string QuoteLocal(string path) => Quote(path.Replace('\\', '/'));

        private static string ShellQuote(string path) => "'" + path.Replace("'", "'\\''") + "'";
        public void Dispose()
        {
        }
    }
}
