namespace Whirtle.Client.Playback;

/// <summary>
/// Compensates for clock drift by resampling a block of 16-bit PCM
/// via linear interpolation.
///
/// A <paramref name="rateRatio"/> of 1.0 is a passthrough.
/// Values above 1.0 produce more output samples (stretch / slow down).
/// Values below 1.0 produce fewer output samples (compress / speed up).
/// </summary>
internal static class SampleInterpolator
{
    /// <param name="input">Interleaved 16-bit PCM input samples.</param>
    /// <param name="channels">Number of interleaved channels.</param>
    /// <param name="rateRatio">Output length / input length ratio.</param>
    /// <returns>Resampled interleaved PCM samples.</returns>
    public static short[] Interpolate(ReadOnlySpan<short> input, int channels, double rateRatio)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        if (input.IsEmpty)
            return [];

        int inputFrames  = input.Length / channels;
        int outputFrames = Math.Max(1, (int)Math.Round(inputFrames * rateRatio));
        var output       = new short[outputFrames * channels];

        for (int outFrame = 0; outFrame < outputFrames; outFrame++)
        {
            double srcPos    = outFrame / rateRatio;
            int    srcLow    = (int)srcPos;
            int    srcHigh   = Math.Min(srcLow + 1, inputFrames - 1);
            double frac      = srcPos - srcLow;

            for (int ch = 0; ch < channels; ch++)
            {
                double lo = input[srcLow  * channels + ch];
                double hi = input[srcHigh * channels + ch];
                output[outFrame * channels + ch] = (short)Math.Round(lo + frac * (hi - lo));
            }
        }

        return output;
    }
}
