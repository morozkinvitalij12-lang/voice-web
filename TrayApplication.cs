using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VoiceSiteLauncher;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly KeyboardInputWindow _ui;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _idleTimer;
    private readonly CancellationTokenSource _cancel = new();
    private readonly LaunchOptions _options;
    private readonly PushToTalk _keys;
    private Icon? _glyph;
    private SpeechRecognizer? _recognizer;
    private AudioRecorder? _recorder;
    private Task _operation = Task.CompletedTask;
    private bool _ready;
    private bool _busy;
    private bool _closing;

    public TrayApplication(LaunchOptions options)
    {
        _options = options;
        _keys = new PushToTalk(StartRecording, StopRecording);
        _ui = new KeyboardInputWindow((scanCode, down) =>
        {
            if (!_closing)
                _keys.Handle(scanCode, down);
        });
        _ = _ui.Handle;
        _idleTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _idleTimer.Tick += (_, _) =>
        {
            if (_ready && !_busy && !_closing)
                _recognizer?.UnloadIfIdle(TimeSpan.FromMinutes(5));
        };
        _idleTimer.Start();
        _menu.Items.Add("Выход", null, (_, _) => BeginShutdown());
        _tray = new NotifyIcon { Text = Program.AppName, ContextMenuStrip = _menu };
        SetStatus("Загрузка модели...", Color.Gray);
        _tray.Visible = true;
        _ui.BeginInvoke((Action)(() => _operation = InitializeAsync()));
    }

    private async Task InitializeAsync()
    {
        try
        {
            _recognizer = await Task.Run(() => SpeechRecognizer.CreateAsync(_options, PostStatus, _cancel.Token));
            if (_closing)
                return;
            _ready = true;
            SetStatus("Готово: удерживайте V + F6", Color.ForestGreen);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error)
        {
            AppLog.Write("Ошибка запуска: " + error);
            if (!_closing)
            {
                SetStatus("Ошибка запуска", Color.Firebrick);
                Notify("Не удалось запустить", error.Message);
            }
        }
    }

    private void StartRecording()
    {
        if (!_ready || _busy || _closing)
            return;
        _busy = true;
        var recorder = new AudioRecorder();
        try
        {
            recorder.Start();
            _recorder = recorder;
            SetStatus("Запись: отпустите обе клавиши", Color.Red);
        }
        catch (Exception error)
        {
            _busy = false;
            AppLog.Write("Ошибка микрофона: " + error);
            SetStatus("Готово: удерживайте V + F6", Color.ForestGreen);
            Notify("Ошибка микрофона", error.Message);
        }
    }

    private void StopRecording()
    {
        if (_recorder is null || _closing)
            return;
        var recorder = _recorder;
        _recorder = null;
        _operation = StopAndRecognizeAsync(recorder);
    }

    private async Task StopAndRecognizeAsync(AudioRecorder recorder)
    {
        float[]? buffer = null;
        try
        {
            SetStatus("Распознавание...", Color.Goldenrod);
            var (audio, length) = await recorder.StopAsync();
            buffer = audio;
            _cancel.Token.ThrowIfCancellationRequested();
            var memory = audio.AsMemory(0, length);
            var text = await Task.Run(() => _recognizer!.RecognizeAsync(memory, _cancel.Token));
            Array.Clear(buffer);
            buffer = null;
            if (_closing)
                return;
            if (text.Length == 0)
            {
                Notify("Речь не распознана", "Повторите фразу, удерживая V + F6.");
                return;
            }
            var url = SiteRouter.Resolve(text);
            AppLog.Write($"Текст: {text}; URL: {url}");
            using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (browser is null)
                AppLog.Write("URL передан оболочке Windows; новый PID браузера не предоставлен.");
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error)
        {
            AppLog.Write("Ошибка обработки: " + error);
            if (!_closing)
                Notify("Ошибка обработки", error.Message);
        }
        finally
        {
            if (buffer is not null)
                Array.Clear(buffer);
            _busy = false;
            if (!_closing)
                SetStatus("Готово: удерживайте V + F6", Color.ForestGreen);
        }
    }

    private void BeginShutdown()
    {
        if (_closing)
            return;
        _closing = true;
        _ready = false;
        _cancel.Cancel();
        _idleTimer.Stop();
        SetStatus("Завершение...", Color.Gray);
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            if (_recorder is not null)
            {
                var recorder = _recorder;
                _recorder = null;
                await recorder.StopAsync(discard: true);
            }
        }
        catch (Exception error)
        {
            AppLog.Write("Ошибка остановки микрофона: " + error);
        }
        try
        {
            await _operation;
            if (_recognizer is not null)
                await Task.Run(() => _recognizer.DisposeAsync().AsTask());
        }
        catch (Exception error)
        {
            AppLog.Write("Ошибка завершения: " + error);
        }
        finally
        {
            _recognizer = null;
            _tray.Visible = false;
            ExitThread();
        }
    }

    private void PostStatus(string text)
    {
        _ui.BeginInvoke((Action)(() =>
        {
            if (!_closing)
                SetStatus(text, _busy ? Color.Goldenrod : Color.Gray);
        }));
    }

    private void SetStatus(string text, Color color)
    {
        Icon? next = null;
        try
        {
            next = CreateMicrophone(color);
            _tray.Icon = next;
            var previous = _glyph;
            _glyph = next;
            next = null;
            previous?.Dispose();
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }
        catch (Exception error)
        {
            next?.Dispose();
            AppLog.Write("Ошибка обновления трея: " + error.Message);
        }
    }

    private void Notify(string title, string message)
    {
        try
        {
            _tray.ShowBalloonTip(7000, title, message, ToolTipIcon.Warning);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Icon CreateMicrophone(Color color)
    {
        using var bitmap = new Bitmap(64, 64);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        using (var pen = new Pen(color, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.FillEllipse(brush, 22, 6, 20, 18);
            graphics.FillRectangle(brush, 22, 15, 20, 20);
            graphics.FillEllipse(brush, 22, 25, 20, 18);
            graphics.DrawArc(pen, 15, 17, 34, 32, 0, 180);
            graphics.DrawLine(pen, 32, 49, 32, 57);
            graphics.DrawLine(pen, 23, 57, 41, 57);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _idleTimer.Dispose();
            _tray.Dispose();
            _glyph?.Dispose();
            _menu.Dispose();
            _cancel.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
