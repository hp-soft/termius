using System.Diagnostics;
using System.IO;

namespace SshManager.Pty;

public static class GitBash
{
    public static string? Find()
    {
        var candidates = new[]
        {
            // common locations (bin)
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            Combine(Environment.GetEnvironmentVariable("ProgramW6432"), "Git", "bin", "bash.exe"),
            Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Programs", "Git", "bin", "bash.exe"),
            // some installations put bash under usr\bin
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\Program Files (x86)\Git\usr\bin\bash.exe",
            Combine(Environment.GetEnvironmentVariable("ProgramW6432"), "Git", "usr", "bin", "bash.exe"),
            Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Programs", "Git", "usr", "bin", "bash.exe"),
        };

        var found = candidates.FirstOrDefault(p => p is not null && File.Exists(p));
        if (found is not null) return found;

        // fallback: try to resolve via PATH (where.exe)
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", "bash")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                var line = proc.StandardOutput.ReadLine();
                if (!string.IsNullOrEmpty(line) && File.Exists(line)) return line;
            }
        }
        catch { }

        return null;
    }

    public static string? FindSsh()
    {
        var candidates = new[]
        {
            // common locations for ssh
            @"C:\Program Files\Git\usr\bin\ssh.exe",
            @"C:\Program Files (x86)\Git\usr\bin\ssh.exe",
            Combine(Environment.GetEnvironmentVariable("ProgramW6432"), "Git", "usr", "bin", "ssh.exe"),
            Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Programs", "Git", "usr", "bin", "ssh.exe"),
            // Windows OpenSSH
            Combine(Environment.GetEnvironmentVariable("WINDIR"), "System32", "OpenSSH", "ssh.exe"),
        };

        var found = candidates.FirstOrDefault(p => p is not null && File.Exists(p));
        if (found is not null) return found;

        // fallback: try to resolve via PATH (where.exe)
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", "ssh")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                var line = proc.StandardOutput.ReadLine();
                if (!string.IsNullOrEmpty(line) && File.Exists(line)) return line;
            }
        }
        catch { }

        return null;
    }

    private static string? Combine(string? root, params string[] parts)
        => string.IsNullOrEmpty(root) ? null : Path.Combine(new[] { root }.Concat(parts).ToArray());
}