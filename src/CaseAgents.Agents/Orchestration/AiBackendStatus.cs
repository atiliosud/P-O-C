using CaseAgents.Core.Abstractions;

namespace CaseAgents.Agents;

/// <summary>Tells the case app whether the AI backend still has work for a case (drives the "AI pending" banner).</summary>
public sealed class AiBackendStatus(Outbox outbox, CaseOrchestrator orchestrator) : IAiBackendStatus
{
    public bool IsPending(string caseId) => orchestrator.IsInProgress(caseId) || outbox.CountFor(caseId) > 0;

    public int OutboxCount => outbox.Count;
}
