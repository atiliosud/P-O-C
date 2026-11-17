using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;

namespace CaseAgents.Agents;

/// <summary>
/// Work the AI backend could not hand to the case app because it was unavailable.
/// A Write entry is a proposal PUT to retry as is (same If-Match and Idempotency-Key); an Event entry is an event to handle again.
/// Attempts counts the replays that failed with an exception (for example a model timeout).
/// </summary>
public sealed record OutboxEntry(
    string Id,
    string CaseId,
    string TriggerMessageId,
    string? QuestionId,
    ProposalWrite? Write,
    string? IfMatch,
    string? IdempotencyKey,
    CaseEvent? Event,
    int Attempts = 0);

/// <summary>Thread-safe, in-memory outbox (in production: a durable store next to the AI backend).</summary>
public sealed class Outbox
{
    private readonly object gate = new();
    private readonly List<OutboxEntry> entries = [];

    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    public int CountFor(string caseId)
    {
        lock (gate)
            return entries.Count(e => e.CaseId == caseId);
    }

    public void Add(OutboxEntry entry)
    {
        lock (gate)
            entries.Add(entry);
    }

    /// <summary>A copy of the entries, oldest first.</summary>
    public IReadOnlyList<OutboxEntry> Snapshot()
    {
        lock (gate)
            return entries.ToList();
    }

    public bool Remove(string id)
    {
        lock (gate)
            return entries.RemoveAll(e => e.Id == id) > 0;
    }
}
