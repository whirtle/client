// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Runtime.Versioning;
using NAudio.Wave;

namespace Whirtle.Client.Playback;

/// <summary>
/// Production WASAPI renderer using NAudio's <see cref="WasapiOut"/>.
/// Buffers outbound samples in a <see cref="BufferedWaveProvider"/> that
/// is fed by <see cref="Write"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiRenderer : IWasapiRenderer
{
    private readonly WasapiOut            _wasapiOut;
    private readonly BufferedWaveProvider _provider;
    private readonly byte[]               _scratch;
    private volatile bool                 _muted;
    private volatile float                _volume = 1.0f;

    public int  SampleRate          { get; }
    public int  Channels            { get; }
    public int  LatencyMs           { get; }
    public int  BufferCapacityBytes => _provider.BufferLength;
    public int  BufferedBytes       => _provider.BufferedBytes;
    public bool IsRunning           { get; private set; }

    /// <param name="deviceId">
    /// WASAPI device ID from <c>WindowsAudioDeviceEnumerator</c>.
    /// Pass <c>null</c> to use the system default output device.
    /// </param>
    /// <param name="latencyMs">Desired output latency in milliseconds (default 100 ms).</param>
    public WasapiRenderer(
        string? deviceId  = null,
        int     sampleRate = 48_000,
        int     channels   = 2,
        int     latencyMs  = 100)
    {
        SampleRate = sampleRate;
        Channels   = channels;
        LatencyMs  = latencyMs;

        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _provider = new BufferedWaveProvider(waveFormat) { DiscardOnBufferOverflow = true };

        var device = deviceId is not null
            ? GetDeviceById(deviceId)
            : null;  // null = default

        _wasapiOut = device is not null
            ? new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, latencyMs)
            : new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, latencyMs);

        _wasapiOut.Init(_provider);

        // Scratch buffer for float conversion (2 × max Opus frame × channels × 4 bytes/float)
        _scratch = new byte[5760 * channels * sizeof(float)];
    }

    public void Start()
    {
        _wasapiOut.Play();
        IsRunning = true;
    }

    public void Stop()
    {
        _wasapiOut.Stop();
        IsRunning = false;
    }

    public void ClearBuffer() => _provider.ClearBuffer();

    public void SetMuted(bool muted) => _muted = muted;

    public void SetVolume(float volume) => _volume = Math.Clamp(volume, 0f, 1f);

    public void Write(ReadOnlySpan<short> samples)
    {
        if (_muted || samples.IsEmpty)
            return;

        // Convert int16 PCM → float32 PCM in-place via scratch buffer, applying volume scaling.
        float vol       = _volume;
        int   floatCount = samples.Length;
        Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
            _scratch.AsSpan(0, floatCount * sizeof(float)));

        for (int i = 0; i < floatCount; i++)
            floats[i] = samples[i] / 32768f * vol;

        _provider.AddSamples(_scratch, 0, floatCount * sizeof(float));
    }

    public void Dispose()
    {
        _wasapiOut.Stop();
        _wasapiOut.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private static NAudio.CoreAudioApi.MMDevice? GetDeviceById(string id)
    {
        using var e = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        try { return e.GetDevice(id); }
        catch { return null; }
    }
}
