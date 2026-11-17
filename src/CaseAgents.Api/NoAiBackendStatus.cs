using CaseAgents.Core.Abstractions;

namespace CaseAgents.Api;

/// <summary>Used when the in-process AI backend is switched off: it never has work of its own.</summary>
public sealed class NoAiBackendStatus : IAiBackendStatus
{
    public bool IsPending(string caseId) => false;

    public int OutboxCount => 0;
}
