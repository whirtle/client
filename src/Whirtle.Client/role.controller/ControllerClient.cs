// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Protocol;

namespace Whirtle.Client.Role;

/// <summary>
/// Sends remote-control commands on behalf of the UI (Controller Role).
///
/// Per the Sendspin spec, commands are sent as <c>client/command</c> messages
/// containing a <c>controller</c> payload.
/// </summary>
public sealed class ControllerClient
{
    private readonly ProtocolClient _protocol;

    public ControllerClient(ProtocolClient protocol) => _protocol = protocol;

    /// <summary>Sends a <c>play</c> command.</summary>
    public Task PlayAsync(CancellationToken cancellationToken = default)
        => Send("play", cancellationToken);

    /// <summary>Sends a <c>pause</c> command.</summary>
    public Task PauseAsync(CancellationToken cancellationToken = default)
        => Send("pause", cancellationToken);

    /// <summary>Sends a <c>next</c> (skip) command.</summary>
    public Task SkipAsync(CancellationToken cancellationToken = default)
        => Send("next", cancellationToken);

    /// <summary>
    /// Sends a <c>volume</c> command.
    /// </summary>
    /// <param name="volume">Normalised 0.0–1.0; clamped and converted to 0–100.</param>
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        var vol100 = (int)Math.Round(Math.Clamp(volume, 0.0, 1.0) * 100);
        return _protocol.SendAsync(
            new ClientCommandMessage(new ClientControllerCommand("volume", Volume: vol100)),
            cancellationToken);
    }

    private Task Send(string command, CancellationToken ct)
        => _protocol.SendAsync(
            new ClientCommandMessage(new ClientControllerCommand(command)),
            ct);
}
