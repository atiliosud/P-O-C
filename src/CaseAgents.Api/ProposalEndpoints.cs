using System.Security.Claims;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Core.Security;
using CaseAgents.Infra.Auth;

namespace CaseAgents.Api;

/// <summary>
/// PUT /api/cases/{caseId}/proposals/{questionId}: the ONLY write the AI backend (Case.Agent, scoped to the case) can make.
/// A proposal never overwrites a person's value, and a quote must really be on the cited page.
/// </summary>
public static class ProposalEndpoints
{
    public const int MaxIdempotencyKeyLength = 200;

    public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/cases/{caseId}/proposals/{questionId}", PutProposalAsync).RequireAuthorization(AuthPolicies.Agent);
        return app;
    }

    private static async Task<IResult> PutProposalAsync(
        string caseId,
        string questionId,
        ProposalWrite? write,
        HttpRequest http,
        HttpResponse response,
        ClaimsPrincipal principal,
        ICaseStore store,
        Questionnaire q,
        TimeProvider time,
        CancellationToken ct)
    {
        var user = principal.ToUserContext();
        if (!CaseAccess.CanWriteProposal(user, caseId))
            return Results.NotFound();
        var record = await store.GetCaseRecordAsync(caseId, ct);
        if (record is null)
            return Results.NotFound();

        var ifMatch = http.IfMatch();
        if (ifMatch is null)
            return ApiResults.PreconditionRequired();
        var key = http.IdempotencyKey();
        if (key is null || key.Length > MaxIdempotencyKeyLength)
            return ApiResults.BadRequest($"The Idempotency-Key header is required (at most {MaxIdempotencyKeyLength} characters).");

        // A repeated message changes nothing and gets the current proposal back.
        if (await store.IsMessageProcessedAsync(caseId, key, ct))
            return Duplicate(response, record, questionId);

        var question = q.Find(questionId);
        if (question is null)
            return Results.NotFound();

        var error = Validate(write, question);
        if (error is not null)
            return ApiResults.BadRequest(error);

        // Version first: a write prepared on an old read (e.g. before a take over) is told to re-read.
        if (ifMatch != record.Case.ETag)
            return ApiResults.VersionChanged();
        if (record.Case.Status != CaseStatus.Ready)
            return ApiResults.Conflict(ConflictCodes.CaseNotReady, $"The case is {record.Case.Status}, not Ready.");
        if (record.Case.AiLocked)
            return ApiResults.Conflict(ConflictCodes.AiLocked, $"The case is taken over by {record.Case.LockedBy}.");
        if (record.Answers.Any(a => a.QuestionId == questionId))
            return ApiResults.Conflict(ConflictCodes.FinalAnswerExists, $"{questionId} already has a final answer; agents never overwrite it.");

        var existing = record.Proposals.FirstOrDefault(p => p.QuestionId == questionId);
        if (existing is { Status: ProposalStatus.Accepted or ProposalStatus.Rejected })
            return ApiResults.Conflict(ConflictCodes.Touched, $"An analyst already decided on the {questionId} proposal.");

        var now = time.GetUtcNow();
        Proposal proposal;
        if (write!.Status == ProposalStatus.Proposed)
        {
            if (string.IsNullOrWhiteSpace(write.Value) || string.IsNullOrWhiteSpace(write.Quote) || write.DocumentId is null || write.Page is null)
                return ApiResults.Unprocessable("A Proposed answer needs a value, a quote, a documentId and a page.");
            if (write.ConfidenceBand is null || write.Confidence is not { } confidence || !(confidence >= q.ProposalThreshold && confidence <= 1))
                return ApiResults.Unprocessable($"A Proposed answer needs a confidence band and a confidence between {q.ProposalThreshold} and 1; below the threshold write NeedsAnalyst.");
            var evidence = EvidenceLocator.Locate(record.Documents, write.DocumentId, write.Page.Value, write.Quote);
            if (evidence is null)
                return ApiResults.Unprocessable($"The quote is not on page {write.Page} of document '{write.DocumentId}'.");
            proposal = FromWrite(caseId, questionId, write, evidence, existing, now);
        }
        else if (write.Status == ProposalStatus.NeedsAnalyst)
        {
            var evidence = write is { Quote: not null, DocumentId: not null, Page: not null }
                ? EvidenceLocator.Locate(record.Documents, write.DocumentId, write.Page.Value, write.Quote)
                : null;
            proposal = FromWrite(caseId, questionId, write, evidence, existing, now);
        }
        else
        {
            // Withdrawn keeps the previous value and evidence; only the status and the reason change.
            // There is nothing to withdraw without a proposal (this also stops a value being stored without evidence).
            if (existing is null)
                return ApiResults.Conflict(ConflictCodes.Conflict, $"{questionId} has no proposal to withdraw.");
            // A withdrawn proposal no longer waits for its parent, so it loses the "depends on, not confirmed yet" flag.
            proposal = existing with
            {
                Status = ProposalStatus.Withdrawn,
                WithdrawnReason = write.WithdrawnReason,
                DependsOnUnconfirmed = false,
                UpdatedAt = now,
            };
        }

        var audit = new AuditDraft
        {
            Actor = AuditDrafts.Agent(user),
            Action = AuditAction(write.Status, existing),
            QuestionId = questionId,
            Before = existing?.Value,
            After = proposal.Value,
            Basis = AuditDrafts.Basis(proposal),
            Reason = write.Status switch
            {
                ProposalStatus.Withdrawn => proposal.WithdrawnReason,
                ProposalStatus.NeedsAnalyst => proposal.NeedsAnalystReason,
                _ => null,
            },
            CausedBy = write.CausedBy,
            Proposal = AuditDrafts.Snapshot(proposal),
        };

        WriteResult result;
        try
        {
            result = await store.WriteProposalAsync(record.Case with { ETag = ifMatch }, proposal, audit, key, ct);
        }
        catch (CaseConflictException)
        {
            return ApiResults.VersionChanged();
        }
        catch (ArgumentException ex)
        {
            // The store refuses empty ids or ids with '/', '\', '?' or '#'.
            return ApiResults.BadRequest(ex.Message);
        }

        if (result.Duplicate)
        {
            var current = await store.GetCaseRecordAsync(caseId, ct);
            return current is null ? Results.NotFound() : Duplicate(response, current, questionId);
        }

        response.SetETag(result.Case.ETag);
        return Results.Ok(new ProposalWriteResponse(proposal, result.Case.ETag!, Duplicate: false));
    }

    /// <summary>Returns an error message, or null when the body is acceptable.</summary>
    private static string? Validate(ProposalWrite? write, Question question)
    {
        if (write is null)
            return "A proposal body is required.";
        if (write.Status is ProposalStatus.Accepted or ProposalStatus.Rejected)
            return "Agents can only write Proposed, NeedsAnalyst or Withdrawn; Accepted and Rejected are set by people.";
        if (write.Status == ProposalStatus.NeedsAnalyst && write.Value is not null)
            return "A NeedsAnalyst proposal never carries a value.";
        if (write.Status == ProposalStatus.Proposed
            && question.Type == QuestionTypes.YesNo
            && write.Value is not null
            && write.Value.Trim() is not (YesNo.Yes or YesNo.No))
            return $"{question.Id} is a yes_no question: the value must be {YesNo.Yes} or {YesNo.No}.";
        if (string.IsNullOrWhiteSpace(write.Agent) || string.IsNullOrWhiteSpace(write.PromptVersion) || string.IsNullOrWhiteSpace(write.ModelVersion))
            return "agent, promptVersion and modelVersion are required.";
        return null;
    }

    private static Proposal FromWrite(string caseId, string questionId, ProposalWrite w, EvidenceRef? evidence, Proposal? existing, DateTimeOffset now) => new()
    {
        Id = ItemIds.Proposal(caseId, questionId),
        CaseId = caseId,
        QuestionId = questionId,
        Value = w.Value?.Trim(),
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
        CreatedAt = existing?.CreatedAt ?? now,
        UpdatedAt = now,
    };

    private static string AuditAction(ProposalStatus status, Proposal? existing) => status switch
    {
        ProposalStatus.Withdrawn => AuditActions.ProposalWithdrawn,
        ProposalStatus.NeedsAnalyst => AuditActions.ProposalNeedsAnalyst,
        _ when existing is null || existing.Status == ProposalStatus.Withdrawn => AuditActions.ProposalCreated,
        _ => AuditActions.ProposalUpdated,
    };

    private static IResult Duplicate(HttpResponse response, CaseRecord record, string questionId)
    {
        var proposal = record.Proposals.FirstOrDefault(p => p.QuestionId == questionId);
        if (proposal is null)
            return ApiResults.Conflict(ConflictCodes.Conflict, "This Idempotency-Key was already used for another write.");

        response.SetETag(record.Case.ETag);
        return Results.Ok(new ProposalWriteResponse(proposal, record.Case.ETag!, Duplicate: true));
    }
}
