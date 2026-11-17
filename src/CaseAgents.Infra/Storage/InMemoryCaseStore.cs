using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;

namespace CaseAgents.Infra.Storage;

/// <summary>
/// Local, process-wide store that mimics the Cosmos container semantics: one partition per case, a new ETag and
/// AuditSeq + 1 on every write, If-Match checks, and all-or-nothing writes (one lock), with deterministic ids.
/// The caller's copy of the case is used only for its ETag: the new case item is always built from the stored one.
/// Items are immutable records, so returning them hands out copies.
/// </summary>
public sealed class InMemoryCaseStore(TimeProvider time) : ICaseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Partition> _partitions = new(StringComparer.Ordinal);

    public Task<CaseItem> CreateCaseAsync(CaseItem item, AuditDraft audit, CancellationToken ct = default)
    {
        ItemIdRules.Validate(item.CaseId, "case id");

        lock (_gate)
        {
            if (_partitions.ContainsKey(item.CaseId))
                throw new CaseConflictException($"Case '{item.CaseId}' already exists.");

            var stored = item with { Id = item.CaseId, Type = ItemTypes.Case, AuditSeq = 1, ETag = NewETag() };
            var partition = new Partition(stored);
            partition.Audit.Add(NewEntry(stored, audit));
            _partitions[item.CaseId] = partition;
            return Task.FromResult(stored);
        }
    }

    public Task<CaseItem> AddDocumentAsync(CaseItem current, CaseDocument document, AuditDraft audit, CancellationToken ct = default)
    {
        ItemIdRules.Validate(current.CaseId, "case id");
        ItemIdRules.Validate(document.DocumentId, "document id");

        lock (_gate)
        {
            var partition = GetForUpdate(current);
            if (partition.Documents.Any(d => d.DocumentId == document.DocumentId))
                throw new CaseConflictException($"Case '{current.CaseId}' already has a document '{document.DocumentId}'.");

            var next = Bump(partition.Case);
            partition.Documents.Add(document with
            {
                Id = ItemIds.Document(current.CaseId, document.DocumentId),
                CaseId = current.CaseId,
                Type = ItemTypes.Document,
            });
            Commit(partition, next, audit);
            return Task.FromResult(next);
        }
    }

    public Task<CaseItem> UpdateCaseAsync(CaseItem current, CaseItem updated, AuditDraft audit, CancellationToken ct = default)
    {
        ItemIdRules.Validate(current.CaseId, "case id");

        lock (_gate)
        {
            var partition = GetForUpdate(current);
            var next = Bump(partition.Case) with { Status = updated.Status, AiLocked = updated.AiLocked, LockedBy = updated.LockedBy };
            Commit(partition, next, audit);
            return Task.FromResult(next);
        }
    }

    public Task<WriteResult> WriteProposalAsync(CaseItem current, Proposal proposal, AuditDraft audit, string? idempotencyKey, CancellationToken ct = default)
    {
        ItemIdRules.Validate(current.CaseId, "case id");
        ItemIdRules.Validate(proposal.QuestionId, "question id");
        if (idempotencyKey is not null)
            ItemIdRules.Validate(idempotencyKey, "idempotency key");

        lock (_gate)
        {
            if (idempotencyKey is not null
                && _partitions.TryGetValue(current.CaseId, out var existing)
                && existing.Messages.Contains(idempotencyKey))
            {
                return Task.FromResult(new WriteResult(existing.Case, true));
            }

            var partition = GetForUpdate(current);
            var next = Bump(partition.Case);
            partition.Proposals[proposal.QuestionId] = proposal with
            {
                Id = ItemIds.Proposal(current.CaseId, proposal.QuestionId),
                CaseId = current.CaseId,
                Type = ItemTypes.Proposal,
            };
            if (idempotencyKey is not null)
                partition.Messages.Add(idempotencyKey);
            Commit(partition, next, audit);
            return Task.FromResult(new WriteResult(next, false));
        }
    }

    public Task<CaseItem> WriteDecisionAsync(CaseItem current, FinalAnswer? answer, Proposal? updatedProposal, AuditDraft audit, CancellationToken ct = default)
    {
        if (answer is null && updatedProposal is null)
            throw new ArgumentException("A decision needs a final answer, an updated proposal, or both.");
        ItemIdRules.Validate(current.CaseId, "case id");
        if (answer is not null)
            ItemIdRules.Validate(answer.QuestionId, "question id");
        if (updatedProposal is not null)
            ItemIdRules.Validate(updatedProposal.QuestionId, "question id");

        lock (_gate)
        {
            var partition = GetForUpdate(current);
            var next = Bump(partition.Case);
            if (answer is not null)
            {
                partition.Answers[answer.QuestionId] = answer with
                {
                    Id = ItemIds.Answer(current.CaseId, answer.QuestionId),
                    CaseId = current.CaseId,
                    Type = ItemTypes.Answer,
                };
            }

            if (updatedProposal is not null)
            {
                partition.Proposals[updatedProposal.QuestionId] = updatedProposal with
                {
                    Id = ItemIds.Proposal(current.CaseId, updatedProposal.QuestionId),
                    CaseId = current.CaseId,
                    Type = ItemTypes.Proposal,
                };
            }

            Commit(partition, next, audit);
            return Task.FromResult(next);
        }
    }

    public Task<CaseItem?> GetCaseAsync(string caseId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_partitions.TryGetValue(caseId, out var p) ? p.Case : null);
    }

    public Task<CaseRecord?> GetCaseRecordAsync(string caseId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_partitions.TryGetValue(caseId, out var p) ? p.ToRecord() : null);
    }

    public Task<IReadOnlyList<CaseItem>> ListCasesAsync(string? submittedBy = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<CaseItem> result = _partitions.Values
                .Select(p => p.Case)
                .Where(c => submittedBy is null || string.Equals(c.SubmittedBy, submittedBy, StringComparison.Ordinal))
                .OrderByDescending(c => c.CreatedAt)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<CaseRecord>> ListCaseRecordsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<CaseRecord> result = _partitions.Values
                .OrderBy(p => p.Case.CreatedAt)
                .Select(p => p.ToRecord())
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string caseId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<AuditEntry> result = _partitions.TryGetValue(caseId, out var p)
                ? p.Audit.OrderBy(a => a.Seq).ToList()
                : [];
            return Task.FromResult(result);
        }
    }

    public Task<bool> IsMessageProcessedAsync(string caseId, string idempotencyKey, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_partitions.TryGetValue(caseId, out var p) && p.Messages.Contains(idempotencyKey));
    }

    /// <summary>The If-Match check: the caller's case must carry the stored ETag.</summary>
    private Partition GetForUpdate(CaseItem current)
    {
        if (!_partitions.TryGetValue(current.CaseId, out var partition))
            throw new CaseConflictException($"Case '{current.CaseId}' does not exist.");
        if (string.IsNullOrEmpty(current.ETag) || !string.Equals(partition.Case.ETag, current.ETag, StringComparison.Ordinal))
            throw new CaseConflictException($"Case '{current.CaseId}' was modified by someone else (ETag mismatch).");
        return partition;
    }

    /// <summary>The replaced CASE item: the stored one with the next AuditSeq and a new ETag.</summary>
    private static CaseItem Bump(CaseItem stored) => stored with { AuditSeq = stored.AuditSeq + 1, ETag = NewETag() };

    private void Commit(Partition partition, CaseItem next, AuditDraft audit)
    {
        partition.Audit.Add(NewEntry(next, audit));
        partition.Case = next;
    }

    private AuditEntry NewEntry(CaseItem caseItem, AuditDraft draft) => new()
    {
        Id = ItemIds.Audit(caseItem.CaseId, caseItem.AuditSeq),
        CaseId = caseItem.CaseId,
        Seq = caseItem.AuditSeq,
        At = time.GetUtcNow(),
        Actor = draft.Actor,
        Action = draft.Action,
        QuestionId = draft.QuestionId,
        Before = draft.Before,
        After = draft.After,
        Basis = draft.Basis,
        Reason = draft.Reason,
        CausedBy = draft.CausedBy,
        Proposal = draft.Proposal,
    };

    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";

    private sealed class Partition(CaseItem item)
    {
        public CaseItem Case { get; set; } = item;
        public List<CaseDocument> Documents { get; } = [];
        public Dictionary<string, FinalAnswer> Answers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Proposal> Proposals { get; } = new(StringComparer.Ordinal);
        public List<AuditEntry> Audit { get; } = [];
        public HashSet<string> Messages { get; } = new(StringComparer.Ordinal);

        public CaseRecord ToRecord() => new(
            Case,
            Documents.OrderBy(d => d.AddedAt).ToList(),
            Answers.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
            Proposals.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList());
    }
}
