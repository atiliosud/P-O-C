namespace CaseAgents.Core.Models;

/// <summary>POST /api/cases (Case.Submit).</summary>
public sealed record CreateCaseRequest(string? Title);

/// <summary>POST /api/cases/{caseId}/documents (Case.Submit, own case, status Draft or Ready).</summary>
public sealed record AddDocumentRequest(string? DocumentId, string? Name, string? Kind, IReadOnlyList<DocumentPage>? Pages);

/// <summary>
/// PUT /api/cases/{caseId}/proposals/{questionId} (Case.Agent scoped to the case).
/// Headers: If-Match = case ETag (required), Idempotency-Key = message id (required).
/// </summary>
public sealed record ProposalWrite
{
    /// <summary>Proposed, NeedsAnalyst or Withdrawn (agents can never set Accepted/Rejected).</summary>
    public ProposalStatus Status { get; init; }

    public string? Value { get; init; }
    public double? Confidence { get; init; }
    public ConfidenceBand? ConfidenceBand { get; init; }
    public string? Quote { get; init; }
    public string? DocumentId { get; init; }
    public int? Page { get; init; }
    public required string Agent { get; init; }
    public required string PromptVersion { get; init; }
    public required string ModelVersion { get; init; }
    public bool VerifierPassed { get; init; }
    public bool DependsOnUnconfirmed { get; init; }
    public string? WithdrawnReason { get; init; }
    public string? NeedsAnalystReason { get; init; }

    /// <summary>The parent question whose final answer caused this write (withdrawal or re-proposal).</summary>
    public string? CausedBy { get; init; }

    public bool Shadow { get; init; }
}

/// <summary>Response of a proposal write.</summary>
public sealed record ProposalWriteResponse(Proposal Proposal, string CaseETag, bool Duplicate);

/// <summary>
/// POST /api/cases/{caseId}/answers/{questionId} (Case.Review). Header If-Match = case ETag (required).
/// accept: no value needed; edit: value + reason required; reject: reason required.
/// </summary>
public sealed record AnswerRequest(string? Action, string? Value, string? Reason);

/// <summary>POST /dev/faults (Development only). Null fields are left unchanged.</summary>
public sealed record FaultsRequest(bool? AiBackendDown, bool? CaseAppDown);

/// <summary>POST /dev/clock (Development only): from now on the app clock reads <paramref name="At"/> and keeps ticking.</summary>
public sealed record DevClockRequest(DateTimeOffset At);
