using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace VoiceSiteLauncher;

internal static class StartupShortcut
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenDuplicate = 0x0002;

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Program.AppName + ".lnk");

    public static bool IsEnabled(string? path = null) => File.Exists(path ?? ShortcutPath);

    public static T ForInteractiveUser<T>(Func<string, T> action)
    {
        // После UAC процесс может принадлежать другому администратору, но трей — исходному пользователю.
        var window = GetShellWindow();
        if (window == 0)
            throw new InvalidOperationException("Не удалось определить пользователя рабочего стола. Откройте настройки повторно после запуска Проводника.");
        if (GetWindowThreadProcessId(window, out var processId) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var opened = OpenProcessToken(process, TokenQuery | TokenImpersonate | TokenDuplicate, out var token);
        using (token)
        {
            if (!opened)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return ForUser(token, action);
        }
    }

    internal static T ForUser<T>(SafeAccessTokenHandle token, Func<string, T> action)
    {
        var path = GetPathForUser(token);
        return WindowsIdentity.RunImpersonated(token, () => action(path));
    }

    private static string GetPathForUser(SafeAccessTokenHandle token)
    {
        var folderId = new Guid("B97D20BB-F46A-4C97-BA10-5E3608430854");
        nint path = 0;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(in folderId, 0, token, out path));
            return Path.Combine(Marshal.PtrToStringUni(path)!, Program.AppName + ".lnk");
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    public static void Manage(LaunchOptions options)
    {
        if (options.RemoveStartup)
        {
            Remove();
            MessageBox.Show("Автозапуск отключён.", Program.AppName);
            return;
        }
        Install(options);
        MessageBox.Show("Автозапуск включён. При входе Windows запросит разрешение UAC.", Program.AppName);
    }

    public static void Remove(string? path = null)
    {
        path ??= ShortcutPath;
        if (File.Exists(path))
            File.Delete(path);
    }

    public static void Install(LaunchOptions options, string? path = null)
    {
        path ??= ShortcutPath;
        if (IsEnabled(path))
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
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, SafeAccessTokenHandle token, out nint path);
}
