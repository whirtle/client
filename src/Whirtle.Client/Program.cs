using System.Net;
using Whirtle.Client.Clock;
using Whirtle.Client.Discovery;
using Whirtle.Client.Protocol;
using Whirtle.Client.Role;
using Whirtle.Client.Transport;

namespace Whirtle.Client;

/// <summary>
/// CLI test harness that exercises all three Phase-3 roles:
///   3.1 Controller  — play / pause / skip / volume commands
///   3.2 Metadata    — display now-playing track info
///   3.3 Artwork     — receive and report binary album art
///
/// Usage:
///   dotnet run [-- [--name &lt;friendly-name&gt;] [--port &lt;port&gt;]]
///
/// Flow:
///   1. Advertise via mDNS so the server can discover this client
///   2. Accept the server's incoming WebSocket connection
///   3. Perform the Sendspin handshake + one clock-sync round trip
///   4. Run a background receive loop (metadata, artwork, pings)
///   5. Interactive command prompt until the user types "quit" or presses Ctrl+C
/// </summary>
internal class Program
{
    private static async Task Main(string[] args)
    {
        // ── Parse arguments ──────────────────────────────────────────────────
        string? name = null;
        int     port = MdnsAdvertiser.DefaultPort;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--name" && i + 1 < args.Length)
                name = args[++i];
            else if (args[i] == "--port" && i + 1 < args.Length &&
                     int.TryParse(args[++i], out int p))
                port = p;
        }

        string hostname = Dns.GetHostName();

        Console.WriteLine("Whirtle Client — Sendspin test harness");
        Console.WriteLine($"  Hostname : {hostname}");
        Console.WriteLine($"  Port     : {port}");
        if (name is not null)
            Console.WriteLine($"  Name     : {name}");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // ── mDNS advertiser ─────────────────────────────────────────────────
        using var advertiser = new MdnsAdvertiser(hostname, name, port);
        _ = advertiser.AdvertiseAsync(cts.Token); // background loop
        Console.WriteLine("[mDNS] Advertising _sendspin._tcp.local. — waiting for server…");

        // ── WebSocket listener ───────────────────────────────────────────────
        await using var listener = new WebSocketListener(port);
        Console.WriteLine($"[WS]   Listening on :{port}{MdnsAdvertiser.DefaultPath}");

        ITransport transport;
        try
        {
            transport = await listener.AcceptAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[bye]");
            return;
        }

        Console.WriteLine("[WS]   Server connected.");

        // ── Handshake ────────────────────────────────────────────────────────
        await using var protocol = new ProtocolClient(transport);

        WelcomeMessage welcome;
        try
        {
            welcome = await protocol.HandshakeAsync("1.0", cts.Token);
        }
        catch (HandshakeException ex)
        {
            Console.Error.WriteLine($"[ERROR] Handshake failed ({ex.Code}): {ex.Message}");
            return;
        }

        Console.WriteLine($"[Proto] Session {welcome.SessionId}  server {welcome.ServerVersion}");

        // ── Clock sync ───────────────────────────────────────────────────────
        var syncer = new ClockSynchronizer(protocol);
        var sync   = await syncer.SyncOnceAsync(cts.Token);
        Console.WriteLine(
            $"[Clock] Offset {sync.ClockOffset.TotalMilliseconds:+0.0;-0.0} ms  " +
            $"RTT {sync.RoundTripTime.TotalMilliseconds:0.0} ms");

        // ── Roles ────────────────────────────────────────────────────────────
        var controller = new ControllerClient(protocol);
        var metadata   = new NowPlayingState();
        var artwork    = new ArtworkReceiver();

        metadata.Changed += () =>
            Console.WriteLine($"[Meta]  {metadata}");

        artwork.Changed += () =>
            Console.WriteLine(
                $"[Art]   {artwork.MimeType}  {artwork.Data!.Length:N0} bytes");

        // ── Background receive loop ──────────────────────────────────────────
        var receiveTask = ReceiveLoopAsync(protocol, metadata, artwork, cts.Token);

        // ── Interactive command prompt ───────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Commands:  play  pause  skip  volume <0–100>  status  quit");
        Console.WriteLine();

        while (!cts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Task.Run(Console.ReadLine, cts.Token);
            }
            catch (OperationCanceledException) { break; }

            if (line is null) break;

            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "play":
                        await controller.PlayAsync(cts.Token);
                        Console.WriteLine("[CMD]  play sent");
                        break;

                    case "pause":
                        await controller.PauseAsync(cts.Token);
                        Console.WriteLine("[CMD]  pause sent");
                        break;

                    case "skip":
                        await controller.SkipAsync(cts.Token);
                        Console.WriteLine("[CMD]  skip sent");
                        break;

                    case "volume" when parts.Length > 1 &&
                                       double.TryParse(parts[1], out double vol):
                        await controller.SetVolumeAsync(vol / 100.0, cts.Token);
                        Console.WriteLine($"[CMD]  volume {vol:0}% sent");
                        break;

                    case "volume":
                        Console.WriteLine("usage: volume <0-100>");
                        break;

                    case "status":
                        Console.WriteLine($"  Now playing : {metadata}");
                        if (artwork.Data is { } art)
                            Console.WriteLine(
                                $"  Artwork     : {artwork.MimeType}  ({art.Length:N0} bytes)");
                        else
                            Console.WriteLine("  Artwork     : (none)");
                        break;

                    case "quit":
                    case "exit":
                        cts.Cancel();
                        break;

                    default:
                        Console.WriteLine($"Unknown command: {parts[0]}");
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex.Message}");
            }
        }

        Console.WriteLine("[bye]");

        try { await receiveTask; }
        catch (OperationCanceledException) { }
    }

    private static async Task ReceiveLoopAsync(
        ProtocolClient    protocol,
        NowPlayingState   metadata,
        ArtworkReceiver   artwork,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in protocol.ReceiveAllAsync(cancellationToken))
        {
            switch (frame)
            {
                case ProtocolFrame { Message: NowPlayingMessage msg }:
                    metadata.Update(msg);
                    break;

                case ArtworkFrame art:
                    artwork.ProcessFrame(art);
                    break;

                // Keepalive
                case ProtocolFrame { Message: PingMessage }:
                    await protocol.SendAsync(new PongMessage(), cancellationToken);
                    break;
            }
        }
    }
}
