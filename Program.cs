using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace VoiceSiteLauncher;

internal sealed record LaunchOptions(string Model, string Device, bool InstallStartup, bool RemoveStartup)
{
    public static LaunchOptions Parse(string[] args)
    {
        var model = "small";
        var device = "auto";
        var install = false;
        var remove = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
                    break;
                case "--device" when i + 1 < args.Length:
                    device = args[++i];
                    break;
                case "--install-startup":
                    install = true;
                    break;
                case "--remove-startup":
                    remove = true;
                    break;
                default:
                    throw new ArgumentException($"Неизвестный или неполный параметр: {args[i]}");
            }
        }
        if (model is not ("base" or "small") || device is not ("auto" or "cpu" or "cuda") || install && remove)
            throw new ArgumentException("Параметры: --model base|small, --device auto|cpu|cuda, --install-startup или --remove-startup.");
        return new(model, device, install, remove);
    }
}

internal static class Program
{
    public const string AppName = "VoiceSiteLauncher";
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var options = LaunchOptions.Parse(args);
            if (options.InstallStartup || options.RemoveStartup)
            {
                StartupShortcut.Manage(options);
                return;
            }
            if (!EnsureAdministrator(args))
                return;
            using var mutex = new Mutex(true, $"Local\\{AppName}", out var created);
            if (!created)
            {
                MessageBox.Show("Программа уже запущена. Найдите микрофон в системном трее.", AppName);
                return;
            }
            try
            {
                Directory.CreateDirectory(DataDirectory);
                using var context = new TrayApplication(options);
                Application.Run(context);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool EnsureAdministrator(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return true;
        if (MessageBox.Show("Для работы поверх повышенных окон нужны права администратора. Перезапустить с повышенными правами?", AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return false;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (var argument in args)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            MessageBox.Show("Повышение прав отменено. Программа не запущена.", AppName);
        }
        return false;
    }
}
