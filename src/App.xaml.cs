using System.Windows;

namespace SshManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if(Environment.GetEnvironmentVariable(SftpService.AskpassModeEnv) == "1")
        {
            var pw = Environment.GetEnvironmentVariable(SftpService.AskpassModeEnv) ?? "";
            Console.Out.Write(pw);
            Console.Out.Write("\n");
            Console.Out.Flush();
            Environment.Exit(0);
            return;
        }
        base.OnStartup(e);
    }
}
