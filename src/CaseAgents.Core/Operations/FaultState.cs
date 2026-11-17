namespace CaseAgents.Core.Operations;

/// <summary>
/// Simulated failures for demos and tests (set through POST /dev/faults in Development).
/// AiBackendDown: the bus delivers nothing, events wait, cases show "AI pending".
/// CaseAppDown: the Case API answers 503 to the AI backend, whose writes wait in its outbox.
/// </summary>
public sealed class FaultState
{
    private volatile bool aiBackendDown;
    private volatile bool caseAppDown;

    public bool AiBackendDown
    {
        get => aiBackendDown;
        set => aiBackendDown = value;
    }

    public bool CaseAppDown
    {
        get => caseAppDown;
        set => caseAppDown = value;
    }

    public FaultSnapshot Snapshot() => new(AiBackendDown, CaseAppDown);
}

public sealed record FaultSnapshot(bool AiBackendDown, bool CaseAppDown);
