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
/// </summary>
public sealed class NowPlayingState
{
    public string? Title           { get; private set; }
    public string? Artist          { get; private set; }
    public string? Album           { get; private set; }
    public double? ProgressSeconds { get; private set; }

    /// <summary>Raised whenever now-playing metadata changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Applies a <see cref="ServerMetadataState"/> snapshot, updating all
    /// properties and firing <see cref="Changed"/>.
    /// </summary>
    public void Update(ServerMetadataState state)
    {
        Title           = state.Title;
        Artist          = state.Artist;
        Album           = state.Album;
        ProgressSeconds = state.Progress;
        Changed?.Invoke();
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
