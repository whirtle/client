using Whirtle.Client.Clock;

namespace Whirtle.Client.Tests.Clock;

/// <summary>
/// Unit tests for <see cref="KalmanClockFilter"/>.
///
/// Time convention: all timestamps are Unix µs; "nowUs" values start at
/// 1_000_000_000 (1000 s) to avoid underflow and to keep Δt calculations readable.
/// </summary>
public class KalmanClockFilterTests
{
    // ── Initialization protocol ───────────────────────────────────────────────

    [Fact]
    public void FirstUpdate_SetsOffsetDirectlyFromMeasurement()
    {
        var f = new KalmanClockFilter();
        f.Update(measOffsetUs: 500.0, maxErrorUs: 50.0, nowUs: 1_000_000_000);

        Assert.Equal(500.0, f.OffsetUs);
        Assert.Equal(1, f.UpdateCount);
    }

    [Fact]
    public void FirstUpdate_SetsDriftToZero()
    {
        var f = new KalmanClockFilter();
        f.Update(measOffsetUs: 500.0, maxErrorUs: 50.0, nowUs: 1_000_000_000);

        Assert.Equal(0.0, f.DriftUsPerS);
    }

    [Fact]
    public void FirstUpdate_SetsOffsetVarianceFromMaxError()
    {
        var f = new KalmanClockFilter();
        // maxError=50 → R=2500 → pOO=2500 → σ=50
        f.Update(measOffsetUs: 500.0, maxErrorUs: 50.0, nowUs: 1_000_000_000);

        Assert.Equal(50.0, f.OffsetStdDevUs, precision: 6);
    }

    [Fact]
    public void SecondUpdate_InitializesDriftViaFiniteDifference()
    {
        var f = new KalmanClockFilter();
        f.Update(measOffsetUs: 0.0, maxErrorUs: 10.0, nowUs: 1_000_000_000);

        // Δt = 5 s; second measurement shows offset advanced by 10 µs
        // pred_offset ≈ 0 (drift was 0), innovation = 10, drift_init = 10/5 = 2 µs/s
        f.Update(measOffsetUs: 10.0, maxErrorUs: 10.0, nowUs: 1_005_000_000);

        // After measurement update with kDri≈0 (pOD=0 at count=2 init), drift ≈ 2 µs/s
        Assert.Equal(2.0, f.DriftUsPerS, precision: 3);
        Assert.Equal(2, f.UpdateCount);
    }

    [Fact]
    public void DriftNotSignificant_BeforeSecondUpdate()
    {
        var f = new KalmanClockFilter();
        f.Update(measOffsetUs: 100.0, maxErrorUs: 10.0, nowUs: 1_000_000_000);

        Assert.False(f.DriftIsSignificant);
    }

    // ── Kalman prediction / update ────────────────────────────────────────────

    [Fact]
    public void SteadyState_OffsetConvergesWithConsistentMeasurements()
    {
        // Feed the filter 10 identical measurements of 200 µs offset at 5 s intervals.
        // After convergence the filtered offset should be very close to 200 µs.
        var f = new KalmanClockFilter();
        long nowUs = 1_000_000_000;
        for (int i = 0; i < 10; i++)
        {
            f.Update(measOffsetUs: 200.0, maxErrorUs: 50.0, nowUs: nowUs);
            nowUs += 5_000_000; // +5 s
        }

        Assert.Equal(200.0, f.OffsetUs, precision: 0);
    }

    [Fact]
    public void SteadyState_OffsetStdDevDecreases_AsFilterConverges()
    {
        var f = new KalmanClockFilter();
        long nowUs = 1_000_000_000;

        f.Update(200.0, 50.0, nowUs);
        var sigmaAfterFirst = f.OffsetStdDevUs;

        nowUs += 5_000_000;
        f.Update(200.0, 50.0, nowUs);
        var sigmaAfterSecond = f.OffsetStdDevUs;

        Assert.True(sigmaAfterSecond < sigmaAfterFirst,
            $"σ should decrease: after 1st={sigmaAfterFirst:F3}, after 2nd={sigmaAfterSecond:F3}");
    }

    [Fact]
    public void KalmanGain_BlendsMeasurementAndPrediction()
    {
        // With a high-uncertainty prior and low-noise measurement, the update
        // should move the offset substantially toward the measurement.
        var f = new KalmanClockFilter();
        f.Update(measOffsetUs: 0.0, maxErrorUs: 100.0, nowUs: 1_000_000_000);
        // Now inject a measurement far from zero with low error.
        f.Update(measOffsetUs: 1000.0, maxErrorUs: 1.0, nowUs: 1_005_000_000);

        // Low measurement noise → offset should move close to 1000 µs.
        Assert.True(f.OffsetUs > 900.0,
            $"Offset {f.OffsetUs:F1} should be near 1000 µs with low-noise measurement");
    }

    // ── Adaptive forgetting ───────────────────────────────────────────────────

    [Fact]
    public void ForgetCount_StaysZero_DuringStabilizationPeriod()
    {
        // StabilizationCount = 100; after only 3 updates forgetting must not fire
        // even if the residual is large.
        var f = new KalmanClockFilter();
        long nowUs = 1_000_000_000;

        f.Update(0.0, 10.0, nowUs);
        nowUs += 5_000_000;
        f.Update(0.0, 10.0, nowUs);
        nowUs += 5_000_000;
        // Large residual — but still within stabilisation period.
        f.Update(9999.0, 10.0, nowUs);

        Assert.Equal(0, f.ForgetCount);
    }

    [Fact]
    public void ForgetCount_Increments_AfterStabilizationWhenResidualLarge()
    {
        // Feed 101 consistent updates to pass the stabilisation threshold, then
        // inject a large-residual measurement.
        var f      = new KalmanClockFilter();
        long nowUs = 1_000_000_000;

        for (int i = 0; i < 101; i++)
        {
            f.Update(0.0, 10.0, nowUs);
            nowUs += 5_000_000;
        }

        // maxErrorUs=10, threshold = 0.75*10 = 7.5 µs; innovation = 1000 µs >> threshold.
        f.Update(1000.0, 10.0, nowUs);

        Assert.True(f.ForgetCount > 0, "Forgetting factor should have fired at least once");
    }

    [Fact]
    public void ForgetCount_DoesNotIncrement_WhenResidualBelowThreshold()
    {
        var f      = new KalmanClockFilter();
        long nowUs = 1_000_000_000;

        // Converge filter on 0 µs for 101 updates.
        for (int i = 0; i < 101; i++)
        {
            f.Update(0.0, 10.0, nowUs);
            nowUs += 5_000_000;
        }
        int forgetBefore = f.ForgetCount;

        // Small residual: 1 µs < 0.75 * 10 = 7.5 µs → no forgetting.
        f.Update(1.0, 10.0, nowUs);

        Assert.Equal(forgetBefore, f.ForgetCount);
    }

    // ── Drift significance ────────────────────────────────────────────────────

    [Fact]
    public void DriftIsSignificant_FalseWhenDriftUncertaintyIsHigh()
    {
        // At count=2 after only a tiny Δt the drift variance is huge → not significant.
        var f = new KalmanClockFilter();
        f.Update(0.0, 10.0, 1_000_000_000);
        f.Update(1.0, 10.0, 1_000_001);  // Δt = 1 µs → enormous pDD

        Assert.False(f.DriftIsSignificant);
    }

    [Fact]
    public void DriftIsSignificant_TrueAfterManyConsistentDriftMeasurements()
    {
        // Inject a steady drift of 100 µs/s over many updates so pDD shrinks
        // and the SNR check passes.
        var f      = new KalmanClockFilter();
        long nowUs = 1_000_000_000;
        double offsetUs = 0.0;

        for (int i = 0; i < 20; i++)
        {
            f.Update(offsetUs, maxErrorUs: 5.0, nowUs);
            nowUs   += 5_000_000; // +5 s
            offsetUs += 500.0;    // drift = +100 µs/s
        }

        Assert.True(f.DriftIsSignificant,
            $"Drift {f.DriftUsPerS:F2} µs/s should be significant vs σ {f.DriftStdDevUsPerS:F4}");
    }

    // ── Time conversion ───────────────────────────────────────────────────────

    [Fact]
    public void ClientToServerUs_AddsOffset_WhenDriftNotSignificant()
    {
        var f = new KalmanClockFilter();
        f.Update(measOffsetUs: 1000.0, maxErrorUs: 50.0, nowUs: 1_000_000_000);
        // count=1 → drift not significant (drift=0)

        var server = f.ClientToServerUs(5_000_000_000);

        Assert.Equal(5_000_001_000L, server);
    }

    [Fact]
    public void ServerToClientUs_SubtractsOffset_WhenDriftNotSignificant()
    {
        var f = new KalmanClockFilter();
        f.Update(measOffsetUs: 1000.0, maxErrorUs: 50.0, nowUs: 1_000_000_000);

        var client = f.ServerToClientUs(5_000_001_000);

        Assert.Equal(5_000_000_000L, client);
    }

    [Fact]
    public void ClientToServer_ServerToClient_RoundTrip()
    {
        // After several updates with consistent drift, round-trip should recover
        // the original client timestamp to within 1 µs.
        var f      = new KalmanClockFilter();
        long nowUs = 1_000_000_000;
        double offsetUs = 0.0;

        for (int i = 0; i < 20; i++)
        {
            f.Update(offsetUs, 5.0, nowUs);
            nowUs   += 5_000_000;
            offsetUs += 500.0;
        }

        long originalClient = 2_000_000_000;
        var server = f.ClientToServerUs(originalClient);
        var back   = f.ServerToClientUs(server);

        Assert.True(Math.Abs(back - originalClient) <= 1,
            $"Round-trip error {Math.Abs(back - originalClient)} µs exceeds 1 µs");
    }

    [Fact]
    public void ClientToServerUs_IncludesDrift_WhenDriftSignificant()
    {
        // Build a filter with significant drift, then verify convert > offset-only.
        var f      = new KalmanClockFilter();
        long nowUs = 1_000_000_000;
        double offsetUs = 0.0;

        for (int i = 0; i < 20; i++)
        {
            f.Update(offsetUs, 5.0, nowUs);
            nowUs   += 5_000_000;
            offsetUs += 500.0; // 100 µs/s drift
        }

        Assert.True(f.DriftIsSignificant);

        // Evaluate conversion at T = lastUpdate + 10 s → drift correction = ~1000 µs
        long clientUs      = f.LastUpdateUs + 10_000_000;
        long withDrift     = f.ClientToServerUs(clientUs);
        long withoutDrift  = clientUs + (long)f.OffsetUs;

        Assert.NotEqual(withDrift, withoutDrift);
    }

    // ── LastUpdateUs ──────────────────────────────────────────────────────────

    [Fact]
    public void LastUpdateUs_IsSetAfterFirstUpdate()
    {
        var f = new KalmanClockFilter();
        f.Update(0.0, 10.0, 1_234_567_890);

        Assert.Equal(1_234_567_890L, f.LastUpdateUs);
    }

    [Fact]
    public void LastUpdateUs_AdvancesWithEachUpdate()
    {
        var f = new KalmanClockFilter();
        f.Update(0.0, 10.0, 1_000_000_000);
        f.Update(0.0, 10.0, 1_005_000_000);

        Assert.Equal(1_005_000_000L, f.LastUpdateUs);
    }
}
