namespace Whirtle.Client.UI.ViewModels;

public enum ConnectionMode
{
    /// <summary>
    /// The server discovers this client via mDNS and opens the connection.
    /// <see cref="Whirtle.Client.Discovery.MdnsAdvertiser"/> must be running.
    /// </summary>
    ServerInitiated,

    /// <summary>
    /// This client discovers servers on the network (or uses a manually entered URL)
    /// and opens the connection itself.
    /// </summary>
    ClientInitiated,
}
