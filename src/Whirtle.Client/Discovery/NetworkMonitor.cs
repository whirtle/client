// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.NetworkInformation;
using Serilog;

namespace Whirtle.Client.Discovery;

/// <summary>Event data for <see cref="NetworkMonitor.PreferredAddressChanged"/>.</summary>
public sealed class NetworkAddressChangedEventArgs(string address) : EventArgs
{
    public string Address { get; } = address;
}

/// <summary>
/// Watches for network address changes and raises
/// <see cref="PreferredAddressChanged"/> only when the preferred LAN IP
/// (as resolved by <see cref="MdnsAdvertiser.GetLocalIpAddress"/>) actually
/// changes.
///
/// <para>
/// <see cref="NetworkChange.NetworkAddressChanged"/> fires many times during
/// a single transition (one event per adapter/address). A 500 ms debounce
/// coalesces the burst into a single notification.
/// </para>
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    /// <summary>
    /// Raised on a thread-pool thread when the preferred local IP address
    /// changes. The argument is the new IP address string.
    /// </summary>
    public event EventHandler<NetworkAddressChangedEventArgs>? PreferredAddressChanged;

    private readonly INetworkChangeSource _source;
    private readonly Func<string>         _getLocalIp;
    private readonly object               _lock = new();

    private string _lastIp;
    private Timer? _debounceTimer;

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    // ── Production constructor ────────────────────────────────────────────────

    public NetworkMonitor()
        : this(new SystemNetworkChangeSource(), MdnsAdvertiser.GetLocalIpAddress) { }

    // ── Internal constructor (tests inject fake source and IP resolver) ────────

    internal NetworkMonitor(INetworkChangeSource source, Func<string> getLocalIp)
    {
        _source     = source;
        _getLocalIp = getLocalIp;
        _lastIp     = getLocalIp();

        _source.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Replace any pending debounce timer with a fresh one.
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => CheckForChange(), null, DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void CheckForChange()
    {
        var current = _getLocalIp();

        string previous;
        lock (_lock)
        {
            previous = _lastIp;
            if (current == previous) return;
            _lastIp = current;
        }

        Log.Information("Preferred network address changed: {OldIp} → {NewIp}", previous, current);
        PreferredAddressChanged?.Invoke(this, new NetworkAddressChangedEventArgs(current));
    }

    public void Dispose()
    {
        _source.NetworkAddressChanged -= OnNetworkAddressChanged;
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }
}
