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
    private bool _preferGpu;
    private DateTimeOffset _lastUsed;
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
        string? libraryPath = null;
        if (options.Device != "cpu")
        {
            if (CudaRuntime.IsToolkitPresent())
            {
                try
                {
                    libraryPath = await CudaRuntime.EnsureAsync(Path.Combine(Program.DataDirectory, "runtime"), status, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    AppLog.Write("CUDA-рантайм недоступен, переход на CPU: " + error.Message);
                }
            }
            else
            {
                AppLog.Write("CUDA Toolkit 13 не обнаружен, используется CPU.");
            }
        }
        // LibraryPath загрузчик трактует как путь к файлу и берёт Path.GetDirectoryName,
        // поэтому добавляем имя файла: корнем поиска станет сам каталог runtime.
        RuntimeOptions.LibraryPath = libraryPath is null ? "" : Path.Combine(libraryPath, "whisper.dll");
        RuntimeOptions.RuntimeLibraryOrder = libraryPath is not null
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];
        var recognizer = new SpeechRecognizer(modelPath, status);
        try
        {
            try
            {
                recognizer.Load(libraryPath is not null);
            }
            catch (Exception error) when (libraryPath is not null && !token.IsCancellationRequested)
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
        _usingGpu = useGpu && RuntimeOptions.LoadedLibrary == RuntimeLibrary.Cuda;
        _preferGpu = _usingGpu;
        _lastUsed = DateTimeOffset.UtcNow;
        AppLog.Write("Распознавание: " + (_usingGpu ? "CUDA (whisper.cpp)" : "CPU (whisper.cpp)"));
    }

    public async Task<string> RecognizeAsync(ReadOnlyMemory<float> audio, CancellationToken token)
    {
        if (_factory is null)
        {
            _status("Загрузка модели...");
            try
            {
                Load(_preferGpu);
            }
            catch (Exception error) when (_preferGpu && !token.IsCancellationRequested)
            {
                AppLog.Write("CUDA недоступна, переход на CPU: " + error.Message);
                await ReleaseModelAsync();
                Load(false);
            }
        }
        _lastUsed = DateTimeOffset.UtcNow;
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

    public void UnloadIfIdle(TimeSpan idle)
    {
        if (_factory is null || _processor is not null || DateTimeOffset.UtcNow - _lastUsed < idle)
            return;
        _factory.Dispose();
        _factory = null;
        _usingGpu = false;
        AppLog.Write("Модель выгружена после простоя.");
    }

    // Пользователь говорит преимущественно по-русски, поэтому английский выбираем только при
    // уверенной детекции; иначе русский. Убирает ложное «русский -> английский» на шумных клипах.
    internal const float EnglishConfidence = 0.75f;

    internal static string SelectLanguage(string? detected, float probability) =>
        detected == "en" && probability >= EnglishConfidence ? "en" : "ru";

    private WhisperProcessor CreateProcessor(string language) => _factory!.CreateBuilder()
        .WithLanguage(language)
        // Beam search вместо greedy: заметно точнее на шумном аудио. Потоки подняты — пользователь
        // разрешил грузить CPU во время распознавания; в фоне обработка не идёт, потребление ~0.
        .WithBeamSearchSamplingStrategy()
        .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 2, 12))
        .WithTemperature(0.2f)
        .WithTemperatureInc(0.2f)
        .WithEntropyThreshold(2.4f)
        .WithNoSpeechThreshold(0.6f)
        .Build();

    private async Task<string> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Язык выбирается только из русского и английского: детекция ограничена этими кандидатами.
        string language;
        var detector = CreateProcessor("auto");
        try
        {
            var (detected, probability) = detector.DetectLanguageWithProbability(audio.Span, "ru", "en");
            language = SelectLanguage(detected, probability);
        }
        finally
        {
            await detector.DisposeAsync();
        }
        token.ThrowIfCancellationRequested();
        var processor = CreateProcessor(language);
        _processor = processor;
        try
        {
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
