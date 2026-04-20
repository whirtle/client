// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Protocol;

/// <summary>
/// Implements the Sendspin multi-server priority rules.
///
/// When more than one server tries to connect simultaneously, the spec defines
/// which server the client should prefer:
/// <list type="number">
///   <item><c>playback</c> always beats <c>discovery</c>.</item>
///   <item>
///     When both are <c>discovery</c> the client prefers the server that most
///     recently performed playback (<see cref="LastPlayedServerId"/>).
///   </item>
///   <item>
///     When both are <c>discovery</c> and neither is the last-played server, the
///     current connection is kept.
///   </item>
/// </list>
///
/// When a new connection wins, the displaced server must receive a
/// <see cref="ClientGoodbyeMessage"/> with reason <c>"another_server"</c> before
/// its WebSocket is closed.
/// </summary>
public sealed class ConnectionManager
{
    private string? _currentServerId;
    private string? _currentConnectionReason;

    /// <summary>
    /// The server ID recorded by the most recent call to <see cref="Accept"/>.
    /// <see langword="null"/> when <see cref="Clear"/> has been called or no
    /// connection has been accepted yet.
    /// </summary>
    public string? CurrentServerId => _currentServerId;

    /// <summary>
    /// The server ID of the most recent server that engaged this client for
    /// playback. Persist this value across restarts for the best user experience.
    /// </summary>
    public string? LastPlayedServerId { get; set; }

    /// <summary>
    /// Returns <see langword="true"/> if an incoming connection from the given
    /// server should replace the current active connection.
    /// </summary>
    /// <param name="newServerId">
    /// <see cref="ServerHelloMessage.ServerId"/> of the incoming connection.
    /// </param>
    /// <param name="newConnectionReason">
    /// <see cref="ServerHelloMessage.ConnectionReason"/> of the incoming
    /// connection: <c>"playback"</c> or <c>"discovery"</c>.
    /// </param>
    public bool ShouldAccept(string? newServerId, string? newConnectionReason)
    {
        // No active connection — always accept.
        if (_currentServerId is null)
            return true;

        // Same server reconnecting — always accept.
        if (newServerId is not null && newServerId == _currentServerId)
            return true;

        // 'playback' beats any existing connection.
        if (newConnectionReason == "playback")
            return true;

        // Incoming is 'discovery' but current is 'playback' — keep current.
        if (_currentConnectionReason == "playback")
            return false;

        // Both are 'discovery': prefer the last-played server.
        return newServerId is not null && newServerId == LastPlayedServerId;
    }

    /// <summary>
    /// Records that the given server is now the active connection.
    /// Call this after accepting a new connection.
    /// </summary>
    public void Accept(string? serverId, string? connectionReason)
    {
        _currentServerId         = serverId;
        _currentConnectionReason = connectionReason;
    }

    /// <summary>
    /// Clears the active connection record (e.g. after a disconnect).
    /// </summary>
    public void Clear()
    {
        _currentServerId         = null;
        _currentConnectionReason = null;
    }
}
