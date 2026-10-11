using Bsync.Clocks;

namespace Bsync.Testing;

/// <summary>A physical clock that never moves, so clock-skew decisions are deterministic.</summary>
internal sealed class FixedClock(long nowMilliseconds) : IPhysicalClock
{
    public long NowMilliseconds() => nowMilliseconds;
}
