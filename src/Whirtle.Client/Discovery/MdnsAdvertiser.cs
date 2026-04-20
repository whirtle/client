// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

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

    // Re-announce every 60 s — half the SRV/A TTL of 120 s so records
    // never expire on the server while we're running.
    private static readonly TimeSpan ReannounceInterval = TimeSpan.FromSeconds(60);

    private readonly List<(IMulticastSocket Socket, string Ip)> _interfaces;
    private readonly string           _instanceName;
    private readonly string           _hostname;
    private readonly int              _port;
    private readonly string           _path;
    private readonly string?          _friendlyName;
    private volatile bool             _paused;

    /// <param name="hostname">Local hostname used in SRV and A records.</param>
    /// <param name="friendlyName">Optional player display name (TXT <c>name</c> key).</param>
    /// <param name="port">Listening port (TXT and SRV records); defaults to 8928.</param>
    /// <param name="path">WebSocket path (TXT <c>path</c> key); defaults to <c>/sendspin</c>.</param>
    public MdnsAdvertiser(
        string  hostname,
        string? friendlyName = null,
        int     port         = DefaultPort,
        string  path         = DefaultPath)
    {
        _interfaces = GetUsefulLocalIpAddresses()
            .Select(ip => ((IMulticastSocket)new SystemMulticastSocket(ip), ip))
            .ToList();
        _hostname     = hostname;
        _friendlyName = friendlyName;
        _port         = port;
        _path         = path;
        var label     = friendlyName ?? hostname;
        _instanceName = $"{label}.{ServiceType}";
    }

    internal MdnsAdvertiser(
        IMulticastSocket socket,
        string           hostname,
        string?          friendlyName = null,
        int              port         = DefaultPort,
        string           path         = DefaultPath,
        string           localIp      = "127.0.0.1")
    {
        _interfaces   = [(socket, localIp)];
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
    /// responds — re-announcing every <see cref="ReannounceInterval"/> to keep
    /// records fresh — until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task AdvertiseAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _interfaces.Select(i => AdvertiseInternalAsync(i.Socket, i.Ip, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task AdvertiseInternalAsync(
        IMulticastSocket  socket,
        string            ip,
        CancellationToken cancellationToken)
    {
        try
        {
            await AdvertiseCoreAsync(socket, ip, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "mDNS: advertiser failed on {IP}", ip);
        }
    }

    private async Task AdvertiseCoreAsync(
        IMulticastSocket  socket,
        string            ip,
        CancellationToken cancellationToken)
    {
        var announcement = DnsMessage.BuildAdvertisement(
            _instanceName, _hostname, ip, _port, _path, _friendlyName);

        Log.Information(
            "mDNS: announcing on {IP}:{Port}{Path}, hostname={Hostname}, friendlyName={FriendlyName}, instance={Instance}, reannounceInterval={ReannounceInterval}",
            ip, _port, _path, _hostname, _friendlyName, _instanceName, ReannounceInterval);

        // Two back-to-back announcements per RFC 6762 §8.3 (survive packet loss).
        // No delay between them — periodic re-announce every 60 s provides robustness.
        await socket.SendAsync(announcement, MdnsEndpoint, cancellationToken);
        await socket.SendAsync(announcement, MdnsEndpoint, cancellationToken);

        var reannounceAt = DateTimeOffset.UtcNow + ReannounceInterval;

        while (!cancellationToken.IsCancellationRequested)
        {
            var timeout = reannounceAt - DateTimeOffset.UtcNow;

            if (timeout <= TimeSpan.Zero)
            {
                if (!_paused)
                {
                    Log.Debug("mDNS: re-announcing on {IP}:{Port}{Path}", ip, _port, _path);
                    await socket.SendAsync(announcement, MdnsEndpoint, cancellationToken);
                }
                reannounceAt = DateTimeOffset.UtcNow + ReannounceInterval;
                continue;
            }

            // Receive with a deadline so we wake up to re-announce on time.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout elapsed — loop back to re-announce.
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var parsed = DnsMessage.TryParse(received.Buffer);
            if (parsed is null || !parsed.IsQuery) continue;

            bool asksForUs = parsed.Questions.Any(q =>
                string.Equals(q.TrimEnd('.'), ServiceType.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

            Log.Debug(
                "mDNS: query received from {Remote}: questions=[{Questions}], asksForUs={AsksForUs}",
                received.RemoteEndPoint,
                string.Join(", ", parsed.Questions),
                asksForUs);

            if (!asksForUs || _paused) continue;

            await socket.SendAsync(announcement, MdnsEndpoint, cancellationToken);
        }
    }

    internal static IReadOnlyList<string> GetUsefulLocalIpAddresses()
    {
        var candidates = new List<(int Score, string Ip)>();

        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                           or NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;

                int score = ScoreAddress(addr.Address);
                if (score < 0) continue; // exclude APIPA

                candidates.Add((score, addr.Address.ToString()));
            }
        }

        return candidates
            .OrderByDescending(x => x.Score)
            .Select(x => x.Ip)
            .ToList();
    }

    internal static string GetLocalIpAddress()
    {
        var addresses = GetUsefulLocalIpAddresses();
        return addresses.Count > 0 ? addresses[0] : "127.0.0.1";
    }

    /// <summary>
    /// Higher score = more likely to be the real LAN address.
    /// 172.16–31.x.x (Docker/WSL/Hyper-V) scores lowest.
    /// </summary>
    private static int ScoreAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b[0] == 169 && b[1] == 254)               return -1; // APIPA
        if (b[0] == 192 && b[1] == 168)               return  3; // typical home/office LAN
        if (b[0] == 10)                                return  2; // corporate LAN
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return  0; // virtual (Docker/WSL/Hyper-V)
        return 1; // anything else
    }

    /// <summary>Suppresses all outgoing announcements and query responses until <see cref="Resume"/> is called.</summary>
    public void Pause()  => _paused = true;

    /// <summary>Re-enables announcements and query responses after a prior <see cref="Pause"/> call.</summary>
    public void Resume() => _paused = false;

    public void Dispose()
    {
        foreach (var (socket, _) in _interfaces)
            socket.Dispose();
    }
}
