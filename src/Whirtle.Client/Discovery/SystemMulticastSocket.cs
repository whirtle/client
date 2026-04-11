using System.Net;
using System.Net.Sockets;

namespace Whirtle.Client.Discovery;

internal sealed class SystemMulticastSocket : IMulticastSocket
{
    private readonly UdpClient _udp;

    private const int    MdnsPort  = 5353;
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    public SystemMulticastSocket()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        _udp.JoinMulticastGroup(MdnsGroup);
        _udp.MulticastLoopback = true;
    }

    public Task SendAsync(byte[] datagram, IPEndPoint endpoint, CancellationToken cancellationToken)
        => _udp.SendAsync(datagram, datagram.Length, endpoint).WaitAsync(cancellationToken);

    public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        => await _udp.ReceiveAsync(cancellationToken);

    public void Dispose() => _udp.Dispose();
}
