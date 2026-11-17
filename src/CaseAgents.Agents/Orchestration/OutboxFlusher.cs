using CaseAgents.Core.Operations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaseAgents.Agents;

/// <summary>
/// Retries the outbox every OutboxFlushInterval. A Write entry is sent again as is (same If-Match and Idempotency-Key,
/// so a write that was applied but whose response was lost comes back as a duplicate); then the run continues.
/// An Event entry is handled again by the orchestrator, which re-queues it if the case app is still unavailable.
/// If a replay throws (for example the model times out), the entry goes back in the outbox and is tried again on a later
/// flush, up to MaxReplayAttempts times; then it is dropped with an error log. Nothing is flushed while "AI backend down".
/// </summary>
public sealed class OutboxFlusher(
    Outbox outbox,
    ICaseAppClient app,
    CaseOrchestrator orchestrator,
    AiBackendOptions o,
    FaultState faults,
    ILogger<OutboxFlusher> log) : BackgroundService
{
    public const int MaxReplayAttempts = 5;

    public async Task FlushOnceAsync(CancellationToken ct)
    {
        // The backend is paused as a whole: queued work waits like the bus events do.
        if (faults.AiBackendDown)
            return;

        foreach (var entry in outbox.Snapshot())
        {
            try
            {
                if (entry.Write is not null && entry.QuestionId is not null)
                    await FlushWriteAsync(entry, ct);
                else if (entry.Event is not null)
                {
                    outbox.Remove(entry.Id);
                    await orchestrator.HandleAsync(entry.Event, ct);
                }
                else
                {
                    outbox.Remove(entry.Id);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Requeue(entry, ex);
            }
        }
    }

    /// <summary>
    /// Puts a failed entry back. Replaying it is safe: a Write entry is sent with the same Idempotency-Key
    /// (a duplicate if it was applied) and an Event entry makes the orchestrator re-read the case.
    /// </summary>
    private void Requeue(OutboxEntry entry, Exception ex)
    {
        outbox.Remove(entry.Id);
        var attempts = entry.Attempts + 1;
        if (attempts >= MaxReplayAttempts)
        {
            log.LogError(ex, "Case {CaseId}: outbox entry {EntryId} dropped after {Attempts} failed replays.", entry.CaseId, entry.Id, attempts);
            return;
        }

        log.LogWarning(ex, "Case {CaseId}: outbox replay failed ({Attempts}/{Max}); it stays in the outbox.", entry.CaseId, attempts, MaxReplayAttempts);
        outbox.Add(entry with { Attempts = attempts });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(o.OutboxFlushInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await FlushOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Outbox flush failed.");
            }
        }
    }

    private async Task FlushWriteAsync(OutboxEntry entry, CancellationToken ct)
    {
        var put = await app.PutProposalAsync(entry.CaseId, entry.QuestionId!, entry.Write!, entry.IfMatch ?? "", entry.IdempotencyKey ?? "", ct);
        switch (put.Outcome)
        {
            case CaseAppOutcome.Unavailable:
                return;
            case CaseAppOutcome.Ok:
            case CaseAppOutcome.Conflict:
                // Conflict: the case changed meanwhile; the run re-reads and decides again.
                outbox.Remove(entry.Id);
                await orchestrator.RunAsync(entry.CaseId, entry.TriggerMessageId, ct);
                return;
            default:
                outbox.Remove(entry.Id);
                log.LogError("Case {CaseId} {QuestionId}: queued proposal refused ({Outcome}: {Error}).",
                    entry.CaseId, entry.QuestionId, put.Outcome, put.Error);
                return;
        }
    }
}
