// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Clock;

internal interface ISystemClock
{
    long UtcNowTicks { get; }
}
