using System;
using System.Collections.Generic;
using Renci.SshNet;

namespace SshManager
{
    public sealed class SftpFileEntry { public string Name { get; set; } = ""; public string FullName { get; set; } = ""; public bool IsDirectory { get; set; } public long Size { get; set; } public DateTime LastWriteTime { get; set; } public string Permissions { get; set; } = ""; public string Owner { get; set; } = ""; public string Group { get; set; } = ""; }

    public class SftpService : IDisposable
    {
        private readonly SftpClient _client;

        public SftpService(string host, int port, string user, string? password = null, string? keyPath = null)
        {
            if (!string.IsNullOrEmpty(keyPath))
            {
                var key = new PrivateKeyFile(keyPath);
                _client = new SftpClient(host, port, user, key);
            }
            else
            {
                _client = new SftpClient(host, port, user, password);
            }
            _client.Connect();
        }

        public IEnumerable<SftpFileEntry> ListDirectory(string path)
        {
            foreach (var f in _client.ListDirectory(path))
            {
                if (f.Name == "." || f.Name == "..") continue;
                var perms = "";
                try { perms = f.Attributes != null ? f.Attributes.ToString() ?? "" : ""; } catch { perms = ""; }
                var owner = "";
                var group = "";
                try
                {
                    var pi = f.GetType().GetProperty("Owner");
                    if (pi != null) owner = pi.GetValue(f)?.ToString() ?? "";
                }
                catch { owner = ""; }
                try
                {
                    var pg = f.GetType().GetProperty("Group");
                    if (pg != null) group = pg.GetValue(f)?.ToString() ?? "";
                }
                catch { group = ""; }
                yield return new SftpFileEntry { Name = f.Name, FullName = f.FullName, IsDirectory = f.IsDirectory, Size = f.Length, LastWriteTime = f.LastWriteTime, Permissions = perms, Owner = owner, Group = group };
            }
        }

        public string GetWorkingDirectory()
        {
            try { return _client.WorkingDirectory ?? "/"; } catch { return "/"; }
        }

        public void DownloadFile(string remotePath, string localPath)
        {
            using var fs = System.IO.File.OpenWrite(localPath);
            _client.DownloadFile(remotePath, fs);
        }

        public void UploadFile(string localPath, string remoteDir)
        {
            var name = System.IO.Path.GetFileName(localPath);
            using var fs = System.IO.File.OpenRead(localPath);
            _client.UploadFile(fs, (remoteDir.EndsWith("/") ? remoteDir : remoteDir + "/") + name);
        }

        public void Dispose()
        {
            try { _client.Disconnect(); } catch { }
            try { _client.Dispose(); } catch { }
        }
    }
}
