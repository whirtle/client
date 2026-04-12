using Whirtle.Client.Clock;

namespace Whirtle.Client.Tests.Clock;

internal sealed class FakeClock : ISystemClock
{
    private long _microseconds;

    public FakeClock(long initialMicroseconds = 0) => _microseconds = initialMicroseconds;

    public long UtcNowMicroseconds => _microseconds;

    public void Advance(TimeSpan by) => _microseconds += (long)by.TotalMicroseconds;
    public void Set(long microseconds) => _microseconds = microseconds;
}
