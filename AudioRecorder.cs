using System.Buffers.Binary;
using NAudio.Wave;

namespace VoiceSiteLauncher;

internal sealed class AudioRecorder
{
    private const int SampleRate = 16_000;
    private const int MaximumSamples = SampleRate * 120;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<IWaveIn> _createCapture;
    private float[]? _buffer;
    private bool _acceptData = true;
    private IWaveIn? _capture;
    private string? _problem;
    private int _samples;

    public AudioRecorder(Func<IWaveIn>? createCapture = null)
    {
        // Оконные callbacks идут через UI message loop: нет отдельного worker, теряющего ранний stop.
        _createCapture = createCapture ?? (() => new WaveIn(WaveCallbackInfo.NewWindow())
        {
            DeviceNumber = -1,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
            NumberOfBuffers = 3
        });
    }

    public void Start()
    {
        var capture = _createCapture();
        _capture = capture;
        _buffer = new float[MaximumSamples];
        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            lock (_gate)
                _acceptData = false;
            try
            {
                capture.Dispose();
            }
            finally
            {
                _capture = null;
                ClearBuffer();
            }
            throw;
        }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        lock (_gate)
        {
            try
            {
                if (!_acceptData || _problem is not null || _buffer is null)
                    return;
                var count = args.BytesRecorded / 2;
                if (_samples + count > MaximumSamples)
                {
                    _problem = "Запись длиннее 120 секунд. Повторите более короткую фразу.";
                    return;
                }
                // Один предаллоцированный буфер: пик ОЗУ ограничен 120 секундами без второй копии.
                var destination = _buffer.AsSpan(_samples, count);
                for (var i = 0; i < count; i++)
                    destination[i] = BinaryPrimitives.ReadInt16LittleEndian(args.Buffer.AsSpan(i * 2, 2)) / 32768f;
                _samples += count;
            }
            finally
            {
                Array.Clear(args.Buffer);
            }
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        lock (_gate)
        {
            if (args.Exception is not null)
                _problem = "Ошибка микрофона: " + args.Exception.Message;
        }
        _stopped.TrySetResult();
    }

    public async Task<(float[] Buffer, int Length)> StopAsync(bool discard = false)
    {
        var capture = _capture;
        if (capture is null)
            return ([], 0);
        try
        {
            capture.StopRecording();
            await _stopped.Task;
            lock (_gate)
            {
                if (discard)
                    return ([], 0);
                if (_problem is not null)
                    throw new InvalidOperationException(_problem);
                if (_samples < SampleRate / 4)
                    throw new InvalidOperationException("Запись слишком короткая. Удерживайте V + F6 и произнесите фразу.");
                // Владение буфером переходит вызывающему: он очищает его после распознавания.
                var buffer = _buffer!;
                var length = _samples;
                _buffer = null;
                return (buffer, length);
            }
        }
        finally
        {
            lock (_gate)
                _acceptData = false;
            try
            {
                capture.Dispose();
            }
            finally
            {
                _capture = null;
                ClearBuffer();
            }
        }
    }

    private void ClearBuffer()
    {
        lock (_gate)
        {
            if (_buffer is not null)
            {
                Array.Clear(_buffer);
                _buffer = null;
            }
            _samples = 0;
        }
    }
}
