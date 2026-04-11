using System.Net;
using Whirtle.Client.Discovery;

namespace Whirtle.Client.Transport;

/// <summary>
/// Listens for incoming WebSocket connections from a Sendspin server.
///
/// Per the spec, clients advertise themselves via mDNS and the server
/// connects to them — clients must not initiate connections while advertising.
/// This class provides the server-facing HTTP/WebSocket endpoint.
/// </summary>
public sealed class WebSocketListener : IAsyncDisposable
{
    private readonly HttpListener _listener;

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

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://*:{port}{Path}/");
        _listener.Start();
    }

    /// <summary>
    /// Waits for the server to connect and returns the connection as an
    /// <see cref="ITransport"/> ready to pass to <see cref="Protocol.ProtocolClient"/>.
    /// </summary>
    public async Task<ITransport> AcceptAsync(CancellationToken cancellationToken = default)
    {
        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.Close();
            throw new InvalidOperationException(
                $"Connection from {context.Request.RemoteEndPoint} was not a WebSocket upgrade.");
        }

        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        return new WebSocketTransport(new AcceptedWebSocket(wsContext.WebSocket));
    }

    public ValueTask DisposeAsync()
    {
        _listener.Close();
        return ValueTask.CompletedTask;
    }
}
