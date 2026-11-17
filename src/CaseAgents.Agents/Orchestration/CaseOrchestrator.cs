using System.Collections.Concurrent;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using Microsoft.Extensions.Logging;

namespace CaseAgents.Agents;

/// <summary>
/// The fixed workflow of the AI backend for one case: read the case through the Case API, pick the next question,
/// answer it from the case's own documents, check the quote and ask the verifier, and write a proposal with
/// If-Match + Idempotency-Key. Events are triggers: every step re-reads the case, so handling is idempotent.
/// It never forces a write: on 409 it re-reads and decides again.
/// </summary>
public sealed class CaseOrchestrator(
    ICaseAppClient app,
    IAnswerer answerer,
    IVerifier verifier,
    Questionnaire q,
    Outbox outbox,
    AiBackendOptions o,
    ILogger<CaseOrchestrator> log) : ICaseEventHandler
{
    private readonly ConcurrentDictionary<string, int> inProgress = new(StringComparer.Ordinal);

    /// <summary>True while an event or run for this case is being processed.</summary>
    public bool IsInProgress(string caseId) => inProgress.TryGetValue(caseId, out var count) && count > 0;

    public async Task HandleAsync(CaseEvent evt, CancellationToken ct)
    {
        if (evt.EventType is not (CaseEventTypes.CaseReady or CaseEventTypes.CaseReleased or CaseEventTypes.AnswerConfirmed))
            return;

        Enter(evt.CaseId);
        try
        {
            // Every event re-checks the dependents of every confirmed parent first. Events are only triggers, so an
            // answer.confirmed that was lost, skipped or handled while the case was taken over is caught up by the next one.
            var reachable = await RecheckAllParentsAsync(evt.CaseId, evt.MessageId, ct)
                && await RunCoreAsync(evt.CaseId, evt.MessageId, ct);

            if (!reachable)
                QueueEvent(evt);
        }
        finally
        {
            Exit(evt.CaseId);
        }
    }

    /// <summary>Proposes answers for the open questions of the case, one at a time, in questionnaire order.</summary>
    public async Task RunAsync(string caseId, string triggerMessageId, CancellationToken ct)
    {
        Enter(caseId);
        try
        {
            if (!await RunCoreAsync(caseId, triggerMessageId, ct))
                QueueEvent(new CaseEvent(triggerMessageId, caseId, 0, CaseEventTypes.CaseReady, DateTimeOffset.UtcNow));
        }
        finally
        {
            Exit(caseId);
        }
    }

    /// <summary>After a final answer on <paramref name="parentId"/>: withdraw or confirm the open proposals of its dependents.</summary>
    public async Task RecheckDependentsAsync(string caseId, string parentId, string triggerMessageId, CancellationToken ct)
    {
        Enter(caseId);
        try
        {
            if (!await RecheckCoreAsync(caseId, parentId, triggerMessageId, ct))
                QueueEvent(new CaseEvent(triggerMessageId, caseId, 0, CaseEventTypes.AnswerConfirmed, DateTimeOffset.UtcNow, parentId));
        }
        finally
        {
            Exit(caseId);
        }
    }

    /// <summary>Returns false when the case app could not be read (the caller queues the event in the outbox).</summary>
    private async Task<bool> RunCoreAsync(string caseId, string triggerMessageId, CancellationToken ct)
    {
        var maxIterations = q.Questions.Count * 3 + 5;
        var conflicts = 0;
        for (var i = 0; i < maxIterations; i++)
        {
            var read = await app.GetCaseAsync(caseId, ct);
            if (read.Outcome == CaseAppOutcome.Unavailable)
                return false;
            if (read.Value is not { } view)
            {
                log.LogWarning("Case {CaseId}: cannot read the case ({Outcome}: {Error}).", caseId, read.Outcome, read.Error);
                return true;
            }

            if (view.Case.Status != CaseStatus.Ready || view.Case.AiLocked)
                return true;

            var (answers, proposals) = Split(view);
            var next = QuestionRules.NextToPropose(q, answers, proposals);
            if (next is null)
                return true;

            var write = await BuildProposalAsync(next, view.Documents, answers, ct);
            var ifMatch = view.Case.ETag ?? "";
            var key = $"{triggerMessageId}:{next.Id}";
            var put = await app.PutProposalAsync(caseId, next.Id, write, ifMatch, key, ct);
            switch (put.Outcome)
            {
                case CaseAppOutcome.Ok when put.Value?.Duplicate == true:
                    // This trigger already wrote this question (a replay, or a question that became open again).
                    // Writing again would change nothing, so stop instead of looping on the same question.
                    log.LogInformation("Case {CaseId} {QuestionId}: key {Key} already used; stopping this run.", caseId, next.Id, key);
                    return true;
                case CaseAppOutcome.Ok:
                    conflicts = 0;
                    break;
                case CaseAppOutcome.Conflict:
                    log.LogInformation("Case {CaseId} {QuestionId}: 409 {Code}; re-reading.", caseId, next.Id, put.Code);
                    if (++conflicts >= o.MaxConflictRetries)
                    {
                        log.LogWarning("Case {CaseId}: giving up after {Conflicts} consecutive conflicts.", caseId, conflicts);
                        return true;
                    }

                    break;
                case CaseAppOutcome.Unavailable:
                    outbox.Add(new OutboxEntry(NewId(), caseId, triggerMessageId, next.Id, write, ifMatch, key, null));
                    log.LogWarning("Case {CaseId} {QuestionId}: case app unavailable; proposal kept in the outbox.", caseId, next.Id);
                    return true;
                default:
                    log.LogError("Case {CaseId} {QuestionId}: proposal refused ({Outcome}: {Error}).", caseId, next.Id, put.Outcome, put.Error);
                    return true;
            }
        }

        log.LogWarning("Case {CaseId}: stopped after {Iterations} iterations.", caseId, maxIterations);
        return true;
    }

    /// <summary>Returns false when the case app was unavailable (the caller queues the event; keys make the replay safe).</summary>
    private async Task<bool> RecheckCoreAsync(string caseId, string parentId, string triggerMessageId, CancellationToken ct)
    {
        var maxIterations = q.DependentsOf(parentId).Count + o.MaxConflictRetries + 1;
        var conflicts = 0;
        for (var i = 0; i < maxIterations; i++)
        {
            var read = await app.GetCaseAsync(caseId, ct);
            if (read.Outcome == CaseAppOutcome.Unavailable)
                return false;
            if (read.Value is not { } view)
            {
                log.LogWarning("Case {CaseId}: cannot read the case ({Outcome}: {Error}).", caseId, read.Outcome, read.Error);
                return true;
            }

            if (view.Case.Status != CaseStatus.Ready || view.Case.AiLocked)
                return true;

            var (answers, proposals) = Split(view);
            var action = QuestionRules.DependentActions(q, parentId, answers, proposals).FirstOrDefault();
            if (action is null)
                return true;

            var questionId = action.Question.Id;
            var (write, key) = action.Kind == DependentActionKind.Withdraw
                ? (ToWrite(action.Current) with { Status = ProposalStatus.Withdrawn, WithdrawnReason = action.Reason, CausedBy = parentId },
                    $"{triggerMessageId}:withdraw:{questionId}")
                : (ToWrite(action.Current) with { DependsOnUnconfirmed = false, CausedBy = parentId },
                    $"{triggerMessageId}:confirm:{questionId}");

            var put = await app.PutProposalAsync(caseId, questionId, write, view.Case.ETag ?? "", key, ct);
            switch (put.Outcome)
            {
                case CaseAppOutcome.Ok:
                    conflicts = 0;
                    break;
                case CaseAppOutcome.Conflict:
                    if (++conflicts >= o.MaxConflictRetries)
                    {
                        log.LogWarning("Case {CaseId}: giving up the {ParentId} recheck after {Conflicts} conflicts.", caseId, parentId, conflicts);
                        return true;
                    }

                    break;
                case CaseAppOutcome.Unavailable:
                    return false;
                default:
                    log.LogError("Case {CaseId} {QuestionId}: write refused ({Outcome}: {Error}).", caseId, questionId, put.Outcome, put.Error);
                    return true;
            }
        }

        return true;
    }

    private async Task<bool> RecheckAllParentsAsync(string caseId, string triggerMessageId, CancellationToken ct)
    {
        foreach (var parent in q.Questions.Where(question => q.DependentsOf(question.Id).Count > 0))
        {
            if (!await RecheckCoreAsync(caseId, parent.Id, triggerMessageId, ct))
                return false;
        }

        return true;
    }

    private async Task<ProposalWrite> BuildProposalAsync(
        Question question, IReadOnlyList<CaseDocument> documents, IReadOnlyList<FinalAnswer> answers, CancellationToken ct)
    {
        // Extractor: passages from THIS case's documents only.
        var passages = CaseIndex.Build(documents).FindPassages(question.EvidenceKeywords);
        var draft = await answerer.AnswerAsync(question, passages, ct);

        EvidenceRef? evidence = null;
        if (draft.SchemaValid && !string.IsNullOrWhiteSpace(draft.Quote) && draft.DocumentId is not null && draft.Page is not null)
            evidence = EvidenceLocator.Locate(documents, draft.DocumentId, draft.Page.Value, draft.Quote);

        VerifierResult? verdict = null;
        if (evidence is not null && !string.IsNullOrWhiteSpace(draft.Value))
            verdict = await verifier.VerifyAsync(question, draft.Value, draft.Quote!, CitedPage(documents, evidence), ct);

        var decision = ProposalPolicy.Decide(q, question, draft, evidence, verdict);
        return new ProposalWrite
        {
            Status = decision.Status,
            Value = decision.Value,
            Confidence = decision.Confidence,
            ConfidenceBand = decision.Band,
            Quote = decision.Quote,
            DocumentId = decision.DocumentId,
            Page = decision.Page,
            Agent = AgentNames.Answerer,
            PromptVersion = q.PromptVersion,
            ModelVersion = draft.ModelVersion,
            VerifierPassed = decision.VerifierPassed,
            DependsOnUnconfirmed = QuestionRules.GetApplicability(q, question, answers) == Applicability.ParentUnconfirmed,
            NeedsAnalystReason = decision.NeedsAnalystReason,
            Shadow = o.ShadowMode,
        };
    }

    private static Passage CitedPage(IReadOnlyList<CaseDocument> documents, EvidenceRef evidence)
    {
        var document = documents.First(d => d.DocumentId == evidence.DocumentId);
        var page = document.Pages.First(p => p.N == evidence.Page);
        return new Passage(document.DocumentId, document.Name, page.N, page.Text);
    }

    /// <summary>The current proposal as a write (used to withdraw or confirm it without changing its values).</summary>
    private static ProposalWrite ToWrite(Proposal p) => new()
    {
        Status = p.Status,
        Value = p.Value,
        Confidence = p.Confidence,
        ConfidenceBand = p.ConfidenceBand,
        Quote = p.Quote,
        DocumentId = p.DocumentId,
        Page = p.Page,
        Agent = p.Agent,
        PromptVersion = p.PromptVersion,
        ModelVersion = p.ModelVersion,
        VerifierPassed = p.VerifierPassed,
        DependsOnUnconfirmed = p.DependsOnUnconfirmed,
        WithdrawnReason = p.WithdrawnReason,
        NeedsAnalystReason = p.NeedsAnalystReason,
        Shadow = p.Shadow,
    };

    private static (IReadOnlyList<FinalAnswer> Answers, IReadOnlyList<Proposal> Proposals) Split(CaseView view) =>
        (view.Questions.Where(s => s.Answer is not null).Select(s => s.Answer!).ToList(),
         view.Questions.Where(s => s.Proposal is not null).Select(s => s.Proposal!).ToList());

    private void QueueEvent(CaseEvent evt)
    {
        outbox.Add(new OutboxEntry(NewId(), evt.CaseId, evt.MessageId, evt.QuestionId, null, null, null, evt));
        log.LogWarning("Case {CaseId}: case app unavailable; event {EventType} kept in the outbox.", evt.CaseId, evt.EventType);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private void Enter(string caseId) => inProgress.AddOrUpdate(caseId, 1, (_, count) => count + 1);

    private void Exit(string caseId) => inProgress.AddOrUpdate(caseId, 0, (_, count) => Math.Max(0, count - 1));
}
