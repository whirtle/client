using Whirtle.Client.Playback;

namespace Whirtle.Client.Tests.Playback;

internal sealed class FakeWasapiRenderer : IWasapiRenderer
{
    public int  SampleRate          => 48_000;
    public int  Channels            => 2;
    public int  LatencyMs           => 100;
    public int  BufferCapacityBytes => SampleRate * Channels * sizeof(float);
    public bool IsRunning           { get; private set; }
    public bool Muted      { get; private set; }

    public List<short[]> Written { get; } = [];

    public void Start()  => IsRunning = true;
    public void Stop()   => IsRunning = false;
    public void SetMuted(bool muted) => Muted = muted;

    public void Write(ReadOnlySpan<short> samples)
    {
        if (!Muted)
            Written.Add(samples.ToArray());
    }

    public void Dispose() { }
}
