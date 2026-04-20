// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Codec;

namespace Whirtle.Client.Playback;

/// <summary>Abstraction over a WASAPI audio output device, for testability.</summary>
internal interface IWasapiRenderer : IDisposable
{
    int  SampleRate         { get; }
    int  Channels           { get; }
    /// <summary>
    /// Hardware/driver output latency in milliseconds.
    /// Audio submitted to <see cref="Write"/> will reach the DAC this many
    /// milliseconds later. Used to advance the playback timestamp so frames
    /// are queued at the right moment.
    /// </summary>
    int  LatencyMs          { get; }
    /// <summary>
    /// Total byte capacity of the audio output buffer.
    /// Report this value as <c>buffer_capacity</c> in <c>client/hello</c> so
    /// the server knows the maximum chunk size the client can receive.
    /// </summary>
    int  BufferCapacityBytes { get; }
    /// <summary>
    /// Number of bytes currently queued in the output buffer waiting to be played.
    /// Used by the render loop to pace writes without relying on imprecise timers.
    /// </summary>
    int  BufferedBytes       { get; }
    bool IsRunning          { get; }

    /// <summary>
    /// Raised when the underlying audio device stops unexpectedly — for example
    /// when the selected endpoint is unplugged, disabled, or reset by Windows.
    /// Stopping the renderer via <see cref="Stop"/> does <b>not</b> fire this event.
    /// </summary>
    event EventHandler? RendererFailed;

    void Start();
    void Stop();

    /// <summary>Writes interleaved 16-bit PCM samples to the hardware output buffer.</summary>
    void Write(ReadOnlySpan<short> samples);

    /// <summary>Discards all samples currently queued in the output buffer.</summary>
    void ClearBuffer();

    /// <summary>Mutes or unmutes the output without stopping the stream.</summary>
    void SetMuted(bool muted);

    /// <summary>Sets the output volume. <paramref name="volume"/> is a linear scalar in [0.0, 1.0].</summary>
    void SetVolume(float volume);
}
