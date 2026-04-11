using System.Net;
using System.Net.Sockets;

namespace Whirtle.Client.Discovery;

/// <summary>Abstraction over a UDP multicast socket, for testability.</summary>
internal interface IMulticastSocket : IDisposable
{
    Task SendAsync(byte[] datagram, IPEndPoint endpoint, CancellationToken cancellationToken);
    Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken);
}
