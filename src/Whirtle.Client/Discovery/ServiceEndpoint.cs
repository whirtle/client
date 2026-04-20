// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Discovery;

/// <summary>A Sendspin client endpoint discovered via mDNS.</summary>
/// <param name="Host">Hostname or IP address of the client.</param>
/// <param name="Port">TCP port the client is listening on (default 8928).</param>
/// <param name="Path">WebSocket path from the TXT <c>path</c> record (default <c>/sendspin</c>).</param>
/// <param name="Name">Friendly player name from the TXT <c>name</c> record, if present.</param>
public sealed record ServiceEndpoint(
    string  Host,
    int     Port = MdnsAdvertiser.DefaultPort,
    string  Path = MdnsAdvertiser.DefaultPath,
    string? Name = null)
{
    public Uri    ToWebSocketUri() => new($"ws://{Host}:{Port}{Path}");
    public string DisplayName     => Name ?? Host;
}
