using System.Runtime.InteropServices;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace VoiceSiteLauncher;

internal sealed class SpeechRecognizer : IAsyncDisposable
{
    private readonly string _modelPath;
    private readonly Action<string> _status;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private bool _usingGpu;
    private static readonly List<nint> NativeHandles = [];

    private SpeechRecognizer(string modelPath, Action<string> status)
    {
        _modelPath = modelPath;
        _status = status;
    }

    public static async Task<SpeechRecognizer> CreateAsync(LaunchOptions options, Action<string> status, CancellationToken token)
    {
        var directory = Path.Combine(Program.DataDirectory, "models");
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, $"ggml-{options.Model}.bin");
        var size = options.Model == "base" ? 147_951_465L : 487_601_967L;
        if (!File.Exists(modelPath) || new FileInfo(modelPath).Length != size)
        {
            status($"Скачивание модели {options.Model}...");
            var partial = modelPath + ".part";
            try
            {
                using var stream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    options.Model == "base" ? GgmlType.Base : GgmlType.Small, cancellationToken: token);
                await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    await stream.CopyToAsync(file, token);
                if (new FileInfo(partial).Length != size)
                    throw new InvalidDataException("Модель скачана не полностью. Проверьте интернет и повторите запуск.");
                File.Move(partial, modelPath, true);
            }
            finally
            {
                if (File.Exists(partial))
                    File.Delete(partial);
            }
        }
        token.ThrowIfCancellationRequested();
        status("Загрузка модели...");
        // CRT предзагружается по абсолютному пути: apphost находится вне каталога распаковки bundle.
        foreach (var name in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll" })
            NativeHandles.Add(NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "native", name)));
        RuntimeOptions.RuntimeLibraryOrder = options.Device == "cpu"
            ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        var recognizer = new SpeechRecognizer(modelPath, status);
        try
        {
            try
            {
                recognizer.Load(options.Device != "cpu");
            }
            catch (Exception error) when (options.Device != "cpu" && !token.IsCancellationRequested)
            {
                AppLog.Write("CUDA недоступна, переход на CPU: " + error.Message);
                await recognizer.ReleaseModelAsync();
                recognizer.Load(false);
            }
            token.ThrowIfCancellationRequested();
            return recognizer;
        }
        catch
        {
            await recognizer.DisposeAsync();
            throw;
        }
    }

    private void Load(bool useGpu)
    {
        _factory = WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
        _processor = CreateProcessor();
        _usingGpu = useGpu && RuntimeOptions.LoadedLibrary == RuntimeLibrary.Cuda;
        AppLog.Write("Распознавание: " + (_usingGpu ? "CUDA (whisper.cpp)" : "CPU (whisper.cpp)"));
    }

    public async Task<string> RecognizeAsync(float[] audio, CancellationToken token)
    {
        try
        {
            return await TranscribeAsync(audio, token);
        }
        catch (Exception error) when (_usingGpu && !token.IsCancellationRequested)
        {
            AppLog.Write("Ошибка CUDA, повтор на CPU: " + error.Message);
            _status("Переход на CPU...");
            await ReleaseModelAsync();
            Load(false);
            return await TranscribeAsync(audio, token);
        }
    }

    private WhisperProcessor CreateProcessor() => _factory!.CreateBuilder()
        .WithLanguage("auto")
        .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 4))
        .WithNoSpeechThreshold(0.6f)
        .Build();

    private async Task<string> TranscribeAsync(float[] audio, CancellationToken token)
    {
        var processor = _processor ??= CreateProcessor();
        try
        {
            token.ThrowIfCancellationRequested();
            var segments = new List<string>();
            // Полное перечисление служит барьером native task; отмену проверяем только после его завершения.
            await foreach (var segment in processor.ProcessAsync(audio, CancellationToken.None))
                segments.Add(segment.Text.Trim());
            token.ThrowIfCancellationRequested();
            return string.Join(" ", segments.Where(x => x.Length > 0)).Trim();
        }
        finally
        {
            await processor.DisposeAsync();
            _processor = null;
        }
    }

    private async ValueTask ReleaseModelAsync()
    {
        if (_processor is not null)
            await _processor.DisposeAsync();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
        _usingGpu = false;
    }

    public ValueTask DisposeAsync() => ReleaseModelAsync();
}
