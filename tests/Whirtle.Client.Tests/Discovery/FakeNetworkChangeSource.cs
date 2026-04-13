using System.Net.NetworkInformation;
using Whirtle.Client.Discovery;

namespace Whirtle.Client.Tests.Discovery;

/// <summary>
/// Test double for <see cref="INetworkChangeSource"/> that lets tests trigger
/// <see cref="NetworkAddressChanged"/> on demand.
/// </summary>
internal sealed class FakeNetworkChangeSource : INetworkChangeSource
{
    public event NetworkAddressChangedEventHandler? NetworkAddressChanged;

    public void TriggerAddressChanged()
        => NetworkAddressChanged?.Invoke(this, EventArgs.Empty);
}
