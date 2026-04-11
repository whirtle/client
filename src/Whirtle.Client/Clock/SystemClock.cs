namespace Whirtle.Client.Clock;

internal sealed class SystemClock : ISystemClock
{
    public static readonly SystemClock Instance = new();

    public long UtcNowTicks => DateTime.UtcNow.Ticks;
}
