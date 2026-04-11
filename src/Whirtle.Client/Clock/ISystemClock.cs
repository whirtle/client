namespace Whirtle.Client.Clock;

internal interface ISystemClock
{
    long UtcNowTicks { get; }
}
