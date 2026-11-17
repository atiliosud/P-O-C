namespace CaseAgents.Core.Messaging;

/// <summary>Event types exchanged between the case application and the AI backend.</summary>
public static class CaseEventTypes
{
    /// <summary>Case app -> AI backend: the case was marked ready.</summary>
    public const string CaseReady = "case.ready";

    /// <summary>Case app -> AI backend: an analyst set a final value (QuestionId, Value).</summary>
    public const string AnswerConfirmed = "answer.confirmed";

    /// <summary>Case app -> AI backend: an analyst rejected a proposal (QuestionId).</summary>
    public const string ProposalRejected = "proposal.rejected";

    /// <summary>Case app -> AI backend: an analyst took over the case.</summary>
    public const string CaseTakenOver = "case.takeover";

    /// <summary>Case app -> AI backend: an analyst released the case back to the AI.</summary>
    public const string CaseReleased = "case.released";
}

/// <summary>
/// A message on the bus. Events are triggers, not deltas: handlers re-read the case, so they are idempotent.
/// </summary>
/// <param name="MessageId">Unique id; a second delivery with the same id is ignored (idempotent consumption).</param>
/// <param name="Sequence">Per-case sequence, starting at 1, assigned at publish time.</param>
public sealed record CaseEvent(
    string MessageId,
    string CaseId,
    long Sequence,
    string EventType,
    DateTimeOffset At,
    string? QuestionId = null,
    string? Value = null);

public sealed record DeadLetter(CaseEvent Event, string Reason, int DeliveryCount, DateTimeOffset At);

/// <summary>Counters exposed at GET /api/ops/bus and shown in the reviewer app.</summary>
public sealed record BusStats(
    long Published,
    long Delivered,
    long DuplicatesIgnored,
    long OutOfOrderDetected,
    long GapsSkipped,
    long Retries,
    int Pending,
    int Held,
    int DeadLetterCount,
    IReadOnlyList<DeadLetter> DeadLetters);

/// <summary>Consumer of bus messages (the AI backend's orchestrator). Throwing means "retry later".</summary>
public interface ICaseEventHandler
{
    Task HandleAsync(CaseEvent evt, CancellationToken ct);
}

/// <summary>
/// The event bus. In-memory in the PoC; in production Azure Service Bus with sessions per caseId and a dead-letter queue.
/// Delivery rules: per-case order by Sequence; an event with a MessageId already delivered, or a Sequence below the
/// next expected one, is ignored and counted as a duplicate; an event ahead of the expected Sequence is held until the
/// gap fills, or delivered after BusOptions.OutOfOrderTimeout (gap skipped, counted); a handler exception is retried
/// up to BusOptions.MaxDeliveryCount deliveries, then the event is dead-lettered and the case moves on.
/// While FaultState.AiBackendDown is true nothing is delivered (events wait).
/// </summary>
public interface IEventBus
{
    /// <summary>Assigns MessageId ("{caseId}:{sequence}") and the next per-case Sequence, enqueues, and returns the event.</summary>
    Task<CaseEvent> PublishAsync(string caseId, string eventType, string? questionId = null, string? value = null, CancellationToken ct = default);

    /// <summary>Enqueues an event exactly as given (keeps MessageId and Sequence): redelivery and tests.</summary>
    Task EnqueueAsync(CaseEvent evt, CancellationToken ct = default);

    /// <summary>One delivery pass over every queued event, applying the rules above. Returns how many were handled successfully.</summary>
    Task<int> DeliverPendingAsync(CancellationToken ct = default);

    /// <summary>Queued plus held events of this case (not yet delivered, not dead-lettered).</summary>
    int PendingFor(string caseId);

    BusStats GetStats();
}
