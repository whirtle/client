// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Clock;

internal sealed class SystemClock : ISystemClock
{
    public static readonly SystemClock Instance = new();

    /// <inheritdoc/>
    public long UtcNowMicroseconds
        => (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
}
