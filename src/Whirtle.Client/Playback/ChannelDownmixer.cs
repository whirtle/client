// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Playback;

/// <summary>
/// Downmixes interleaved 16-bit PCM from an arbitrary channel count to stereo.
///
/// Channel-order assumptions follow the standard Sendspin / WAV convention:
///   1 ch  : mono
///   2 ch  : L, R
///   3 ch  : L, R, C
///   4 ch  : L, R, Ls, Rs
///   5 ch  : L, C, R, Ls, Rs
///   6 ch  : L, C, R, Ls, Rs, LFE
///   7 ch  : L, C, R, Ls, Rs, LFE, Cs        (6.1)
///   8 ch  : L, C, R, Ls, Rs, LFE, Lb, Rb    (7.1)
///
/// Mix coefficients (-3 dB for center/surround) follow ITU-R BS.775.
/// If the source is already stereo the input buffer is returned as-is.
/// </summary>
internal static class ChannelDownmixer
{
    // -3 dB in linear amplitude
    private const double Center3dB   = 0.7071;
    // -3 dB for surround channels
    private const double Surround3dB = 0.7071;

    /// <summary>
    /// Downmixes <paramref name="input"/> to stereo.
    /// Returns the original span unchanged when <paramref name="sourceChannels"/> is 2,
    /// or a new stereo array otherwise.
    /// </summary>
    public static short[] Downmix(ReadOnlySpan<short> input, int sourceChannels)
    {
        if (sourceChannels == 2)
            return input.ToArray();

        if (sourceChannels == 1)
            return MonoToStereo(input);

        int frames  = input.Length / sourceChannels;
        var output  = new short[frames * 2];

        for (int f = 0; f < frames; f++)
        {
            int src = f * sourceChannels;
            int dst = f * 2;

            (double l, double r) = sourceChannels switch
            {
                3 => Mix3(input, src),   // L R C
                4 => Mix4(input, src),   // L R Ls Rs
                5 => Mix5(input, src),   // L C R Ls Rs
                6 => Mix6(input, src),   // L C R Ls Rs LFE
                7 => Mix7(input, src),   // L C R Ls Rs LFE Cs        (6.1)
                8 => Mix8(input, src),   // L C R Ls Rs LFE Lb Rb     (7.1)
                _ => MixGeneric(input, src, sourceChannels)
            };

            output[dst]     = Clamp(l);
            output[dst + 1] = Clamp(r);
        }

        return output;
    }

    // ── Per-layout mixers ─────────────────────────────────────────────────────

    private static (double l, double r) Mix3(ReadOnlySpan<short> s, int i)
    {
        double L = s[i], R = s[i + 1], C = s[i + 2];
        return (L + C * Center3dB, R + C * Center3dB);
    }

    private static (double l, double r) Mix4(ReadOnlySpan<short> s, int i)
    {
        double L = s[i], R = s[i + 1], Ls = s[i + 2], Rs = s[i + 3];
        return (L + Ls * Surround3dB, R + Rs * Surround3dB);
    }

    private static (double l, double r) Mix5(ReadOnlySpan<short> s, int i)
    {
        double L = s[i], C = s[i + 1], R = s[i + 2], Ls = s[i + 3], Rs = s[i + 4];
        return (L + C * Center3dB + Ls * Surround3dB,
                R + C * Center3dB + Rs * Surround3dB);
    }

    private static (double l, double r) Mix6(ReadOnlySpan<short> s, int i)
    {
        // LFE omitted (subwoofer channel; not reproduced on stereo output)
        double L = s[i], C = s[i + 1], R = s[i + 2], Ls = s[i + 3], Rs = s[i + 4];
        return (L + C * Center3dB + Ls * Surround3dB,
                R + C * Center3dB + Rs * Surround3dB);
    }

    private static (double l, double r) Mix7(ReadOnlySpan<short> s, int i)
    {
        // 6.1: L C R Ls Rs LFE Cs — LFE omitted; Cs (center surround) splits equally
        double L = s[i], C = s[i + 1], R = s[i + 2], Ls = s[i + 3], Rs = s[i + 4],
               Cs = s[i + 6];
        return (L + C * Center3dB + Ls * Surround3dB + Cs * Surround3dB * 0.5,
                R + C * Center3dB + Rs * Surround3dB + Cs * Surround3dB * 0.5);
    }

    private static (double l, double r) Mix8(ReadOnlySpan<short> s, int i)
    {
        // 7.1: L C R Ls Rs LFE Lb Rb — LFE omitted; back surrounds at -3 dB
        double L = s[i], C = s[i + 1], R = s[i + 2], Ls = s[i + 3], Rs = s[i + 4],
               Lb = s[i + 6], Rb = s[i + 7];
        return (L + C * Center3dB + Ls * Surround3dB + Lb * Surround3dB,
                R + C * Center3dB + Rs * Surround3dB + Rb * Surround3dB);
    }

    /// <summary>
    /// Generic fallback: pair even/odd channels into L/R and average.
    /// Used for channel counts > 8.
    /// </summary>
    private static (double l, double r) MixGeneric(ReadOnlySpan<short> s, int i, int channels)
    {
        double l = 0, r = 0;
        for (int ch = 0; ch < channels; ch++)
            if (ch % 2 == 0) l += s[i + ch]; else r += s[i + ch];

        int half = (channels + 1) / 2;
        return (l / half, r / half);
    }

    private static short[] MonoToStereo(ReadOnlySpan<short> input)
    {
        var output = new short[input.Length * 2];
        for (int i = 0; i < input.Length; i++)
        {
            output[i * 2]     = input[i];
            output[i * 2 + 1] = input[i];
        }
        return output;
    }

    private static short Clamp(double v) =>
        (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
}
