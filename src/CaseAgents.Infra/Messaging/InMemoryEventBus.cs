using CaseAgents.Core.Messaging;
using CaseAgents.Core.Operations;

namespace CaseAgents.Infra.Messaging;

/// <summary>
/// In-process event bus with the delivery rules of <see cref="IEventBus"/> (stand-in for Service Bus sessions per case).
/// All state sits behind one lock; handlers are called outside it, one event at a time, and delivery passes never overlap.
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly IReadOnlyList<ICaseEventHandler> handlers;
    private readonly FaultState faults;
    private readonly TimeProvider time;
    private readonly BusOptions options;

    private readonly object gate = new();
    private readonly SemaphoreSlim passGate = new(1, 1);
    private readonly Dictionary<string, CaseQueue> cases = new(StringComparer.Ordinal);
    private readonly List<string> caseOrder = [];
    private readonly List<DeadLetter> deadLetters = [];

    private long published;
    private long delivered;
    private long duplicatesIgnored;
    private long outOfOrderDetected;
    private long gapsSkipped;
    private long retries;

    public InMemoryEventBus(IEnumerable<ICaseEventHandler> handlers, FaultState faults, TimeProvider time, BusOptions options)
    {
        this.handlers = handlers.ToList();
        this.faults = faults;
        this.time = time;
        this.options = options;
    }

    /// <summary>Released on every publish or enqueue so the pump runs a pass right away.</summary>
    public SemaphoreSlim WorkSignal { get; } = new(0);

    public Task<CaseEvent> PublishAsync(string caseId, string eventType, string? questionId = null, string? value = null, CancellationToken ct = default)
    {
        CaseEvent evt;
        lock (gate)
        {
            var queue = GetQueue(caseId);
            var sequence = queue.LastSequence + 1;
            evt = new CaseEvent($"{caseId}:{sequence}", caseId, sequence, eventType, time.GetUtcNow(), questionId, value);
            Add(queue, evt);
            published++;
        }

        Signal();
        return Task.FromResult(evt);
    }

    public Task EnqueueAsync(CaseEvent evt, CancellationToken ct = default)
    {
        lock (gate)
            Add(GetQueue(evt.CaseId), evt);

        Signal();
        return Task.CompletedTask;
    }

    public async Task<int> DeliverPendingAsync(CancellationToken ct = default)
    {
        if (faults.AiBackendDown || handlers.Count == 0)
            return 0;

        await passGate.WaitAsync(ct);
        try
        {
            List<string> order;
            lock (gate)
                order = caseOrder.ToList();

            var handled = 0;
            foreach (var caseId in order)
                handled += await DeliverCaseAsync(caseId, ct);
            return handled;
        }
        finally
        {
            passGate.Release();
        }
    }

    public int PendingFor(string caseId)
    {
        lock (gate)
            return cases.TryGetValue(caseId, out var queue) ? queue.Events.Count : 0;
    }

    public BusStats GetStats()
    {
        lock (gate)
        {
            return new BusStats(
                published,
                delivered,
                duplicatesIgnored,
                outOfOrderDetected,
                gapsSkipped,
                retries,
                Pending: cases.Values.Sum(q => q.Events.Count),
                Held: cases.Values.Sum(q => q.HeldSince.Count),
                DeadLetterCount: deadLetters.Count,
                DeadLetters: deadLetters.ToList());
        }
    }

    /// <summary>Delivers this case's events in sequence until the queue is empty or the case must wait (held or retrying).</summary>
    private async Task<int> DeliverCaseAsync(string caseId, CancellationToken ct)
    {
        var handled = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var next = TakeNextDeliverable(caseId);
            if (next is null)
                return handled;

            Exception? failure = null;
            try
            {
                foreach (var handler in handlers)
                    await handler.HandleAsync(next, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failure = ex;
            }

            if (!RecordOutcome(caseId, next, failure))
                return handled;
            if (failure is null)
                handled++;
        }
    }

    /// <summary>
    /// Drops duplicates, holds an event ahead of the expected sequence (or skips the gap after the timeout),
    /// and returns the event to deliver now, or null when the case has nothing deliverable in this pass.
    /// </summary>
    private CaseEvent? TakeNextDeliverable(string caseId)
    {
        lock (gate)
        {
            var queue = cases[caseId];
            while (queue.Events.Count > 0)
            {
                var next = queue.Events.MinBy(e => e.Sequence)!;

                if (queue.DeliveredIds.Contains(next.MessageId) || next.Sequence < queue.NextExpected)
                {
                    queue.Events.Remove(next);
                    queue.HeldSince.Remove(next.MessageId);
                    duplicatesIgnored++;
                    continue;
                }

                var now = time.GetUtcNow();
                if (next.Sequence == queue.NextExpected)
                    return IsDue(queue, next, now) ? next : null;

                if (!queue.HeldSince.TryGetValue(next.MessageId, out var since))
                {
                    since = now;
                    queue.HeldSince[next.MessageId] = now;
                    outOfOrderDetected++;
                }

                if (now - since <= options.OutOfOrderTimeout)
                    return null;

                gapsSkipped++;
                queue.NextExpected = next.Sequence;
                return next;
            }

            return null;
        }
    }

    /// <summary>Applies the result of one delivery. Returns true when the case can continue in this pass.</summary>
    private bool RecordOutcome(string caseId, CaseEvent evt, Exception? failure)
    {
        lock (gate)
        {
            var queue = cases[caseId];

            if (failure is null)
            {
                Complete(queue, evt);
                queue.DeliveredIds.Add(evt.MessageId);
                delivered++;
                return true;
            }

            retries++;
            var count = queue.DeliveryCounts.GetValueOrDefault(evt.MessageId) + 1;
            queue.DeliveryCounts[evt.MessageId] = count;
            if (count < options.MaxDeliveryCount)
            {
                // Backoff: RetryDelay * 2^(count-1) before the next attempt; the case waits meanwhile.
                queue.NotBefore[evt.MessageId] = time.GetUtcNow() + options.RetryDelay * Math.Pow(2, count - 1);
                return false;
            }

            Complete(queue, evt);
            deadLetters.Add(new DeadLetter(evt, failure.Message, count, time.GetUtcNow()));
            return true;
        }
    }

    private static void Complete(CaseQueue queue, CaseEvent evt)
    {
        queue.Events.Remove(evt);
        queue.HeldSince.Remove(evt.MessageId);
        queue.DeliveryCounts.Remove(evt.MessageId);
        queue.NotBefore.Remove(evt.MessageId);
        queue.NextExpected = evt.Sequence + 1;
    }

    /// <summary>False while a failed event is waiting for its retry time.</summary>
    private static bool IsDue(CaseQueue queue, CaseEvent evt, DateTimeOffset now) =>
        !queue.NotBefore.TryGetValue(evt.MessageId, out var notBefore) || now >= notBefore;

    private CaseQueue GetQueue(string caseId)
    {
        if (!cases.TryGetValue(caseId, out var queue))
        {
            queue = new CaseQueue();
            cases[caseId] = queue;
            caseOrder.Add(caseId);
        }

        return queue;
    }

    private static void Add(CaseQueue queue, CaseEvent evt)
    {
        queue.Events.Add(evt);
        queue.LastSequence = Math.Max(queue.LastSequence, evt.Sequence);
    }

    private void Signal()
    {
        if (WorkSignal.CurrentCount == 0)
            WorkSignal.Release();
    }

    private sealed class CaseQueue
    {
        /// <summary>Queued and held events, in arrival order (delivery picks the lowest Sequence).</summary>
        public List<CaseEvent> Events { get; } = [];

        public long NextExpected { get; set; } = 1;

        /// <summary>Highest Sequence seen; PublishAsync assigns the next one.</summary>
        public long LastSequence { get; set; }

        public HashSet<string> DeliveredIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> DeliveryCounts { get; } = new(StringComparer.Ordinal);

        /// <summary>Failed events and the earliest time of their next attempt.</summary>
        public Dictionary<string, DateTimeOffset> NotBefore { get; } = new(StringComparer.Ordinal);

        /// <summary>Events ahead of NextExpected and when they were first held.</summary>
        public Dictionary<string, DateTimeOffset> HeldSince { get; } = new(StringComparer.Ordinal);
    }
}
