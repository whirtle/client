using Whirtle.Client.Playback;

namespace Whirtle.Client.Tests.Playback;

internal sealed class FakeWasapiRenderer : IWasapiRenderer
{
    public int  SampleRate          => 48_000;
    public int  Channels            => 2;
    public int  LatencyMs           => 100;
    public int  BufferCapacityBytes  => SampleRate * Channels * sizeof(float);
    /// <summary>
    /// Simulated buffered-byte level. Defaults to 0 so the pacing loop never blocks.
    /// Set to a non-zero value in tests that exercise ahead-buffer adaptation.
    /// </summary>
    public int  BufferedBytesValue  { get; set; }
    public int  BufferedBytes       => BufferedBytesValue;
    public bool IsRunning           { get; private set; }
    public bool  Muted  { get; private set; }
    public float Volume { get; private set; } = 1.0f;

    public List<short[]> Written { get; } = [];

#pragma warning disable CS0067 // event never fired in tests
    public event EventHandler? RendererFailed;
#pragma warning restore CS0067

    public void Start()  => IsRunning = true;
    public void Stop()   => IsRunning = false;
    public void ClearBuffer() => Written.Clear();
    public void SetMuted(bool muted)     => Muted  = muted;
    public void SetVolume(float volume)  => Volume = volume;

    public void Write(ReadOnlySpan<short> samples)
    {
        if (!Muted)
            Written.Add(samples.ToArray());
    }

    public void Dispose() { }
}
