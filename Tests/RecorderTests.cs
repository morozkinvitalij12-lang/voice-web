using System.Buffers.Binary;
using System.Reflection;
using NAudio.Wave;
using Xunit;

namespace VoiceSiteLauncher.Tests;

public sealed class RecorderTests
{
    [Fact]
    public async Task ConvertsPcmAndClearsAllManagedBuffers()
    {
        var fake = new FakeCapture();
        var recorder = new AudioRecorder(() => fake);
        recorder.Start();
        var bytes = Pcm(4000);
        fake.Emit(bytes);
        var chunks = Chunks(recorder).ToArray();
        Assert.All(bytes, value => Assert.Equal((byte)0, value));
        var audio = await recorder.StopAsync();
        Assert.Equal(4000, audio.Length);
        Assert.Equal(-1f, audio[0]);
        Assert.Equal(0.5f, audio[1]);
        Assert.All(chunks.SelectMany(x => x), value => Assert.Equal(0f, value));
        Assert.Empty(Chunks(recorder));
        Assert.True(fake.Disposed);
        Array.Clear(audio);
    }

    [Fact]
    public async Task StopBeforeFirstBufferCompletesAndReportsShortRecording()
    {
        var fake = new FakeCapture();
        var recorder = new AudioRecorder(() => fake);
        recorder.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task PostedStopIsAwaitedWithoutBlockingCaller()
    {
        var fake = new FakeCapture { DeferStopped = true };
        var recorder = new AudioRecorder(() => fake);
        recorder.Start();
        fake.Emit(Pcm(4000));
        var task = recorder.StopAsync();
        Assert.False(task.IsCompleted);
        Assert.False(fake.Disposed);
        fake.Finish();
        var audio = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fake.Disposed);
        Array.Clear(audio);
    }

    [Fact]
    public async Task DiscardClearsChunksAndReturnsNoAudio()
    {
        var fake = new FakeCapture();
        var recorder = new AudioRecorder(() => fake);
        recorder.Start();
        fake.Emit(Pcm(4000));
        var chunks = Chunks(recorder).ToArray();
        Assert.Empty(await recorder.StopAsync(discard: true));
        Assert.All(chunks.SelectMany(x => x), value => Assert.Equal(0f, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3999)]
    public async Task RejectsShortRecordings(int count)
    {
        var fake = new FakeCapture();
        var recorder = new AudioRecorder(() => fake);
        recorder.Start();
        fake.Emit(Pcm(count));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync());
        Assert.Empty(Chunks(recorder));
    }

    [Fact]
    public async Task EnforcesMaximumAndClearsOverflowBuffer()
    {
        var fake = new FakeCapture();
        var recorder = new AudioRecorder(() => fake);
        recorder.Start();
        fake.Emit(Pcm(16_000 * 120));
        var overflow = Pcm(1);
        fake.Emit(overflow);
        Assert.All(overflow, value => Assert.Equal((byte)0, value));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync());
        Assert.Contains("120", error.Message);
        Assert.Empty(Chunks(recorder));
    }

    [Fact]
    public async Task DeviceErrorDoesNotReturnAudio()
    {
        var fake = new FakeCapture { StopError = new IOException("disconnected") };
        var recorder = new AudioRecorder(() => fake);
        recorder.Start();
        fake.Emit(Pcm(4000));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync());
        Assert.Contains("disconnected", error.Message);
        Assert.Empty(Chunks(recorder));
        Assert.True(fake.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedStartCleansPartialCaptureEvenWhenDisposeFails(bool disposeFails)
    {
        var fake = new FakeCapture { StartError = new IOException("start"), DisposeError = disposeFails };
        var recorder = new AudioRecorder(() => fake);
        Assert.Throws<IOException>(() => recorder.Start());
        Assert.True(fake.Disposed);
        Assert.Empty(Chunks(recorder));
        var late = Pcm(4000);
        fake.Emit(late);
        Assert.Empty(Chunks(recorder));
        Assert.All(late, value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupSurvivesStopOrDisposeFailure(bool stopFails)
    {
        var fake = new FakeCapture { ThrowOnStop = stopFails, DisposeError = !stopFails };
        var recorder = new AudioRecorder(() => fake);
        recorder.Start();
        fake.Emit(Pcm(4000));
        var chunks = Chunks(recorder).ToArray();
        await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync(discard: true));
        Assert.All(chunks.SelectMany(x => x), value => Assert.Equal(0f, value));
        Assert.Empty(Chunks(recorder));
        fake.Emit(Pcm(4000));
        Assert.Empty(Chunks(recorder));
        Assert.Empty(await recorder.StopAsync());
    }

    [Fact]
    public async Task IdleStopDoesNotCreateCapture()
    {
        var recorder = new AudioRecorder(() => throw new InvalidOperationException("must not open"));
        Assert.Empty(await recorder.StopAsync());
    }

    private static List<float[]> Chunks(AudioRecorder recorder) => (List<float[]>)typeof(AudioRecorder)
        .GetField("_chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!;

    private static byte[] Pcm(int count)
    {
        var bytes = new byte[count * 2];
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), i % 2 == 0 ? short.MinValue : (short)16384);
        return bytes;
    }

    private sealed class FakeCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public Exception? StartError { get; init; }
        public Exception? StopError { get; init; }
        public bool ThrowOnStop { get; init; }
        public bool DisposeError { get; init; }
        public bool DeferStopped { get; init; }
        public bool Disposed { get; private set; }

        public void StartRecording()
        {
            if (StartError is not null)
                throw StartError;
        }

        public void StopRecording()
        {
            if (ThrowOnStop)
                throw new IOException("stop");
            if (!DeferStopped)
                Finish();
        }

        public void Finish() => RecordingStopped?.Invoke(this, new StoppedEventArgs(StopError));
        public void Emit(byte[] bytes) => DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));

        public void Dispose()
        {
            Disposed = true;
            Emit(Pcm(2));
            if (DisposeError)
                throw new IOException("dispose");
        }
    }
}
