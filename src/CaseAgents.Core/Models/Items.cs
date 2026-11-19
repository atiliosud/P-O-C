using System.Text.Json.Serialization;

namespace CaseAgents.Core.Models;

/// <summary>
/// CASE item (owned by the case application). <c>id</c> equals <c>caseId</c>; <c>_etag</c> is the case version.
/// EVERY write in the case partition replaces this item, so any write changes the version.
/// </summary>
public sealed record CaseItem
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public string Type { get; init; } = ItemTypes.Case;
    public required string Title { get; init; }
    public required string SubmittedBy { get; init; }
    public CaseStatus Status { get; init; } = CaseStatus.Draft;

    /// <summary>Set by "Take over": the AI backend may not write while true.</summary>
    public bool AiLocked { get; init; }

    public string? LockedBy { get; init; }

    /// <summary>Last audit sequence number used in this case (the next entry gets AuditSeq + 1).</summary>
    public long AuditSeq { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("_etag")]
    public string? ETag { get; init; }
}

/// <param name="N">1-based page number.</param>
/// <param name="Text">Text of the page, as already extracted (stands in for Document Intelligence output).</param>
public sealed record DocumentPage(int N, string Text);

/// <summary>DOCUMENT item: a supporting document of the case, as text per page.</summary>
public sealed record CaseDocument
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public string Type { get; init; } = ItemTypes.Document;
    public required string DocumentId { get; init; }
    public required string Name { get; init; }

    /// <summary>Document kind matched against Questionnaire.RequiredDocuments (e.g. "bank_letter").</summary>
    public required string Kind { get; init; }

    public required IReadOnlyList<DocumentPage> Pages { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

/// <summary>ANSWER item: the FINAL value of a question. Only people write it (id = "{caseId}:answer:{questionId}").</summary>
public sealed record FinalAnswer
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public string Type { get; init; } = ItemTypes.Answer;
    public required string QuestionId { get; init; }
    public required string Value { get; init; }

    /// <summary>The person (user id from the token) who set the value.</summary>
    public required string SetBy { get; init; }

    public DateTimeOffset SetAt { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Where a quote sits in a document: references and a hash, no raw personal data.</summary>
/// <param name="Offset">Index of the quote in the whitespace-normalised page text.</param>
/// <param name="Length">Length of the normalised quote.</param>
/// <param name="Hash">"sha256:" + lowercase hex SHA-256 of the normalised quote (UTF-8).</param>
public sealed record EvidenceRef(string DocumentId, int Page, int Offset, int Length, string Hash);

/// <summary>
/// PROPOSAL item, written only by the AI backend (id = "{caseId}:proposal:{questionId}", one per question, replaced on change).
/// A proposal never overwrites a final answer. NeedsAnalyst proposals have no Value.
/// </summary>
public sealed record Proposal
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public string Type { get; init; } = ItemTypes.Proposal;
    public required string QuestionId { get; init; }
    public string? Value { get; init; }

    /// <summary>yes_no: p = exp(logprob) of the answer token. free_text: the verifier score.</summary>
    public double? Confidence { get; init; }

    public ConfidenceBand? ConfidenceBand { get; init; }
    public string? Quote { get; init; }
    public string? DocumentId { get; init; }
    public int? Page { get; init; }
    public EvidenceRef? Evidence { get; init; }

    /// <summary>Logical agent role that produced it, e.g. "Answerer".</summary>
    public required string Agent { get; init; }

    public required string PromptVersion { get; init; }
    public required string ModelVersion { get; init; }
    public bool VerifierPassed { get; init; }
    public ProposalStatus Status { get; init; }

    /// <summary>True when the parent question has no final answer yet.</summary>
    public bool DependsOnUnconfirmed { get; init; }

    public string? WithdrawnReason { get; init; }

    /// <summary>Why the proposal needs an analyst (no quote, quote not found, verifier rejected, low confidence, invalid output).</summary>
    public string? NeedsAnalystReason { get; init; }

    /// <summary>Shadow mode: written for evaluation only and never shown to analysts.</summary>
    public bool Shadow { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record AuditActor(string Type, string Id);

/// <summary>Evidence of an audited value: references + hash, plus the quote shown in the audit view.</summary>
public sealed record AuditBasis(string DocumentId, int Page, int Offset, int Length, string Hash, string Quote);

/// <summary>What the agent proposed, captured in the audit entry at the time of the action.</summary>
public sealed record ProposalSnapshot(
    string? Value,
    double? Confidence,
    ConfidenceBand? Band,
    string Agent,
    string PromptVersion,
    string ModelVersion,
    bool VerifierPassed,
    ProposalStatus Status,
    bool DependsOnUnconfirmed);

/// <summary>
/// AUDIT item: append-only (id = "{caseId}:audit:{seq:D6}"). There is no update or delete anywhere in the code.
/// </summary>
public sealed record AuditEntry
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public string Type { get; init; } = ItemTypes.Audit;
    public long Seq { get; init; }
    public DateTimeOffset At { get; init; }
    public required AuditActor Actor { get; init; }

    /// <summary>One of <see cref="AuditActions"/>.</summary>
    public required string Action { get; init; }

    public string? QuestionId { get; init; }

    /// <summary>
    /// The value the action replaced. On answer_confirmed: the previous final answer when the analyst corrects one,
    /// otherwise the proposed value the analyst decided on.
    /// </summary>
    public string? Before { get; init; }

    public string? After { get; init; }
    public AuditBasis? Basis { get; init; }
    public string? Reason { get; init; }

    /// <summary>The question whose final answer caused this entry (e.g. "Q4" on the Q4.1 withdrawal).</summary>
    public string? CausedBy { get; init; }

    public ProposalSnapshot? Proposal { get; init; }
}

/// <summary>What a writer supplies; the store assigns id, seq and time inside the same atomic write.</summary>
public sealed record AuditDraft
{
    public required AuditActor Actor { get; init; }
    public required string Action { get; init; }
    public string? QuestionId { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public AuditBasis? Basis { get; init; }
    public string? Reason { get; init; }
    public string? CausedBy { get; init; }
    public ProposalSnapshot? Proposal { get; init; }
}

/// <summary>Every item of one case except the audit trail, read from one partition.</summary>
public sealed record CaseRecord(
    CaseItem Case,
    IReadOnlyList<CaseDocument> Documents,
    IReadOnlyList<FinalAnswer> Answers,
    IReadOnlyList<Proposal> Proposals);

/// <param name="Case">The case after the write (new ETag), or the current case when Duplicate.</param>
/// <param name="Duplicate">True when the Idempotency-Key was already processed: nothing was written.</param>
public sealed record WriteResult(CaseItem Case, bool Duplicate);

/// <summary>
/// Thrown by the store when a write loses a race (the case ETag changed) or breaks a uniqueness rule.
/// </summary>
public sealed class CaseConflictException(string message, Exception? inner = null) : Exception(message, inner);
