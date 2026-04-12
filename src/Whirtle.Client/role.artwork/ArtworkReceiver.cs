// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Protocol;

namespace Whirtle.Client.Role;

/// <summary>
/// Stores the latest artwork received from the server for a single channel
/// (Artwork Role).
///
/// Feed <see cref="ArtworkFrame"/> instances from
/// <see cref="ProtocolClient.ReceiveAllAsync"/> into <see cref="ProcessFrame"/>.
/// Instantiate one receiver per channel.
/// </summary>
public sealed class ArtworkReceiver
{
    // Guards _data, _mimeType, _timestamp, and Changed so that a reader on one
    // thread never sees a partially updated state, and so that event subscription
    // races are safely observed.
    private readonly object _lock = new();

    private byte[]? _data;
    private string  _mimeType  = string.Empty;
    private long    _timestamp;

    /// <summary>
    /// The most recent raw image bytes, or <see langword="null"/> if none have been
    /// received yet or the channel was cleared.
    /// </summary>
    public byte[]? Data      { get { lock (_lock) return _data; } }

    /// <summary>
    /// MIME type of the current artwork: <c>image/jpeg</c>, <c>image/png</c>, or
    /// <c>application/octet-stream</c>. Empty string when no artwork is present.
    /// </summary>
    public string  MimeType  { get { lock (_lock) return _mimeType; } }

    /// <summary>
    /// Server clock time in microseconds when the current image should be displayed.
    /// </summary>
    public long    Timestamp { get { lock (_lock) return _timestamp; } }

    /// <summary>Raised whenever artwork changes or is cleared.</summary>
    public event Action? Changed;

    /// <summary>
    /// Applies <paramref name="frame"/> and fires <see cref="Changed"/>.
    /// An empty <see cref="ArtworkFrame.Data"/> array is treated as a clear,
    /// setting <see cref="Data"/> to <see langword="null"/>.
    /// </summary>
    public void ProcessFrame(ArtworkFrame frame)
    {
        // Update state and capture the current subscriber list under the lock so
        // callers always see Data, MimeType, and Timestamp in a consistent group.
        // Invoke the handler outside the lock to prevent deadlocks if a subscriber
        // calls back into ProcessFrame or reads the public properties.
        Action? handler;
        lock (_lock)
        {
            _timestamp = frame.Timestamp;
            if (frame.Data.Length > 0)
            {
                _data     = frame.Data;
                _mimeType = frame.MimeType;
            }
            else
            {
                _data     = null;
                _mimeType = string.Empty;
            }
            handler = Changed;
        }
        handler?.Invoke();
    }
}
