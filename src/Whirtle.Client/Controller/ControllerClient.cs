using Whirtle.Client.Protocol;

namespace Whirtle.Client.Controller;

/// <summary>
/// Sends remote-control commands on behalf of the UI (Controller Role).
///
/// Per the Sendspin spec the client sends <c>client/command</c> messages to
/// control playback for the whole group — play, pause, skip, and volume.
/// </summary>
public sealed class ControllerClient
{
    private readonly ProtocolClient _protocol;

    public ControllerClient(ProtocolClient protocol) => _protocol = protocol;

    /// <summary>Sends a <c>play</c> command.</summary>
    public Task PlayAsync(CancellationToken cancellationToken = default)
        => _protocol.SendAsync(new ClientCommandMessage("play"), cancellationToken);

    /// <summary>Sends a <c>pause</c> command.</summary>
    public Task PauseAsync(CancellationToken cancellationToken = default)
        => _protocol.SendAsync(new ClientCommandMessage("pause"), cancellationToken);

    /// <summary>Sends a <c>skip</c> command.</summary>
    public Task SkipAsync(CancellationToken cancellationToken = default)
        => _protocol.SendAsync(new ClientCommandMessage("skip"), cancellationToken);

    /// <summary>
    /// Sends a <c>volume</c> command.
    /// </summary>
    /// <param name="volume">Normalised volume — 0.0 (silent) to 1.0 (full). Clamped automatically.</param>
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
        => _protocol.SendAsync(
            new ClientCommandMessage("volume", Math.Clamp(volume, 0.0, 1.0)),
            cancellationToken);
}
