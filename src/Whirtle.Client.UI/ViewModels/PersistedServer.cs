// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.UI.ViewModels;

/// <summary>
/// A manually-added server saved in settings.
/// <see cref="Label"/> is what the user typed; <see cref="ServerName"/> is the name
/// the server reported in its hello message (cached across sessions).
/// <see cref="ServerId"/> is the stable server identity from the hello message, used
/// to match the entry even if the server's address changes.
/// </summary>
public sealed record PersistedServer(
    string  Label,
    string  Host,
    int     Port       = 8928,
    string  Path       = "/sendspin",
    string? ServerId   = null,
    string? ServerName = null)
{
    /// <summary>Best available display name: server-reported name, then user label.</summary>
    public string DisplayName => ServerName ?? Label;
}
