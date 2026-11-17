using CaseAgents.Core.Models;

namespace CaseAgents.Core.Abstractions;

/// <summary>Read-only access to cases. The MCP server depends only on this interface.</summary>
public interface ICaseReader
{
    Task<CaseItem?> GetCaseAsync(string caseId, CancellationToken ct = default);

    /// <summary>Case, documents, final answers and proposals (all of them, including shadow), or null.</summary>
    Task<CaseRecord?> GetCaseRecordAsync(string caseId, CancellationToken ct = default);

    /// <summary>Cases, newest first; only those submitted by <paramref name="submittedBy"/> when it is not null.</summary>
    Task<IReadOnlyList<CaseItem>> ListCasesAsync(string? submittedBy = null, CancellationToken ct = default);

    /// <summary>Every case record, oldest first (input for the Eval).</summary>
    Task<IReadOnlyList<CaseRecord>> ListCaseRecordsAsync(CancellationToken ct = default);

    /// <summary>The audit trail of a case, ordered by Seq. Empty when the case does not exist.</summary>
    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string caseId, CancellationToken ct = default);

    /// <summary>True when a write with this Idempotency-Key was already applied to the case.</summary>
    Task<bool> IsMessageProcessedAsync(string caseId, string idempotencyKey, CancellationToken ct = default);
}

/// <summary>
/// Writes. Each method is ONE atomic unit inside the case partition (a TransactionalBatch in Cosmos DB):
/// it replaces the CASE item if-match <c>current.ETag</c> (so the version always changes and AuditSeq advances),
/// writes its item(s), and appends exactly one audit entry built from the <see cref="AuditDraft"/>
/// (Id = ItemIds.Audit(caseId, AuditSeq + 1), At = store clock). On ETag mismatch: <see cref="CaseConflictException"/>.
/// The audit trail can only be appended to: there is no method that updates or deletes an audit entry.
/// </summary>
public interface ICaseStore : ICaseReader
{
    /// <summary>Creates the case (Draft unless set) with audit seq 1. Throws CaseConflictException if the id exists.</summary>
    Task<CaseItem> CreateCaseAsync(CaseItem item, AuditDraft audit, CancellationToken ct = default);

    /// <summary>Adds a document. Throws CaseConflictException if a document with the same DocumentId exists.</summary>
    Task<CaseItem> AddDocumentAsync(CaseItem current, CaseDocument document, AuditDraft audit, CancellationToken ct = default);

    /// <summary>Replaces case fields (Status, AiLocked, LockedBy); id, caseId, title, submittedBy and createdAt are kept from current.</summary>
    Task<CaseItem> UpdateCaseAsync(CaseItem current, CaseItem updated, AuditDraft audit, CancellationToken ct = default);

    /// <summary>
    /// Upserts the proposal of proposal.QuestionId. When <paramref name="idempotencyKey"/> was already processed for this
    /// case, writes nothing and returns Duplicate = true (checked BEFORE the ETag). Otherwise records the key in the same unit.
    /// </summary>
    Task<WriteResult> WriteProposalAsync(CaseItem current, Proposal proposal, AuditDraft audit, string? idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// Writes (upserts) the final answer when <paramref name="answer"/> is not null, and replaces the proposal when
    /// <paramref name="updatedProposal"/> is not null, in one unit. At least one of them must be given.
    /// </summary>
    Task<CaseItem> WriteDecisionAsync(CaseItem current, FinalAnswer? answer, Proposal? updatedProposal, AuditDraft audit, CancellationToken ct = default);
}

/// <summary>Tells the case app whether the AI backend still has work queued for a case (drives the "AI pending" banner).</summary>
public interface IAiBackendStatus
{
    /// <summary>The AI backend has work for this case that is not finished (processing, or waiting in its outbox).</summary>
    bool IsPending(string caseId);

    /// <summary>Writes waiting in the AI backend's outbox (all cases).</summary>
    int OutboxCount { get; }
}
