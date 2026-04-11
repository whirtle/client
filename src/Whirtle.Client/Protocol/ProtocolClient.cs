using System.Runtime.CompilerServices;
using Whirtle.Client.Transport;

namespace Whirtle.Client.Protocol;

public sealed class ProtocolClient : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly MessageSerializer _serializer = new();

    public ProtocolClient(ITransport transport)
    {
        _transport = transport;
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
        => _transport.ConnectAsync(uri, cancellationToken);

    /// <summary>
    /// Sends <see cref="HelloMessage"/> and waits for the server's <see cref="WelcomeMessage"/>.
    /// Throws <see cref="HandshakeException"/> if the server replies with an error or
    /// closes the connection before welcoming.
    /// </summary>
    public async Task<WelcomeMessage> HandshakeAsync(
        string clientVersion,
        CancellationToken cancellationToken = default)
    {
        await SendAsync(new HelloMessage(clientVersion), cancellationToken);

        await foreach (var msg in ReceiveRawAsync(cancellationToken))
        {
            return msg switch
            {
                WelcomeMessage welcome => welcome,
                ErrorMessage error     => throw new HandshakeException(error.Code, error.Description),
                _                      => throw new HandshakeException(
                                              "unexpected_message",
                                              $"Expected Welcome but received {msg.GetType().Name}."),
            };
        }

        throw new HandshakeException(
            "connection_closed",
            "Connection closed before handshake completed.");
    }

    public async Task SendAsync(Message message, CancellationToken cancellationToken = default)
    {
        var data = _serializer.Serialize(message);
        await _transport.SendAsync(data, cancellationToken);
    }

    /// <summary>
    /// Yields decoded messages until the connection closes or a
    /// <see cref="GoodbyeMessage"/> is received (which is consumed, not yielded).
    /// </summary>
    public async IAsyncEnumerable<Message> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var msg in ReceiveRawAsync(cancellationToken))
        {
            if (msg is GoodbyeMessage)
                yield break;

            yield return msg;
        }
    }

    public async Task DisconnectAsync(
        string reason = "normal",
        CancellationToken cancellationToken = default)
    {
        await SendAsync(new GoodbyeMessage(reason), cancellationToken);
        await _transport.DisconnectAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transport.IsConnected)
            await DisconnectAsync().ConfigureAwait(false);

        if (_transport is IAsyncDisposable d)
            await d.DisposeAsync().ConfigureAwait(false);
    }

    // Deserializes the raw byte stream without Goodbye filtering,
    // so HandshakeAsync can inspect all message types including errors.
    private async IAsyncEnumerable<Message> ReceiveRawAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var data in _transport.ReceiveAsync(cancellationToken))
            yield return _serializer.Deserialize(data);
    }
}
