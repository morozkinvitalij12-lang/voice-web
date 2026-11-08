using System.Text;

namespace VoiceSiteLauncher;

internal static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var path = Path.Combine(Program.DataDirectory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 1_000_000)
                {
                    if (File.Exists(path + ".1"))
                        File.Move(path + ".1", path + ".2", true);
                    File.Move(path, path + ".1", true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(error.Message);
            }
        }
    }
}
