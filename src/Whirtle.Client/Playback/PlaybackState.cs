// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Playback;

public enum PlaybackState
{
    /// <summary>Buffering enough frames to begin or resume playback.</summary>
    Buffering,

    /// <summary>Playing audio in sync with the server clock.</summary>
    Synchronized,

    /// <summary>
    /// A buffer underrun or unrecoverable clock drift was detected.
    /// Audio output is muted; the engine is buffering until recovery.
    /// </summary>
    Error,
}
