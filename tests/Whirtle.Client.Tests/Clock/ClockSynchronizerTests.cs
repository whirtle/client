using Whirtle.Client.Clock;
using Whirtle.Client.Protocol;
using Whirtle.Client.Tests.Protocol;

namespace Whirtle.Client.Tests.Clock;

public class ClockSynchronizerTests
{
    private static (ClockSynchronizer syncer, FakeClock clock, FakeTransport transport) Build(
        TimeSpan? syncTimeout       = null,
        TimeSpan? rapidSyncInterval = null)
    {
        var transport = new FakeTransport();
        var client    = new ProtocolClient(transport);
        var clock     = new FakeClock();
        return (new ClockSynchronizer(client, clock, syncTimeout, rapidSyncInterval), clock, transport);
    }

    // Helper: ServerTimeMessage with T3 = T2 (zero server processing time).
    private static ServerTimeMessage Reply(long t1, long t2, long? t3 = null) =>
        new(t1, t2, t3 ?? t2);

    // ── SyncOnceAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncOnceAsync_ReturnsCorrectRtt()
    {
        // t0=T1=0, t3=T4=200 µs → RTT = 200 µs
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(200);
        syncer.Deliver(Reply(0, 100));

        var result = await syncTask;

        Assert.Equal(TimeSpan.FromMicroseconds(200), result.RoundTripTime);
    }

    [Fact]
    public async Task SyncOnceAsync_ZeroOffset_WhenClocksAgree()
    {
        // Symmetric: T1=0, T2=100, T3=100, T4=200 µs
        // offset = ((100-0)+(100-200))/2 = (100-100)/2 = 0
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(200);
        syncer.Deliver(Reply(0, 100));

        var result = await syncTask;

        Assert.Equal(TimeSpan.Zero, result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_PositiveOffset_WhenServerAhead()
    {
        // T1=0, T2=600, T3=600, T4=200 µs
        // offset = ((600-0)+(600-200))/2 = (600+400)/2 = 500 µs
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(200);
        syncer.Deliver(Reply(0, 600));

        var result = await syncTask;

        Assert.Equal(TimeSpan.FromMicroseconds(500), result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_NegativeOffset_WhenClientAhead()
    {
        // T1=0, T2=50, T3=50, T4=200 µs
        // offset = ((50-0)+(50-200))/2 = (50-150)/2 = -50 µs
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(200);
        syncer.Deliver(Reply(0, 50));

        var result = await syncTask;

        Assert.Equal(TimeSpan.FromMicroseconds(-50), result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_MaxError_AccountsForServerProcessingTime()
    {
        // T1=0, T2=100, T3=150, T4=300 µs
        // delay    = (300-0) - (150-100) = 300 - 50 = 250 µs
        // max_err  = 250 / 2 = 125 µs
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(300);
        syncer.Deliver(new ServerTimeMessage(ClientTransmitted: 0, ServerReceived: 100, ServerTransmitted: 150));

        var result = await syncTask;

        Assert.Equal(TimeSpan.FromMicroseconds(125), result.MaxError);
    }

    [Fact]
    public async Task SyncOnceAsync_ClientReceivedUs_IsT4()
    {
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(999);
        syncer.Deliver(Reply(0, 100));

        var result = await syncTask;

        Assert.Equal(999L, result.ClientReceivedUs);
    }

    [Fact]
    public async Task SyncOnceAsync_TimeoutBeforeDeliver_Throws()
    {
        var (syncer, _, _) = Build(syncTimeout: TimeSpan.FromMilliseconds(1));

        var ex = await Record.ExceptionAsync(() => syncer.SyncOnceAsync());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    // ── Deliver ───────────────────────────────────────────────────────────────

    [Fact]
    public void Deliver_ReturnsFalse_WhenNoPendingSync()
    {
        var (syncer, _, _) = Build();

        var delivered = syncer.Deliver(Reply(0, 100));

        Assert.False(delivered);
    }

    [Fact]
    public async Task Deliver_ReturnsTrue_WhenSyncIsPending()
    {
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();

        var delivered = syncer.Deliver(Reply(0, 100));
        await syncTask;

        Assert.True(delivered);
    }

    // ── RunAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_InvokesCallbackForEachDeliveredSync()
    {
        var (syncer, clock, _) = Build();
        var results = new List<ClockSyncResult>();
        using var cts = new CancellationTokenSource();

        clock.Set(0);
        var runTask = syncer.RunAsync(
            (r, _) => results.Add(r),
            interval: TimeSpan.FromMilliseconds(10),
            cancellationToken: cts.Token);

        for (var round = 0; round < 2; round++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!syncer.Deliver(Reply(0, 100)))
                await Task.Delay(1, deadline.Token);
            await Task.Delay(20);
        }

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task RunAsync_PerformsRapidSyncs_BeforeSteadyState()
    {
        var (syncer, clock, _) = Build(rapidSyncInterval: TimeSpan.FromMilliseconds(5));
        var results = new List<ClockSyncResult>();
        using var cts = new CancellationTokenSource();

        clock.Set(0);
        var runTask = syncer.RunAsync(
            (r, _) => results.Add(r),
            interval: TimeSpan.FromHours(1),
            cancellationToken: cts.Token);

        for (int i = 0; i < 3; i++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!syncer.Deliver(Reply(0, 100)))
                await Task.Delay(1, deadline.Token);
            await Task.Delay(10);
        }

        Assert.Equal(3, results.Count);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RunAsync_RetriesAfterTimeout_UntilCancelled()
    {
        var (syncer, _, _) = Build(syncTimeout: TimeSpan.FromMilliseconds(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => syncer.RunAsync((_, _) => { }, interval: TimeSpan.Zero, cts.Token));
    }

    // ── GetStats ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetStats_ReturnsZeroDefaults_BeforeAnySamples()
    {
        var (syncer, _, _) = Build();

        var stats = syncer.GetStats();

        Assert.Equal(0.0, stats.FilteredOffsetUs);
        Assert.Equal(0, stats.UpdateCount);
        Assert.Equal(0L, stats.LastSyncUtcMicroseconds);
        Assert.Equal(0, stats.ForgetCount);
        Assert.Equal(0.0, stats.DriftUsPerS);
    }

    [Fact]
    public async Task GetStats_UpdateCount_IncrementsAfterEachSync()
    {
        var (syncer, clock, _) = Build();
        using var cts = new CancellationTokenSource();
        clock.Set(1_000_000); // non-zero so Δt > 0

        var runTask = syncer.RunAsync((_, _) => { },
            interval: TimeSpan.FromMilliseconds(10),
            cancellationToken: cts.Token);

        for (int i = 0; i < 2; i++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!syncer.Deliver(Reply(0, 100)))
                await Task.Delay(1, deadline.Token);
            await Task.Delay(20);
        }

        Assert.Equal(2, syncer.GetStats().UpdateCount);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
    }

    // ── WaitForConvergenceAsync ───────────────────────────────────────────────

    [Fact]
    public async Task WaitForConvergenceAsync_ReturnsFalse_OnTimeout()
    {
        var (syncer, _, _) = Build();

        // targetStdDevUs is impossibly small — the filter will never converge that fast.
        var result = await syncer.WaitForConvergenceAsync(
            targetStdDevUs: 0.001,
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.False(result);
    }

    [Fact]
    public async Task WaitForConvergenceAsync_ReturnsFalse_WhenCancelled()
    {
        var (syncer, _, _) = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        var result = await syncer.WaitForConvergenceAsync(
            targetStdDevUs: 0.001,
            timeout: TimeSpan.FromSeconds(60),
            cancellationToken: cts.Token);

        Assert.False(result);
    }

    [Fact]
    public async Task WaitForConvergenceAsync_ReturnsTrue_WhenStdDevDropsBelowTarget()
    {
        // After a few rapid syncs with a symmetric 200 µs RTT the filter's std-dev
        // drops well below 200_000 µs (200 ms), so the convergence task must complete.
        var (syncer, clock, _) = Build(rapidSyncInterval: TimeSpan.FromMilliseconds(5));
        clock.Set(0);
        using var cts = new CancellationTokenSource();

        // Register convergence waiter BEFORE starting RunAsync.
        var convergenceTask = syncer.WaitForConvergenceAsync(
            targetStdDevUs: 200_000, timeout: TimeSpan.FromSeconds(5));

        var runTask = syncer.RunAsync(
            (_, _) => { },
            interval: TimeSpan.FromHours(1),
            cancellationToken: cts.Token);

        // Deliver three symmetric replies so the Kalman filter converges.
        for (int i = 0; i < 3; i++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!syncer.Deliver(Reply(0, 100)))
                await Task.Delay(1, deadline.Token);
            await Task.Delay(10);
        }

        var converged = await convergenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(converged);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RunAsync_StatsFilteredOffset_ReflectsKalmanEstimate()
    {
        // After a sync, the Kalman-filtered offset should match the raw measurement
        // (at count=1 the filter initialises directly from the measurement).
        //
        // Clock stays at 0 throughout so T1 = T4 = 0.
        // Reply has T2 = T3 = 500  →  offset = ((500-0)+(500-0))/2 = 500 µs.
        var (syncer, clock, _) = Build();
        using var cts = new CancellationTokenSource();
        clock.Set(0);

        ClockSyncStats? lastStats = null;
        var runTask = syncer.RunAsync(
            (_, stats) => lastStats = stats,
            interval: TimeSpan.FromHours(1),
            cancellationToken: cts.Token);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!syncer.Deliver(Reply(0, 500)))
            await Task.Delay(1, deadline.Token);
        await Task.Delay(20);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);

        Assert.NotNull(lastStats);
        Assert.Equal(500.0, lastStats!.FilteredOffsetUs, precision: 1);
    }
}
