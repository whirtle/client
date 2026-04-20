// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.NetworkInformation;

namespace Whirtle.Client.Discovery;

/// <summary>
/// Production implementation of <see cref="INetworkChangeSource"/> that
/// delegates to <see cref="NetworkChange.NetworkAddressChanged"/>.
/// </summary>
internal sealed class SystemNetworkChangeSource : INetworkChangeSource
{
    public event NetworkAddressChangedEventHandler? NetworkAddressChanged
    {
        add    => NetworkChange.NetworkAddressChanged += value;
        remove => NetworkChange.NetworkAddressChanged -= value;
    }
}
