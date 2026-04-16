// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Net;
using Serilog;
using Whirtle.Client.Clock;
using Whirtle.Client.Discovery;
using Whirtle.Client.Protocol;
using Whirtle.Client.Role;
using Whirtle.Client.Transport;

namespace Whirtle.Client;

/// <summary>
/// CLI test harness that exercises all three roles:
///   Controller  — play / pause / skip / volume commands
///   Metadata    — display now-playing track info
///   Artwork     — receive and report binary album art
///
/// Usage:
///   dotnet run [-- [--name &lt;friendly-name&gt;] [--port &lt;port&gt;]]
///
/// Flow:
///   1. Advertise via mDNS so the server can discover this client
///   2. Accept the server's incoming WebSocket connections, applying
///      multi-server priority rules via <see cref="ConnectionManager"/>
///   3. Perform the Sendspin handshake + one clock-sync round trip
///   4. Run a background receive loop (metadata, artwork)
///   5. Interactive command prompt until the user types "quit" or presses Ctrl+C
/// </summary>
internal class Program
{
    private static async Task Main(string[] args)
    {
        // ── Parse arguments ──────────────────────────────────────────────────
        string? friendlyName = null;
        int     port         = MdnsAdvertiser.DefaultPort;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--name" && i + 1 < args.Length)
                friendlyName = args[++i];
            else if (args[i] == "--port" && i + 1 < args.Length &&
                     int.TryParse(args[++i], out int p))
                port = p;
        }

        string hostname  = Dns.GetHostName();
        string clientId  = $"whirtle-{hostname}";
        string clientName = friendlyName ?? $"Whirtle ({hostname})";

        Console.WriteLine("Whirtle Client — Sendspin test harness");
        Console.WriteLine($"  Client ID : {clientId}");
        Console.WriteLine($"  Name      : {clientName}");
        Console.WriteLine($"  Port      : {port}");
        Console.WriteLine();

        // ── Logging ──────────────────────────────────────────────────────────
        AppLogger.Configure();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
            AppLogger.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // ── mDNS advertiser ─────────────────────────────────────────────────
        using var advertiser = new MdnsAdvertiser(hostname, friendlyName, port);
        _ = advertiser.AdvertiseAsync(cts.Token); // background loop
        Console.WriteLine("[mDNS] Advertising _sendspin._tcp.local. — waiting for server…");

        // ── WebSocket listener ───────────────────────────────────────────────
        await using var listener       = new WebSocketListener(port);
        var             connManager    = new ConnectionManager();

        Console.WriteLine($"[WS]   Listening on :{port}{MdnsAdvertiser.DefaultPath}");

        ITransport? transport = null;

        // Accept loop: keeps accepting connections, applying priority rules.
        while (!cts.IsCancellationRequested)
        {
            ITransport candidate;
            try
            {
                candidate = await listener.AcceptAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[bye]");
                return;
            }

            // Perform handshake to learn the server's ID and connection reason
            // before deciding whether to accept it.
            await using var candidateProtocol = new ProtocolClient(candidate);

            ServerHelloMessage welcome;
            try
            {
                welcome = await candidateProtocol.HandshakeAsync(
                    clientId, clientName,
                    supportedRoles: ["metadata@v1", "controller@v1"],
                    cancellationToken: cts.Token);
            }
            catch (HandshakeException ex)
            {
                Console.Error.WriteLine(
                    $"[WARN]  Handshake failed ({ex.Code}): {ex.Message} — dropping connection.");
                continue;
            }

            if (!connManager.ShouldAccept(welcome.ServerId, welcome.ConnectionReason))
            {
                Console.WriteLine(
                    $"[WS]   Rejecting lower-priority connection from {welcome.ServerId} " +
                    $"(reason: {welcome.ConnectionReason}).");
                Log.Information("Rejected lower-priority server {ServerId} (reason={Reason})",
                    welcome.ServerId, welcome.ConnectionReason);
                await candidateProtocol.DisconnectAsync("rejected", cts.Token);
                continue;
            }

            connManager.Accept(welcome.ServerId, welcome.ConnectionReason);
            transport = candidate;

            Log.Information("Connected to server {ServerId} ({ServerName}), reason={Reason}",
                welcome.ServerId, welcome.Name, welcome.ConnectionReason);
            Console.WriteLine(
                $"[Proto] Connected  server={welcome.ServerId}  name={welcome.Name}  " +
                $"reason={welcome.ConnectionReason}");

            // ── Clock sync ───────────────────────────────────────────────────
            var syncer = new ClockSynchronizer(candidateProtocol);
            try
            {
                var sync = await syncer.SyncOnceAsync(cts.Token);
                Console.WriteLine(
                    $"[Clock] Raw offset {sync.ClockOffset.TotalMilliseconds:+0.0;-0.0} ms  " +
                    $"RTT {sync.RoundTripTime.TotalMilliseconds:0.0} ms  " +
                    $"max_err {sync.MaxError.TotalMilliseconds:0.0} ms");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[WARN]  Clock sync failed: {ex.Message}");
            }

            // ── Roles ────────────────────────────────────────────────────────
            var controller = new ControllerClient(candidateProtocol);
            var metadata   = new NowPlayingState();
            var artwork    = new ArtworkReceiver();

            metadata.Changed += () =>
                Console.WriteLine($"[Meta]  {metadata}");

            artwork.Changed += () =>
                Console.WriteLine(
                    $"[Art]   {artwork.MimeType}  {artwork.Data!.Length:N0} bytes");

            // ── Background receive loop ──────────────────────────────────────
            var receiveTask = ReceiveLoopAsync(candidateProtocol, metadata, artwork, cts.Token);

            // ── Interactive command prompt ───────────────────────────────────
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
                            await controller.NextAsync(cts.Token);
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
            Log.Information("Session ended");

            try { await receiveTask; }
            catch (OperationCanceledException) { }

            AppLogger.CloseAndFlush();
            return;
        }
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
                case ProtocolFrame { Message: ServerStateMessage msg } when msg.Metadata is not null:
                    metadata.Update(msg.Metadata);
                    break;

                case ArtworkFrame art:
                    artwork.ProcessFrame(art);
                    break;
            }
        }
    }
}
