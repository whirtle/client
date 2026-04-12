// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace Whirtle.Client.Discovery;

/// <summary>
/// Advertises this Sendspin client on the local network via multicast DNS
/// (RFC 6762 / DNS-SD RFC 6763) so that servers can discover and connect to it.
///
/// Per the Sendspin spec:
///   Service type : <c>_sendspin._tcp.local.</c>
///   Default port : 8928
///   TXT path key : path  (WebSocket endpoint, default <c>/sendspin</c>)
///   TXT name key : name  (friendly player name, optional)
///
/// The advertiser listens for PTR queries on the mDNS multicast group and
/// responds with PTR + SRV + TXT + A records describing this client.
/// The server connects to the advertised address; clients must NOT
/// manually connect to servers while advertising.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    public const string ServiceType = "_sendspin._tcp.local.";
    public const int    DefaultPort = 8928;
    public const string DefaultPath = "/sendspin";

    internal const string TxtKeyPath = "path";
    internal const string TxtKeyName = "name";

    private static readonly IPEndPoint MdnsEndpoint =
        new(IPAddress.Parse("224.0.0.251"), 5353);

    private readonly IMulticastSocket _socket;
    private readonly string           _instanceName;
    private readonly string           _hostname;
    private readonly int              _port;
    private readonly string           _path;
    private readonly string?          _friendlyName;

    /// <param name="hostname">Local hostname used in SRV and A records.</param>
    /// <param name="friendlyName">Optional player display name (TXT <c>name</c> key).</param>
    /// <param name="port">Listening port (TXT and SRV records); defaults to 8928.</param>
    /// <param name="path">WebSocket path (TXT <c>path</c> key); defaults to <c>/sendspin</c>.</param>
    public MdnsAdvertiser(
        string  hostname,
        string? friendlyName = null,
        int     port         = DefaultPort,
        string  path         = DefaultPath)
        : this(new SystemMulticastSocket(), hostname, friendlyName, port, path) { }

    internal MdnsAdvertiser(
        IMulticastSocket socket,
        string           hostname,
        string?          friendlyName = null,
        int              port         = DefaultPort,
        string           path         = DefaultPath)
    {
        _socket       = socket;
        _hostname     = hostname;
        _friendlyName = friendlyName;
        _port         = port;
        _path         = path;

        var label     = friendlyName ?? hostname;
        _instanceName = $"{label}.{ServiceType}";
    }

    /// <summary>
    /// Sends an unsolicited mDNS announcement on startup (twice, 1 s apart per
    /// RFC 6762 §8.3 to survive packet loss), then listens for PTR queries and
    /// responds until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task AdvertiseAsync(CancellationToken cancellationToken = default)
    {
        var ip           = GetLocalIpAddress();
        var announcement = DnsMessage.BuildAdvertisement(
            _instanceName, _hostname, ip, _port, _path, _friendlyName);

        Log.Information(
            "mDNS: announcing \"{Name}\" on {IP}:{Port}{Path}",
            _instanceName, ip, _port, _path);

        try
        {
            await _socket.SendAsync(announcement, MdnsEndpoint, cancellationToken);
            await Task.Delay(1000, cancellationToken);
            await _socket.SendAsync(announcement, MdnsEndpoint, cancellationToken);
        }
        catch (OperationCanceledException) { return; }

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var parsed = DnsMessage.TryParse(received.Buffer);
            if (parsed is null || !parsed.IsQuery) continue;

            bool asksForUs = parsed.Questions.Any(q =>
                string.Equals(q.TrimEnd('.'), ServiceType.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

            if (!asksForUs) continue;

            try
            {
                await _socket.SendAsync(announcement, MdnsEndpoint, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private static string GetLocalIpAddress()
    {
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                    return addr.Address.ToString();
            }
        }
        return "127.0.0.1";
    }

    public void Dispose() => _socket.Dispose();
}
