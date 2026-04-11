using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Whirtle.Client.Discovery;

namespace Whirtle.Client.Tests.Discovery;

internal sealed class FakeMulticastSocket : IMulticastSocket
{
    private readonly Channel<UdpReceiveResult> _inbound =
        Channel.CreateUnbounded<UdpReceiveResult>();

    public IReadOnlyList<(byte[] Datagram, IPEndPoint Endpoint)> Sent => _sent;
    private readonly List<(byte[] Datagram, IPEndPoint Endpoint)> _sent = [];

    public void EnqueueResponse(byte[] datagram, IPEndPoint? from = null)
        => _inbound.Writer.TryWrite(new UdpReceiveResult(
               datagram,
               from ?? new IPEndPoint(IPAddress.Loopback, MdnsDiscovery.MdnsPort)));

    public void Close() => _inbound.Writer.TryComplete();

    public Task SendAsync(byte[] datagram, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        _sent.Add((datagram, endpoint));
        return Task.CompletedTask;
    }

    public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        => await _inbound.Reader.ReadAsync(cancellationToken);

    public void Dispose() { }
}
