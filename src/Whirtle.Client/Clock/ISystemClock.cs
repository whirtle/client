// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Clock;

/// <summary>Abstraction over wall-clock time, enabling deterministic testing.</summary>
internal interface ISystemClock
{
    /// <summary>Current UTC time expressed as Unix microseconds (μs since 1970-01-01T00:00:00Z).</summary>
    long UtcNowMicroseconds { get; }
}
