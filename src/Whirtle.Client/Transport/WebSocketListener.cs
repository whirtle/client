// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Whirtle.Client.Discovery;

namespace Whirtle.Client.Transport;

/// <summary>
/// Listens for incoming WebSocket connections from a Sendspin server.
///
/// Per the spec, clients advertise themselves via mDNS and the server
/// connects to them — clients must not initiate connections while advertising.
/// This class provides the server-facing HTTP/WebSocket endpoint.
///
/// Uses <see cref="TcpListener"/> with a manual WebSocket upgrade handshake
/// instead of <see cref="System.Net.HttpListener"/> to avoid the Windows URL
/// ACL requirement that causes <c>Access is denied</c> for non-admin processes.
/// </summary>
public sealed class WebSocketListener : IAsyncDisposable
{
    private readonly TcpListener _listener;

    public int    Port { get; }
    public string Path { get; }

    /// <param name="port">Port to listen on; defaults to <see cref="MdnsAdvertiser.DefaultPort"/> (8928).</param>
    /// <param name="path">WebSocket path; defaults to <see cref="MdnsAdvertiser.DefaultPath"/> (<c>/sendspin</c>).</param>
    public WebSocketListener(
        int    port = MdnsAdvertiser.DefaultPort,
        string path = MdnsAdvertiser.DefaultPath)
    {
        Port = port;
        Path = path.TrimEnd('/');

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
    }

    /// <summary>
    /// Waits for the server to connect, performs the WebSocket upgrade handshake,
    /// and returns the connection as an <see cref="ITransport"/> ready to pass to
    /// <see cref="Protocol.ProtocolClient"/>.
    /// </summary>
    public async Task<ITransport> AcceptAsync(CancellationToken cancellationToken = default)
    {
        TcpClient tcp;
        try
        {
            tcp = await _listener.AcceptTcpClientAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        tcp.NoDelay = true;
        var stream = tcp.GetStream();

        Dictionary<string, string> headers;
        try
        {
            headers = await ReadHttpHeadersAsync(stream, cancellationToken);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        if (!headers.TryGetValue("Upgrade", out var upgrade) ||
            !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            tcp.Dispose();
            throw new InvalidOperationException(
                "Incoming TCP connection was not a WebSocket upgrade request.");
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var wsKey) ||
            string.IsNullOrWhiteSpace(wsKey))
        {
            tcp.Dispose();
            throw new InvalidOperationException(
                "WebSocket upgrade request is missing Sec-WebSocket-Key header.");
        }

        // Send 101 Switching Protocols
        var accept   = ComputeAcceptKey(wsKey);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
           $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n");

        try
        {
            await stream.WriteAsync(response, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var webSocket = WebSocket.CreateFromStream(
            stream,
            isServer:           true,
            subProtocol:        null,
            keepAliveInterval:  TimeSpan.FromSeconds(30));

        // AcceptedWebSocket takes ownership of both the WebSocket and the
        // TcpClient so that Dispose() closes the underlying TCP connection.
        return new WebSocketTransport(new AcceptedWebSocket(webSocket, tcp));
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the HTTP request line and headers byte-by-byte until the
    /// blank line (<c>\r\n\r\n</c>) that terminates the header block.
    /// Byte-by-byte reading is intentional: it avoids over-reading into the
    /// binary WebSocket frame stream that immediately follows.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadHttpHeadersAsync(
        NetworkStream     stream,
        CancellationToken cancellationToken)
    {
        var sb      = new StringBuilder(512);
        var oneChar = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(oneChar, cancellationToken);
            if (read == 0) break; // connection closed before headers finished

            sb.Append((char)oneChar[0]);

            // HTTP header block ends with \r\n\r\n
            var len = sb.Length;
            if (len >= 4 &&
                sb[len - 4] == '\r' && sb[len - 3] == '\n' &&
                sb[len - 2] == '\r' && sb[len - 1] == '\n')
                break;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines   = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Skip(1)) // skip the GET /path HTTP/1.1 request line
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return headers;
    }

    /// <summary>
    /// Computes the <c>Sec-WebSocket-Accept</c> value per RFC 6455 §4.2.2.
    /// </summary>
    private static string ComputeAcceptKey(string clientKey)
    {
        const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(clientKey + magic));
        return Convert.ToBase64String(hash);
    }
}
