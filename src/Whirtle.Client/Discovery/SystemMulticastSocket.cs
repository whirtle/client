// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;

namespace Whirtle.Client.Discovery;

internal sealed class SystemMulticastSocket : IMulticastSocket
{
    private readonly UdpClient _udp;

    private const int    MdnsPort  = 5353;
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    /// <param name="localIp">
    /// The LAN IP to bind multicast to. Pins both inbound group membership and
    /// outbound multicast interface so packets don't route via a virtual adapter
    /// (WSL, Hyper-V, Docker). Pass <c>null</c> to use the system default (tests).
    /// </param>
    public SystemMulticastSocket(string? localIp = null)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

        var localAddress = localIp is not null ? IPAddress.Parse(localIp) : IPAddress.Any;
        _udp.JoinMulticastGroup(MdnsGroup, localAddress);

        if (localAddress != IPAddress.Any)
        {
            // Pin the outbound multicast interface to the same LAN adapter.
            _udp.Client.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                localAddress.GetAddressBytes());
        }

        _udp.MulticastLoopback = true;
    }

    public Task SendAsync(byte[] datagram, IPEndPoint endpoint, CancellationToken cancellationToken)
        => _udp.SendAsync(datagram, datagram.Length, endpoint).WaitAsync(cancellationToken);

    public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        => await _udp.ReceiveAsync(cancellationToken);

    public void Dispose() => _udp.Dispose();
}
