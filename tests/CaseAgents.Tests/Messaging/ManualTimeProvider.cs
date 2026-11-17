namespace CaseAgents.Tests.Messaging;

/// <summary>A clock that only moves when the test says so.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public static readonly DateTimeOffset DefaultStart = new(2026, 11, 17, 10, 30, 0, TimeSpan.Zero);

    private DateTimeOffset now;

    public ManualTimeProvider()
        : this(DefaultStart)
    {
    }

    public ManualTimeProvider(DateTimeOffset start) => now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
