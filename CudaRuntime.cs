using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VoiceSiteLauncher;

// CUDA-рантайм не входит в EXE: он скачивается по требованию, только если в системе
// есть драйвер NVIDIA и CUDA Toolkit 13. Загрузчик Whisper.net сам перепроверяет
// версию cudart и наличие GPU и при неудаче переходит на CPU.
internal static class CudaRuntime
{
    private const string PackageUrl = "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.cuda.windows/1.9.1/whisper.net.runtime.cuda.windows.1.9.1.nupkg";
    private const string PackageSha512 = "J7ZyOZmhgrZCZLGNKNoIxWGcDQYYTsqiDYaHTqAiy/Yo3wN/7UAXqPEA9IICmGwp8X0WZBNkcD86934yALgEZQ==";
    private const string EntryPrefix = "build/win-x64/";
    private static readonly string[] NativeFiles =
        ["whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-cuda-whisper.dll"];

    public static bool IsToolkitPresent()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var driver))
            return false;
        NativeLibrary.Free(driver);
        if (!NativeLibrary.TryLoad("cudart64_13.dll", out var runtime))
            return false;
        NativeLibrary.Free(runtime);
        return true;
    }

    public static async Task<string> EnsureAsync(string cacheRoot, Action<string> status, CancellationToken token)
    {
        var target = Path.Combine(cacheRoot, "runtimes", "cuda", "win-x64");
        if (NativeFiles.All(file => File.Exists(Path.Combine(target, file))))
            return cacheRoot;
        Directory.CreateDirectory(target);
        status("Скачивание CUDA-рантайма (~140 МБ)...");
        var package = Path.Combine(cacheRoot, "cuda.nupkg.part");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.GetAsync(PackageUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var destination = new FileStream(package, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(destination, token);
            await using (var verify = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            {
                var hash = Convert.ToBase64String(await SHA512.HashDataAsync(verify, token));
                if (hash != PackageSha512)
                    throw new InvalidDataException("CUDA-рантайм скачан повреждённым (несовпадение SHA512). Повторите запуск.");
            }
            using (var archive = ZipFile.OpenRead(package))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith(EntryPrefix, StringComparison.Ordinal))
                        continue;
                    var file = entry.FullName[EntryPrefix.Length..];
                    if (NativeFiles.Contains(file))
                        entry.ExtractToFile(Path.Combine(target, file), true);
                }
            }
            if (!NativeFiles.All(file => File.Exists(Path.Combine(target, file))))
                throw new InvalidDataException("CUDA-рантайм распакован не полностью. Повторите запуск.");
            return cacheRoot;
        }
        finally
        {
            if (File.Exists(package))
                File.Delete(package);
        }
    }
}