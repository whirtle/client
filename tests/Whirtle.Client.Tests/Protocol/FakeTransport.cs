using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Whirtle.Client.Transport;

namespace Whirtle.Client.Tests.Protocol;

internal sealed class FakeTransport : ITransport
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly List<byte[]> _sent = [];
    private bool _connected;

    public bool IsConnected => _connected;
    public IReadOnlyList<byte[]> Sent => _sent;

    public void EnqueueInbound(byte[] data) => _inbound.Writer.TryWrite(data);
    public void CloseInbound() => _inbound.Writer.TryComplete();
    /// <summary>Completes the inbound stream with an error, simulating a connection drop.</summary>
    public void CompleteWithError(Exception ex) => _inbound.Writer.TryComplete(ex);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        _sent.Add(data.ToArray());
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<byte[]> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var data in _inbound.Reader.ReadAllAsync(cancellationToken))
            yield return data;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        _inbound.Writer.TryComplete();
        return Task.CompletedTask;
    }
}
