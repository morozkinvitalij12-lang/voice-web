using System.Buffers.Binary;
using NAudio.Wave;

namespace VoiceSiteLauncher;

internal sealed class AudioRecorder
{
    private const int SampleRate = 16_000;
    private const int MaximumSamples = SampleRate * 120;
    private readonly object _gate = new();
    private readonly List<float[]> _chunks = [];
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _acceptData = true;
    private readonly Func<IWaveIn> _createCapture;
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
                ClearChunks();
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
                if (!_acceptData || _problem is not null)
                    return;
                var count = args.BytesRecorded / 2;
                if (_samples + count > MaximumSamples)
                {
                    _problem = "Запись длиннее 120 секунд. Повторите более короткую фразу.";
                    return;
                }
                var chunk = new float[count];
                for (var i = 0; i < count; i++)
                    chunk[i] = BinaryPrimitives.ReadInt16LittleEndian(args.Buffer.AsSpan(i * 2, 2)) / 32768f;
                _chunks.Add(chunk);
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

    public async Task<float[]> StopAsync(bool discard = false)
    {
        var capture = _capture;
        if (capture is null)
            return [];
        try
        {
            capture.StopRecording();
            await _stopped.Task;
            lock (_gate)
            {
                if (discard)
                    return [];
                if (_problem is not null)
                    throw new InvalidOperationException(_problem);
                if (_samples < SampleRate / 4)
                    throw new InvalidOperationException("Запись слишком короткая. Удерживайте V + F6 и произнесите фразу.");
                var result = new float[_samples];
                var offset = 0;
                foreach (var chunk in _chunks)
                {
                    chunk.CopyTo(result, offset);
                    offset += chunk.Length;
                }
                return result;
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
                ClearChunks();
            }
        }
    }

    private void ClearChunks()
    {
        lock (_gate)
        {
            foreach (var chunk in _chunks)
                Array.Clear(chunk);
            _chunks.Clear();
            _samples = 0;
        }
    }
}
