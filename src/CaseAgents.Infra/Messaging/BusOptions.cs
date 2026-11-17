namespace CaseAgents.Infra.Messaging;

/// <summary>Configuration section "Bus" (in-memory event bus).</summary>
public sealed class BusOptions
{
    public const string SectionName = "Bus";

    /// <summary>Deliveries of one event before it is dead-lettered.</summary>
    public int MaxDeliveryCount { get; set; } = 3;

    /// <summary>
    /// Backoff base: after the n-th failed delivery the event is not retried before RetryDelay * 2^(n-1).
    /// With the defaults (3 deliveries, 1 s) an event that keeps failing is dead-lettered about 3 s after the first try.
    /// The case waits meanwhile (per-case order is kept).
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long an event ahead of the expected sequence is held before the gap is skipped.</summary>
    public TimeSpan OutOfOrderTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the pump runs a delivery pass when nothing new was published.</summary>
    public TimeSpan PumpInterval { get; set; } = TimeSpan.FromMilliseconds(200);
}
