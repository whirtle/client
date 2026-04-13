// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Codec;

namespace Whirtle.Client.Playback;

/// <summary>Abstraction over a WASAPI audio output device, for testability.</summary>
internal interface IWasapiRenderer : IDisposable
{
    int  SampleRate { get; }
    int  Channels   { get; }
    /// <summary>
    /// Hardware/driver output latency in milliseconds.
    /// Audio submitted to <see cref="Write"/> will reach the DAC this many
    /// milliseconds later. Used to advance the playback timestamp so frames
    /// are queued at the right moment.
    /// </summary>
    int  LatencyMs  { get; }
    bool IsRunning  { get; }

    void Start();
    void Stop();

    /// <summary>Writes interleaved 16-bit PCM samples to the hardware output buffer.</summary>
    void Write(ReadOnlySpan<short> samples);

    /// <summary>Mutes or unmutes the output without stopping the stream.</summary>
    void SetMuted(bool muted);
}
