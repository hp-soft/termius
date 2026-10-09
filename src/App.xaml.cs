using System.IO;
using System.Windows;

namespace SshManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--gen-icon"))
        {
            var targetIco = e.Args.Length > 1 ? e.Args[1] : Path.Combine(AppContext.BaseDirectory, "app.ico");
            var targetPng = e.Args.Length > 2 ? e.Args[2] : Path.Combine(AppContext.BaseDirectory, "web", "app.png");

            var icoDir = Path.GetDirectoryName(Path.GetFullPath(targetIco));
            if (!string.IsNullOrEmpty(icoDir) && !Directory.Exists(icoDir)) Directory.CreateDirectory(icoDir);

            var pngDir = Path.GetDirectoryName(Path.GetFullPath(targetPng));
            if (!string.IsNullOrEmpty(pngDir) && !Directory.Exists(pngDir)) Directory.CreateDirectory(pngDir);

            IconBuilder.Generate(targetIco, targetPng);
            Environment.Exit(0);
            return;
        }

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var icoPath = Path.Combine(baseDir, "app.ico");
            var webIconPath = Path.Combine(baseDir, "web", "app.ico");
            if (!File.Exists(icoPath) || !File.Exists(webIconPath))
            {
                IconBuilder.Generate(icoPath, webIconPath);
            }
        }
        catch { };

        if(Environment.GetEnvironmentVariable(SftpService.AskpassModeEnv) == "1")
        {
            var pw = Environment.GetEnvironmentVariable(SftpService.AskpassPwEnv) ?? "";
            Console.Out.Write(pw);
            Console.Out.Write("\n");
            Console.Out.Flush();
            Environment.Exit(0);
            return;
        }
        base.OnStartup(e);
    }
}
