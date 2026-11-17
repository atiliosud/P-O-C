using CaseAgents.Agents;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;

namespace CaseAgents.Tests.Agents;

/// <summary>One PUT the orchestrator attempted, with what the fake answered.</summary>
internal sealed record PutCall(string CaseId, string QuestionId, ProposalWrite Write, string IfMatch, string IdempotencyKey, CaseAppOutcome Outcome, string? Code);

/// <summary>
/// In-memory stand-in for the Case API as the AI backend sees it. Checks If-Match and Idempotency-Key like the API:
/// a processed key returns Duplicate (before the version check), a stale If-Match returns 409 version_changed,
/// locked/closed cases and final answers return 409, and a Proposed write must carry a quote found on the cited page.
/// </summary>
internal sealed class FakeCaseApp(Questionnaire q) : ICaseAppClient
{
    private readonly object gate = new();
    private readonly Dictionary<string, CaseRecord> cases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> processedKeys = new(StringComparer.Ordinal);
    private int version = 1;
    private TaskCompletionSource? rendezvous;
    private int rendezvousParties;
    private int rendezvousArrived;

    public List<PutCall> Puts { get; } = [];
    public int AppliedWrites { get; private set; }
    public int DuplicateKeys { get; private set; }
    public int Reads { get; private set; }

    /// <summary>The case app is down: every call returns Unavailable.</summary>
    public bool Down { get; set; }

    /// <summary>Reads work but writes return Unavailable (the app went down between read and write).</summary>
    public bool WritesDown { get; set; }

    /// <summary>The next write is applied but the response is lost (the client sees Unavailable).</summary>
    public bool LoseNextWriteResponse { get; set; }

    /// <summary>Runs before a write is checked (simulates an analyst acting while the agent's write is in flight).</summary>
    public Action<string, string>? BeforeWrite { get; set; }

    public void Add(CaseRecord record)
    {
        lock (gate)
            cases[record.Case.CaseId] = record;
    }

    public CaseRecord Record(string caseId)
    {
        lock (gate)
            return cases[caseId];
    }

    public Proposal? Proposal(string caseId, string questionId) =>
        Record(caseId).Proposals.FirstOrDefault(p => p.QuestionId == questionId);

    public string ETag(string caseId) => Record(caseId).Case.ETag!;

    /// <summary>An analyst sets a final value (bumps the case version).</summary>
    public void Confirm(string caseId, string questionId, string value)
    {
        lock (gate)
        {
            var record = cases[caseId];
            var answer = new FinalAnswer
            {
                Id = ItemIds.Answer(caseId, questionId),
                CaseId = caseId,
                QuestionId = questionId,
                Value = value,
                SetBy = "A. Silva",
            };
            cases[caseId] = Bump(record with { Answers = record.Answers.Where(a => a.QuestionId != questionId).Append(answer).ToList() });
        }
    }

    public void TakeOver(string caseId) => SetLock(caseId, true);

    public void Release(string caseId) => SetLock(caseId, false);

    /// <summary>
    /// The first <paramref name="parties"/> writes wait for each other. Each run reads the case just before it writes,
    /// so concurrent runs propose from the same version and pick the same question.
    /// </summary>
    public void HoldFirstWrites(int parties)
    {
        lock (gate)
        {
            rendezvousParties = parties;
            rendezvousArrived = 0;
            rendezvous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task<CaseAppResult<CaseView>> GetCaseAsync(string caseId, CancellationToken ct)
    {
        await Task.Yield();
        lock (gate)
        {
            Reads++;
            if (Down)
                return new CaseAppResult<CaseView>(CaseAppOutcome.Unavailable, null, "down", null);
            if (!cases.TryGetValue(caseId, out var record))
                return new CaseAppResult<CaseView>(CaseAppOutcome.NotFound, null, "not found", null);

            // Agents see shadow proposals too (the API includes them for Case.Agent callers).
            return new CaseAppResult<CaseView>(CaseAppOutcome.Ok, CaseViewBuilder.Build(q, record, aiPending: false, includeShadow: true), null, null);
        }
    }

    public async Task<CaseAppResult<ProposalWriteResponse>> PutProposalAsync(
        string caseId, string questionId, ProposalWrite write, string ifMatch, string idempotencyKey, CancellationToken ct)
    {
        await Task.Yield();
        TaskCompletionSource? wait = null;
        lock (gate)
        {
            if (rendezvous is not null)
            {
                wait = rendezvous;
                if (++rendezvousArrived >= rendezvousParties)
                {
                    rendezvous.SetResult();
                    rendezvous = null;
                }
            }
        }

        if (wait is not null)
            await wait.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        lock (gate)
        {
            BeforeWrite?.Invoke(caseId, questionId);
            var result = Apply(caseId, questionId, write, ifMatch, idempotencyKey);
            if (result.Outcome == CaseAppOutcome.Ok && !result.Value!.Duplicate && LoseNextWriteResponse)
            {
                LoseNextWriteResponse = false;
                result = Fail(CaseAppOutcome.Unavailable, "response lost", null);
            }

            Puts.Add(new PutCall(caseId, questionId, write, ifMatch, idempotencyKey, result.Outcome, result.Code));
            return result;
        }
    }

    private CaseAppResult<ProposalWriteResponse> Apply(string caseId, string questionId, ProposalWrite write, string ifMatch, string key)
    {
        if (Down || WritesDown)
            return Fail(CaseAppOutcome.Unavailable, "down", null);
        if (!cases.TryGetValue(caseId, out var record))
            return Fail(CaseAppOutcome.NotFound, "case not found", null);
        if (string.IsNullOrEmpty(ifMatch) || string.IsNullOrEmpty(key))
            return Fail(CaseAppOutcome.Rejected, "If-Match and Idempotency-Key are required", null);

        var existing = record.Proposals.FirstOrDefault(p => p.QuestionId == questionId);
        if (processedKeys.TryGetValue(caseId, out var keys) && keys.Contains(key))
        {
            DuplicateKeys++;
            return new(CaseAppOutcome.Ok, new ProposalWriteResponse(existing ?? ToProposal(caseId, questionId, write, null, null), record.Case.ETag!, true), null, null);
        }

        if (q.Find(questionId) is null)
            return Fail(CaseAppOutcome.NotFound, "unknown question", null);
        if (write.Status is ProposalStatus.Accepted or ProposalStatus.Rejected)
            return Fail(CaseAppOutcome.Rejected, "agents cannot set Accepted/Rejected", null);
        if (write.Status == ProposalStatus.NeedsAnalyst && write.Value is not null)
            return Fail(CaseAppOutcome.Rejected, "NeedsAnalyst carries no value", null);
        if (ifMatch != record.Case.ETag)
            return Fail(CaseAppOutcome.Conflict, "version changed", "version_changed");
        if (record.Case.Status != CaseStatus.Ready)
            return Fail(CaseAppOutcome.Conflict, "case not ready", "case_not_ready");
        if (record.Case.AiLocked)
            return Fail(CaseAppOutcome.Conflict, "ai locked", "ai_locked");
        if (record.Answers.Any(a => a.QuestionId == questionId))
            return Fail(CaseAppOutcome.Conflict, "final answer exists", "final_answer_exists");
        if (existing is { Status: ProposalStatus.Accepted or ProposalStatus.Rejected })
            return Fail(CaseAppOutcome.Conflict, "touched", "touched");

        EvidenceRef? evidence = existing?.Evidence;
        if (write.Status == ProposalStatus.Proposed)
        {
            if (write.Value is null || write.Quote is null || write.DocumentId is null || write.Page is null)
                return Fail(CaseAppOutcome.Rejected, "Proposed needs value, quote, document and page", null);
            evidence = EvidenceLocator.Locate(record.Documents, write.DocumentId, write.Page.Value, write.Quote);
            if (evidence is null)
                return Fail(CaseAppOutcome.Rejected, "quote not on page", null);
        }
        else if (write.Status == ProposalStatus.NeedsAnalyst)
        {
            evidence = write.Quote is not null && write.DocumentId is not null && write.Page is not null
                ? EvidenceLocator.Locate(record.Documents, write.DocumentId, write.Page.Value, write.Quote)
                : null;
        }

        var proposal = ToProposal(caseId, questionId, write, evidence, existing);
        var updated = Bump(record with { Proposals = record.Proposals.Where(p => p.QuestionId != questionId).Append(proposal).ToList() });
        cases[caseId] = updated;
        if (!processedKeys.TryGetValue(caseId, out keys))
            processedKeys[caseId] = keys = new HashSet<string>(StringComparer.Ordinal);
        keys.Add(key);
        AppliedWrites++;
        return new(CaseAppOutcome.Ok, new ProposalWriteResponse(proposal, updated.Case.ETag!, false), null, null);
    }

    private static Proposal ToProposal(string caseId, string questionId, ProposalWrite w, EvidenceRef? evidence, Proposal? existing) => new()
    {
        Id = ItemIds.Proposal(caseId, questionId),
        CaseId = caseId,
        QuestionId = questionId,
        Value = w.Value,
        Confidence = w.Confidence,
        ConfidenceBand = w.ConfidenceBand,
        Quote = w.Quote,
        DocumentId = w.DocumentId,
        Page = w.Page,
        Evidence = evidence,
        Agent = w.Agent,
        PromptVersion = w.PromptVersion,
        ModelVersion = w.ModelVersion,
        VerifierPassed = w.VerifierPassed,
        Status = w.Status,
        DependsOnUnconfirmed = w.DependsOnUnconfirmed,
        WithdrawnReason = w.WithdrawnReason,
        NeedsAnalystReason = w.NeedsAnalystReason,
        Shadow = w.Shadow,
        CreatedAt = existing?.CreatedAt ?? default,
    };

    private void SetLock(string caseId, bool locked)
    {
        lock (gate)
        {
            var record = cases[caseId];
            cases[caseId] = Bump(record with { Case = record.Case with { AiLocked = locked, LockedBy = locked ? "A. Silva" : null } });
        }
    }

    private CaseRecord Bump(CaseRecord record) => record with { Case = record.Case with { ETag = $"\"v{++version}\"" } };

    private static CaseAppResult<ProposalWriteResponse> Fail(CaseAppOutcome outcome, string error, string? code) => new(outcome, null, error, code);
}
