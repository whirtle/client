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
    private int _serverVolumeMax = 100;

    public ControllerClient(ProtocolClient protocol) => _protocol = protocol;

    /// <summary>
    /// Maximum volume on the server's scale, sourced from
    /// <c>supported_commands["volume"]</c> in the most recent <c>server/state</c>.
    /// Outgoing volume commands are proportioned to this range.
    /// </summary>
    public int ServerVolumeMax
    {
        get => _serverVolumeMax;
        set => _serverVolumeMax = Math.Max(1, value);
    }

    /// <summary>Sends a <c>play</c> command.</summary>
    public Task PlayAsync(CancellationToken cancellationToken = default)
        => Send("play", cancellationToken);

    /// <summary>Sends a <c>pause</c> command.</summary>
    public Task PauseAsync(CancellationToken cancellationToken = default)
        => Send("pause", cancellationToken);

    /// <summary>Sends a <c>stop</c> command (stops playback and resets position).</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
        => Send("stop", cancellationToken);

    /// <summary>Sends a <c>next</c> command (skip forward).</summary>
    public Task NextAsync(CancellationToken cancellationToken = default)
        => Send("next", cancellationToken);

    /// <summary>Sends a <c>previous</c> command (skip back / restart current).</summary>
    public Task PreviousAsync(CancellationToken cancellationToken = default)
        => Send("previous", cancellationToken);

    /// <summary>
    /// Sends a <c>volume</c> command.
    /// </summary>
    /// <param name="volume">
    /// Normalised 0.0–1.0; clamped and proportioned to <see cref="ServerVolumeMax"/>.
    /// </param>
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        var vol = (int)Math.Round(Math.Clamp(volume, 0.0, 1.0) * _serverVolumeMax);
        return _protocol.SendAsync(
            new ClientCommandMessage(new ClientControllerCommand("volume", Volume: vol)),
            cancellationToken);
    }

    /// <summary>Sends a <c>mute</c> command.</summary>
    /// <param name="muted"><see langword="true"/> to mute; <see langword="false"/> to unmute.</param>
    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken = default)
        => _protocol.SendAsync(
            new ClientCommandMessage(new ClientControllerCommand("mute", Mute: muted)),
            cancellationToken);

    /// <summary>Sends a <c>repeat_off</c> command.</summary>
    public Task RepeatOffAsync(CancellationToken cancellationToken = default)
        => Send("repeat_off", cancellationToken);

    /// <summary>Sends a <c>repeat_one</c> command (repeat current track continuously).</summary>
    public Task RepeatOneAsync(CancellationToken cancellationToken = default)
        => Send("repeat_one", cancellationToken);

    /// <summary>Sends a <c>repeat_all</c> command (repeat all tracks continuously).</summary>
    public Task RepeatAllAsync(CancellationToken cancellationToken = default)
        => Send("repeat_all", cancellationToken);

    /// <summary>Sends a <c>shuffle</c> command (randomise playback order).</summary>
    public Task ShuffleAsync(CancellationToken cancellationToken = default)
        => Send("shuffle", cancellationToken);

    /// <summary>Sends an <c>unshuffle</c> command (restore original playback order).</summary>
    public Task UnshuffleAsync(CancellationToken cancellationToken = default)
        => Send("unshuffle", cancellationToken);

    /// <summary>
    /// Sends a <c>switch</c> command (move this client to the next group in the cycle).
    /// </summary>
    public Task SwitchAsync(CancellationToken cancellationToken = default)
        => Send("switch", cancellationToken);

    private Task Send(string command, CancellationToken ct)
        => _protocol.SendAsync(
            new ClientCommandMessage(new ClientControllerCommand(command)),
            ct);
}
