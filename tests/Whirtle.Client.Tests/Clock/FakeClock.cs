using Whirtle.Client.Clock;

namespace Whirtle.Client.Tests.Clock;

internal sealed class FakeClock : ISystemClock
{
    private long _ticks;

    public FakeClock(long initialTicks = 0) => _ticks = initialTicks;

    public long UtcNowTicks => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
    public void Set(long ticks) => _ticks = ticks;
}
