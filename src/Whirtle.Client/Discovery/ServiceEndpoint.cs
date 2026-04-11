namespace Whirtle.Client.Discovery;

/// <summary>A discovered Whirtle server endpoint.</summary>
/// <param name="Host">Hostname or IP address of the server.</param>
/// <param name="Port">TCP port the server is listening on.</param>
public sealed record ServiceEndpoint(string Host, int Port)
{
    public Uri ToWebSocketUri(string path = "/") =>
        new($"ws://{Host}:{Port}{path}");
}
