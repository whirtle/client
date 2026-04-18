// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Serilog;

namespace Whirtle.Client.Clock;

/// <summary>
/// Two-dimensional Kalman filter for NTP-style clock synchronisation.
///
/// State vector: [offset (µs), drift (µs/s)]
///   offset — estimated difference between server and client clocks
///   drift  — rate of change of the offset (clock frequency error, µs/s)
///
/// Covariance matrix P (upper triangle):
///   _pOO — variance of offset estimate (µs²)
///   _pOD — covariance of offset and drift (µs · µs/s)
///   _pDD — variance of drift estimate ((µs/s)²)
///
/// Corresponds to SendspinTimeFilter in the C++ reference implementation.
/// Parameters mirror the C++ constructor (process_std_dev, drift_process_std_dev,
/// forget_factor, adaptive_cutoff, min_samples).
/// </summary>
internal sealed class KalmanClockFilter
{
    // ── Tuning constants (C++ reference defaults) ────────────────────────────

    // process_std_dev = 0.01 µs  →  q_offset = 0.01² = 1e-4 µs²/s
    private const double QOffset = 0.01 * 0.01;

    // drift_process_std_dev = 0.0 µs/s  →  q_drift = 0 (drift assumed constant)
    private const double QDrift  = 0.0;

    // forget_factor = 1.001  (> 1: inflates P on large residuals)
    private const double ForgetFactor = 1.001;

    // adaptive_cutoff = 0.75: forgetting fires when |innovation| > 0.75 * max_error
    private const double AdaptiveCutoff = 0.75;

    // min_samples = 100: forgetting is disabled for the first 100 updates
    private const int StabilizationCount = 100;

    // SNR threshold for drift significance: |drift| must exceed 2σ_drift
    private const double DriftSignificanceK = 2.0;

    // ── Kalman state ─────────────────────────────────────────────────────────

    private double _offset;        // µs
    private double _drift;         // µs/s
    private double _pOO;           // µs²
    private double _pOD;           // µs·(µs/s)
    private double _pDD;           // (µs/s)²
    private int    _count;         // updates processed so far
    private long   _lastUpdateUs;  // client Unix µs at most-recent update (T_last_update)
    private int    _forgetCount;   // cumulative forgetting-factor applications

    // ── Public observable state ───────────────────────────────────────────────

    /// <summary>Kalman-estimated clock offset in µs (server − client).</summary>
    public double OffsetUs => _offset;

    /// <summary>Kalman-estimated clock drift in µs/s.</summary>
    public double DriftUsPerS => _drift;

    /// <summary>
    /// Standard deviation of the offset estimate in µs (√P_OO).
    /// Equivalent to C++ <c>get_error()</c>.
    /// </summary>
    public double OffsetStdDevUs => Math.Sqrt(_pOO);

    /// <summary>Standard deviation of the drift estimate in µs/s (√P_DD).</summary>
    public double DriftStdDevUsPerS => Math.Sqrt(_pDD);

    /// <summary>
    /// <see langword="true"/> when the drift estimate is statistically significant:
    /// |drift| &gt; <c>DriftSignificanceK</c> × σ_drift, and at least 2 updates have occurred.
    /// Time-conversion methods use drift compensation only when this is true.
    /// </summary>
    public bool DriftIsSignificant =>
        _count >= 2 && Math.Abs(_drift) > DriftSignificanceK * DriftStdDevUsPerS;

    /// <summary>Total number of filter updates processed.</summary>
    public int UpdateCount => _count;

    /// <summary>Cumulative number of times the forgetting factor was applied.</summary>
    public int ForgetCount => _forgetCount;

    /// <summary>Client Unix µs timestamp of the most-recent update (T_last_update).</summary>
    public long LastUpdateUs => _lastUpdateUs;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates the filter with a new NTP measurement. Maps to C++ <c>update()</c>.
    /// </summary>
    /// <param name="measOffsetUs">
    /// Measured clock offset in µs: ((T2−T1)+(T3−T4))/2.
    /// </param>
    /// <param name="maxErrorUs">
    /// Worst-case offset uncertainty in µs: ((T4−T1)−(T3−T2))/2.
    /// Used as the measurement noise standard deviation: R = maxError².
    /// Also used as the adaptive forgetting threshold.
    /// </param>
    /// <param name="nowUs">
    /// Client Unix µs at T4 (moment the server reply was received).
    /// Stored as T_last_update and used to compute Δt.
    /// </param>
    public void Update(double measOffsetUs, double maxErrorUs, long nowUs)
    {
        var r = maxErrorUs * maxErrorUs; // measurement variance R

        if (_count == 0)
        {
            FirstUpdate(measOffsetUs, r, nowUs);
            return;
        }

        var dtS = Math.Max((nowUs - _lastUpdateUs) / 1_000_000.0, 1e-6);

        // 1. Prediction step — advance state and covariance by Δt.
        var predOffset           = _offset + _drift * dtS;
        var (pOOp, pODp, pDDp) = PredictCovariance(_pOO, _pOD, _pDD, dtS);

        Log.Debug(
            "Clock Kalman predict: dt={Dt:F3} s, pred_offset={PredOffset:+0.000;-0.000} ms, pOO={Poo:F6}",
            dtS, predOffset / 1_000.0, pOOp);

        if (_count == 1)
        {
            // Second update: initialize drift and its variance via finite differences.
            SecondUpdateInit(predOffset, measOffsetUs, r, dtS, ref pODp, ref pDDp);
        }

        // 2. Adaptive forgetting (enabled only after the stabilisation period).
        var innovation = measOffsetUs - predOffset;
        ApplyForgetting(innovation, maxErrorUs, ref pOOp, ref pODp, ref pDDp);

        // 3. Measurement update — fuse prediction with new measurement.
        ApplyMeasurementUpdate(predOffset, innovation, r, pOOp, pODp, pDDp);

        _lastUpdateUs = nowUs;
        _count++;
    }

    /// <summary>
    /// Converts a client-domain timestamp (µs) to the server domain.
    /// Applies drift compensation only when the estimate is statistically significant.
    /// Maps to C++ <c>compute_server_time()</c>.
    /// </summary>
    public long ClientToServerUs(long clientUs)
    {
        if (!DriftIsSignificant)
            return clientUs + (long)_offset;

        var dtS = (clientUs - _lastUpdateUs) / 1_000_000.0;
        return clientUs + (long)(_offset + _drift * dtS);
    }

    /// <summary>
    /// Converts a server-domain timestamp (µs) to the client domain.
    /// Applies drift compensation only when the estimate is statistically significant.
    /// Maps to C++ <c>compute_client_time()</c>.
    /// </summary>
    public long ServerToClientUs(long serverUs)
    {
        if (!DriftIsSignificant)
            return serverUs - (long)_offset;

        // Rearrange T_server = T_client + offset + drift*(T_client - T_last)/1e6:
        //   T_client = (T_server - offset + drift*T_last/1e6) / (1 + drift/1e6)
        var driftPerUs = _drift / 1_000_000.0;
        var numerator  = serverUs - _offset + _drift * (_lastUpdateUs / 1_000_000.0);
        return (long)(numerator / (1.0 + driftPerUs));
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private void FirstUpdate(double measOffsetUs, double r, long nowUs)
    {
        _offset       = measOffsetUs;
        _pOO          = r;
        _drift        = 0.0;
        _pOD          = 0.0;
        _pDD          = 0.0;
        _lastUpdateUs = nowUs;
        _count        = 1;

        Log.Information(
            "Clock Kalman init (count=1): offset={Offset:+0.000;-0.000} ms, " +
            "σ_offset={Sigma:F3} ms",
            _offset / 1_000.0, OffsetStdDevUs / 1_000.0);
    }

    private void SecondUpdateInit(
        double predOffset, double measOffsetUs, double r,
        double dtS,
        ref double pODp, ref double pDDp)
    {
        // Spec §3.1 (count=1): initialize drift via finite difference and
        // its variance from the two offset estimates.
        var y  = measOffsetUs - predOffset;
        _drift = y / dtS;
        pODp   = 0.0;
        pDDp   = (_pOO + r) / (dtS * dtS);

        Log.Information(
            "Clock Kalman init (count=2): drift={Drift:+0.0000;-0.0000} ms/s, " +
            "σ_drift={Sigma:F4} ms/s",
            _drift / 1_000.0, Math.Sqrt(pDDp) / 1_000.0);
    }

    // ── Prediction ────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the covariance matrix by Δt using the state-transition matrix
    /// F = [[1, Δt], [0, 1]] and additive process noise Q.
    /// </summary>
    private static (double pOOp, double pODp, double pDDp) PredictCovariance(
        double pOO, double pOD, double pDD, double dtS)
    {
        var pOOp = pOO + 2.0 * pOD * dtS + pDD * dtS * dtS + QOffset * dtS;
        var pODp = pOD + pDD * dtS;
        var pDDp = pDD + QDrift * dtS;   // QDrift = 0, retained for completeness
        return (pOOp, pODp, pDDp);
    }

    // ── Adaptive forgetting ───────────────────────────────────────────────────

    private void ApplyForgetting(
        double innovation, double maxErrorUs,
        ref double pOOp, ref double pODp, ref double pDDp)
    {
        if (_count < StabilizationCount)
            return;

        var threshold = AdaptiveCutoff * maxErrorUs;

        if (Math.Abs(innovation) > threshold)
        {
            pOOp        *= ForgetFactor;
            pODp        *= ForgetFactor;
            pDDp        *= ForgetFactor;
            _forgetCount++;

            Log.Debug(
                "Clock Kalman forget: |y|={Y:F3} ms > threshold={T:F3} ms, " +
                "λ²={L:F4}, forget_count={Fc}",
                Math.Abs(innovation) / 1_000.0, threshold / 1_000.0, ForgetFactor, _forgetCount);
        }
        else
        {
            Log.Debug(
                "Clock Kalman forget: |y|={Y:F3} ms ≤ threshold={T:F3} ms, no forgetting",
                Math.Abs(innovation) / 1_000.0, threshold / 1_000.0);
        }
    }

    // ── Measurement update ────────────────────────────────────────────────────

    private void ApplyMeasurementUpdate(
        double predOffset, double innovation, double r,
        double pOOp, double pODp, double pDDp)
    {
        var s    = pOOp + r;         // innovation covariance S
        var kOff = pOOp / s;         // Kalman gain for offset
        var kDri = pODp / s;         // Kalman gain for drift

        _offset = predOffset + kOff * innovation;
        _drift += kDri * innovation;

        // Simplified covariance update: P = (I - K·H) · P_pred
        _pOO = pOOp - kOff * pOOp;
        _pDD = pDDp - kDri * pODp;
        _pOD = pODp - kDri * pOOp;

    }
}
