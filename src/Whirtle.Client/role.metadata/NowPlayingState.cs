// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Protocol;

namespace Whirtle.Client.Role;

/// <summary>
/// Tracks the most recently received now-playing metadata (Metadata Role).
///
/// Call <see cref="Update"/> each time a <see cref="ServerStateMessage"/> that
/// contains a non-null <see cref="ServerStateMessage.Metadata"/> arrives from
/// the server. Raises <see cref="Changed"/> after every update.
///
/// Use <see cref="CalculatePositionMs"/> to compute the current playback
/// position at any point in time from the last received progress snapshot.
/// </summary>
public sealed class NowPlayingState
{
    /// <summary>Server clock time in microseconds for when this metadata is valid.</summary>
    public long    Timestamp   { get; private set; }

    public string? Title       { get; private set; }
    public string? Artist      { get; private set; }
    public string? AlbumArtist { get; private set; }
    public string? Album       { get; private set; }
    public Uri?    ArtworkUrl  { get; private set; }
    public int?    Year        { get; private set; }
    public int?    Track       { get; private set; }

    /// <summary>
    /// Playback progress snapshot, or <see langword="null"/> if none has been received.
    /// Use <see cref="CalculatePositionMs"/> to get the live position.
    /// </summary>
    public PlaybackProgress? Progress { get; private set; }

    /// <summary>
    /// Repeat mode: <c>"off"</c>, <c>"one"</c>, <c>"all"</c>, or
    /// <see langword="null"/> if not reported.
    /// </summary>
    public string? Repeat  { get; private set; }

    /// <summary>Shuffle enabled, or <see langword="null"/> if not reported.</summary>
    public bool?   Shuffle { get; private set; }

    /// <summary>Raised whenever now-playing metadata changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Applies a <see cref="ServerMetadataState"/> snapshot, updating all
    /// properties and firing <see cref="Changed"/>.
    /// </summary>
    public void Update(ServerMetadataState state)
    {
        Timestamp   = state.Timestamp ?? Timestamp;
        if (state.Title.IsSet)       Title       = state.Title.Value;
        if (state.Artist.IsSet)      Artist      = state.Artist.Value;
        if (state.AlbumArtist.IsSet) AlbumArtist = state.AlbumArtist.Value;
        if (state.Album.IsSet)       Album       = state.Album.Value;
        if (state.ArtworkUrl.IsSet)  ArtworkUrl  = state.ArtworkUrl.Value;
        if (state.Year.IsSet)        Year        = state.Year.Value;
        if (state.Track.IsSet)       Track       = state.Track.Value;
        Progress    = state.Progress ?? Progress;
        if (state.Repeat.IsSet)      Repeat      = state.Repeat.Value;
        if (state.Shuffle.IsSet)     Shuffle     = state.Shuffle.Value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Calculates the current playback position in milliseconds using the
    /// last received <see cref="Progress"/> snapshot and
    /// <paramref name="currentTimestampUs"/> (server clock, microseconds).
    /// Returns 0 when no progress snapshot is available.
    /// </summary>
    /// <remarks>
    /// Formula from the Sendspin spec:
    /// <code>
    /// calculated = track_progress + (current_time − timestamp) × playback_speed ÷ 1 000 000
    /// </code>
    /// The result is clamped to [0, track_duration] when track_duration is non-zero.
    /// </remarks>
    public long CalculatePositionMs(long currentTimestampUs)
    {
        if (Progress is null) return 0;

        long calculated = Progress.TrackProgress
            + (currentTimestampUs - Timestamp) * (long)Progress.PlaybackSpeed / 1_000_000L;

        return Progress.TrackDuration != 0
            ? Math.Max(Math.Min(calculated, Progress.TrackDuration), 0L)
            : Math.Max(calculated, 0L);
    }

    /// <summary>Returns a human-readable summary of the current now-playing state.</summary>
    public override string ToString()
    {
        var parts = new List<string>(3);
        if (Title  is not null) parts.Add(Title);
        if (Artist is not null) parts.Add(Artist);
        if (Album  is not null) parts.Add($"[{Album}]");
        return parts.Count > 0 ? string.Join(" \u2014 ", parts) : "(no metadata)";
    }
}
