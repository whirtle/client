using Whirtle.Client.Discovery;

namespace Whirtle.Client.Tests.Discovery;

public class NetworkMonitorTests
{
    [Fact]
    public async Task PreferredAddressChanged_RaisedWhenIpChanges()
    {
        var source  = new FakeNetworkChangeSource();
        var ips     = new Queue<string>(["192.168.1.1", "10.0.0.5"]);
        using var monitor = new NetworkMonitor(source, () => ips.Dequeue());

        string? received = null;
        monitor.PreferredAddressChanged += (_, e) => received = e.Address;

        source.TriggerAddressChanged();

        // Wait for debounce + a margin
        await Task.Delay(700);

        Assert.Equal("10.0.0.5", received);
    }

    [Fact]
    public async Task PreferredAddressChanged_NotRaisedWhenIpUnchanged()
    {
        var source  = new FakeNetworkChangeSource();
        using var monitor = new NetworkMonitor(source, () => "192.168.1.1");

        bool raised = false;
        monitor.PreferredAddressChanged += (_, _) => raised = true;

        source.TriggerAddressChanged();

        await Task.Delay(700);

        Assert.False(raised);
    }

    [Fact]
    public async Task BurstOfEvents_DebouncedIntoSingleNotification()
    {
        var source   = new FakeNetworkChangeSource();
        int callCount = 0;
        // Constructor dequeues "192.168.1.1"; the single debounce check dequeues "10.0.0.5".
        // The 3 rapid triggers collapse into one timer, so CheckForChange runs exactly once.
        var ips = new Queue<string>(["192.168.1.1", "10.0.0.5"]);
        using var monitor = new NetworkMonitor(source, () => ips.TryDequeue(out var ip) ? ip : "10.0.0.5");

        monitor.PreferredAddressChanged += (_, _) => Interlocked.Increment(ref callCount);

        // Fire 3 events rapidly — should be coalesced into one debounce check
        source.TriggerAddressChanged();
        source.TriggerAddressChanged();
        source.TriggerAddressChanged();

        await Task.Delay(700);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Dispose_StopsNotifications()
    {
        var source  = new FakeNetworkChangeSource();
        var ips     = new Queue<string>(["192.168.1.1", "10.0.0.5"]);
        var monitor = new NetworkMonitor(source, () => ips.TryDequeue(out var ip) ? ip : "10.0.0.5");

        bool raised = false;
        monitor.PreferredAddressChanged += (_, _) => raised = true;

        monitor.Dispose();
        source.TriggerAddressChanged();

        await Task.Delay(700);

        Assert.False(raised);
    }
}
