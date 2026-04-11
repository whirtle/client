namespace Whirtle.Client.Codec;

/// <summary>
/// A decoded audio frame as interleaved 16-bit PCM samples.
/// For stereo audio, samples alternate L/R: [L0, R0, L1, R1, …].
/// </summary>
/// <param name="Samples">Interleaved 16-bit PCM samples.</param>
/// <param name="SampleRate">Samples per second per channel (e.g. 48000).</param>
/// <param name="Channels">Number of channels (1 = mono, 2 = stereo).</param>
public sealed record AudioFrame(short[] Samples, int SampleRate, int Channels)
{
    /// <summary>Number of samples per channel.</summary>
    public int SamplesPerChannel => Samples.Length / Channels;

    /// <summary>Frame duration derived from sample count and rate.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SamplesPerChannel / SampleRate);
}
