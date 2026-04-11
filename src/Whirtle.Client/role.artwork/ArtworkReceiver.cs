// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Protocol;

namespace Whirtle.Client.Role;

/// <summary>
/// Stores the latest album artwork received from the server as binary WebSocket
/// frames (Artwork Role).
///
/// Feed <see cref="ArtworkFrame"/> instances from
/// <see cref="ProtocolClient.ReceiveAllAsync"/> into <see cref="ProcessFrame"/>.
/// </summary>
public sealed class ArtworkReceiver
{
    // Guards _data, _mimeType, and Changed so that a reader on one thread
    // never sees the old MimeType paired with the new Data (or vice-versa),
    // and so that event subscription races are safely observed.
    private readonly object _lock = new();

    private byte[]? _data;
    private string  _mimeType = string.Empty;

    /// <summary>The most recent raw image bytes, or <see langword="null"/> if none have been received yet.</summary>
    public byte[]? Data     { get { lock (_lock) return _data; } }

    /// <summary>
    /// MIME type of the current artwork: <c>image/jpeg</c>, <c>image/png</c>, or
    /// <c>application/octet-stream</c>. Empty string when no artwork has been received.
    /// </summary>
    public string  MimeType { get { lock (_lock) return _mimeType; } }

    /// <summary>Raised whenever new artwork arrives.</summary>
    public event Action? Changed;

    /// <summary>
    /// Stores the artwork from <paramref name="frame"/> and fires <see cref="Changed"/>.
    /// </summary>
    public void ProcessFrame(ArtworkFrame frame)
    {
        // Update state and capture the current subscriber list under the lock so
        // callers always see Data and MimeType in a consistent pair.  Invoke the
        // handler outside the lock to prevent deadlocks if a subscriber itself
        // calls back into ProcessFrame or reads Data/MimeType.
        Action? handler;
        lock (_lock)
        {
            _data     = frame.Data;
            _mimeType = frame.MimeType;
            handler   = Changed;
        }
        handler?.Invoke();
    }
}
