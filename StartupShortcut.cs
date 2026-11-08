using System.Runtime.InteropServices;

namespace VoiceSiteLauncher;

internal static class StartupShortcut
{
    public static void Manage(LaunchOptions options)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Program.AppName + ".lnk");
        if (options.RemoveStartup)
        {
            if (File.Exists(path))
                File.Delete(path);
            MessageBox.Show("Автозапуск отключён.", Program.AppName);
            return;
        }
        if (File.Exists(path))
            throw new InvalidOperationException("Ярлык уже существует. Сначала отключите автозапуск, затем создайте его заново.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = Environment.ProcessPath!;
            link.Arguments = $"--model {options.Model} --device {options.Device}";
            link.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
            link.IconLocation = Environment.ProcessPath! + ",0";
            link.Description = "Голосовое открытие сайтов: V + F6";
            link.Save();
        }
        finally
        {
            if (shortcut is not null)
                Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null)
                Marshal.FinalReleaseComObject(shell);
        }
        MessageBox.Show("Автозапуск включён. При входе Windows запросит разрешение UAC.", Program.AppName);
    }
}
