using System.Net;
using System.Runtime.CompilerServices;

namespace Whirtle.Client.Discovery;

/// <summary>
/// Discovers Whirtle servers on the local network using multicast DNS
/// (RFC 6762) by querying for <c>_whirtle._tcp.local.</c> PTR records.
///
/// Flow per response packet:
///   PTR  →  instance name
///   SRV  →  hostname + port  (from additional records in the same packet)
///   A    →  IP address        (from additional records in the same packet)
///
/// A <see cref="ServiceEndpoint"/> is emitted for every PTR record that has
/// a matching SRV and at least one A/AAAA record in the same response.
/// If no address record is present the SRV target hostname is used directly.
/// </summary>
public sealed class MdnsDiscovery : IDisposable
{
    public static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    public const int MdnsPort = 5353;

    public const string ServiceType = "_whirtle._tcp.local.";

    private static readonly IPEndPoint MdnsEndpoint = new(MdnsGroup, MdnsPort);

    private readonly IMulticastSocket _socket;

    public MdnsDiscovery() : this(new SystemMulticastSocket()) { }

    internal MdnsDiscovery(IMulticastSocket socket) => _socket = socket;

    /// <summary>
    /// Sends one mDNS PTR query and yields every <see cref="ServiceEndpoint"/>
    /// discovered from responses until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async IAsyncEnumerable<ServiceEndpoint> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = DnsMessage.BuildQuery(ServiceType);
        await _socket.SendAsync(query, MdnsEndpoint, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            var response = DnsMessage.TryParse(received.Buffer);
            if (response is null)
                continue;

            foreach (var endpoint in ExtractEndpoints(response))
                yield return endpoint;
        }
    }

    /// <summary>
    /// Waits for the first discovered endpoint and returns it, or returns
    /// <c>null</c> if <paramref name="cancellationToken"/> is cancelled first.
    /// </summary>
    public async Task<ServiceEndpoint?> DiscoverFirstAsync(
        CancellationToken cancellationToken = default)
    {
        await foreach (var ep in DiscoverAsync(cancellationToken))
            return ep;

        return null;
    }

    private static IEnumerable<ServiceEndpoint> ExtractEndpoints(DnsMessage.ParsedResponse r)
    {
        // Build a hostname→IP map from A/AAAA records in the same packet
        var addresses = r.ARecords
            .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Key, StringComparer.OrdinalIgnoreCase);

        // Map SRV target → SrvRecord for quick lookup
        var srvByTarget = r.SrvRecords
            .ToDictionary(s => s.Target, s => s, StringComparer.OrdinalIgnoreCase);

        foreach (var ptr in r.PtrRecords)
        {
            // The PTR value names the SRV record
            if (!srvByTarget.TryGetValue(ptr.InstanceName, out var srv) &&
                !r.SrvRecords.Any())
                continue;

            srv ??= r.SrvRecords.FirstOrDefault();
            if (srv is null) continue;

            // Prefer resolved IP; fall back to the target hostname
            var host = r.ARecords.Count > 0
                ? r.ARecords[0].Address
                : srv.Target.TrimEnd('.');

            yield return new ServiceEndpoint(host, srv.Port);
        }
    }

    public void Dispose() => _socket.Dispose();
}
