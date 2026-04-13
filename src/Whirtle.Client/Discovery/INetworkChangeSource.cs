// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Net.NetworkInformation;

namespace Whirtle.Client.Discovery;

/// <summary>
/// Seam over <see cref="System.Net.NetworkInformation.NetworkChange"/> so that
/// <see cref="NetworkMonitor"/> can be exercised without a real network adapter.
/// </summary>
internal interface INetworkChangeSource
{
    event NetworkAddressChangedEventHandler NetworkAddressChanged;
}
