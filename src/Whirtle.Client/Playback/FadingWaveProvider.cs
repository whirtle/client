// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Whirtle.Client.Playback;

/// <summary>
/// Wraps a <see cref="BufferedWaveProvider"/> and applies a short linear gain
/// envelope on clear and on the first write after a clear. Prevents the audible
/// click that a mid-waveform buffer clear produces at the DAC.
///
/// Format: IEEE float32, interleaved, any channel count / sample rate.
///
/// State machine
/// ─────────────
///  Normal      → FadingOut  : <see cref="FadeOutAndClear"/> called
///  FadingOut   → Silent     : envelope reaches zero in <see cref="Read"/>;
///                              inner buffer is cleared, subsequent Reads return zeros
///  Silent      → FadingIn   : first <see cref="AddSamples"/> after the clear
///  FadingIn    → Normal     : envelope reaches unity in <see cref="Read"/>
/// </summary>
internal sealed class FadingWaveProvider : IWaveProvider
{
    private enum FadeState { Normal, FadingOut, Silent, FadingIn }

    private readonly BufferedWaveProvider _inner;
    private readonly int                  _fadeFrames;   // samples-per-channel across a full fade
    private readonly int                  _channels;
    private readonly object               _lock = new();

    private FadeState _state;
    private int       _fadeFramesRemaining;

    public WaveFormat WaveFormat    => _inner.WaveFormat;
    public int        BufferLength  => _inner.BufferLength;
    public int        BufferedBytes => _inner.BufferedBytes;
    public bool       IsSilent      { get { lock (_lock) { return _state == FadeState.Silent; } } }

    public FadingWaveProvider(WaveFormat format, int fadeMs = 5)
    {
        if (format.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("FadingWaveProvider requires IEEE float format.", nameof(format));

        _inner       = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
        _channels    = format.Channels;
        _fadeFrames  = Math.Max(1, format.SampleRate * fadeMs / 1000);
    }

    public void AddSamples(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_state == FadeState.Silent)
            {
                _state               = FadeState.FadingIn;
                _fadeFramesRemaining = _fadeFrames;
            }
            _inner.AddSamples(buffer, offset, count);
        }
    }

    /// <summary>
    /// Ramps the currently-queued audio to zero over the fade duration, then
    /// clears the inner buffer. Returns immediately; the fade happens on the
    /// consuming thread inside <see cref="Read"/>.
    /// </summary>
    public void FadeOutAndClear()
    {
        lock (_lock)
        {
            if (_state == FadeState.Silent)
                return;
            _state               = FadeState.FadingOut;
            _fadeFramesRemaining = _fadeFrames;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_state == FadeState.Silent)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            int read = _inner.Read(buffer, offset, count);
            if (read < count)
                Array.Clear(buffer, offset + read, count - read);

            if (_state == FadeState.Normal)
                return count;

            Span<float> floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            int frameCount = floats.Length / _channels;

            if (_state == FadeState.FadingOut)
            {
                for (int f = 0; f < frameCount; f++)
                {
                    if (_fadeFramesRemaining <= 0)
                    {
                        floats[(f * _channels)..].Clear();
                        _inner.ClearBuffer();
                        _state = FadeState.Silent;
                        return count;
                    }
                    float gain = (float)_fadeFramesRemaining / _fadeFrames;
                    int   baseIdx = f * _channels;
                    for (int c = 0; c < _channels; c++)
                        floats[baseIdx + c] *= gain;
                    _fadeFramesRemaining--;
                }
            }
            else // FadingIn
            {
                for (int f = 0; f < frameCount; f++)
                {
                    if (_fadeFramesRemaining <= 0)
                    {
                        _state = FadeState.Normal;
                        return count;
                    }
                    float gain = 1f - (float)_fadeFramesRemaining / _fadeFrames;
                    int   baseIdx = f * _channels;
                    for (int c = 0; c < _channels; c++)
                        floats[baseIdx + c] *= gain;
                    _fadeFramesRemaining--;
                }
            }

            return count;
        }
    }
}
