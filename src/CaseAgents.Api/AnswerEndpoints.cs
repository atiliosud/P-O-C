using System.Security.Claims;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Core.Security;
using CaseAgents.Infra.Auth;

namespace CaseAgents.Api;

/// <summary>
/// POST /api/cases/{caseId}/answers/{questionId}: analysts accept, edit or reject. Only people set final values.
/// Analysts can decide while the case is taken over.
/// </summary>
public static class AnswerEndpoints
{
    public const int MaxValueLength = 2000;
    public const int MaxReasonLength = 1000;

    public static IEndpointRouteBuilder MapAnswerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cases/{caseId}/answers/{questionId}", AnswerAsync).RequireAuthorization(AuthPolicies.Review);
        return app;
    }

    private static async Task<IResult> AnswerAsync(
        string caseId,
        string questionId,
        AnswerRequest? request,
        HttpRequest http,
        HttpResponse response,
        ClaimsPrincipal principal,
        ICaseStore store,
        IEventBus bus,
        Questionnaire q,
        CaseViews views,
        TimeProvider time,
        CancellationToken ct)
    {
        var user = principal.ToUserContext();
        var ifMatch = http.IfMatch();
        if (ifMatch is null)
            return ApiResults.PreconditionRequired();

        var action = request?.Action?.Trim().ToLowerInvariant();
        var value = string.IsNullOrWhiteSpace(request?.Value) ? null : request.Value.Trim();
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim();
        var error = Validate(action, value, reason);
        if (error is not null)
            return ApiResults.BadRequest(error);

        var record = await store.GetCaseRecordAsync(caseId, ct);
        var question = q.Find(questionId);
        if (record is null || question is null)
            return Results.NotFound();

        if (action == AnswerActions.Edit && question.Type == QuestionTypes.YesNo && value is not (YesNo.Yes or YesNo.No))
            return ApiResults.BadRequest($"{questionId} is a yes_no question: the value must be {YesNo.Yes} or {YesNo.No}.");

        if (record.Case.Status != CaseStatus.Ready)
            return ApiResults.Conflict(ConflictCodes.CaseNotReady, $"The case is {record.Case.Status}, not Ready.");
        if (QuestionRules.GetApplicability(q, question, record.Answers) == Applicability.NotApplicable)
            return ApiResults.Conflict(ConflictCodes.NotApplicable, $"{questionId} does not apply given the answer to {question.DependsOn?.QuestionId}.");
        if (ifMatch != record.Case.ETag)
            return ApiResults.VersionChanged();

        // Analysts only see (and decide on) non-shadow proposals.
        var proposal = record.Proposals.FirstOrDefault(p => p.QuestionId == questionId && !p.Shadow);
        var now = time.GetUtcNow();
        FinalAnswer? answer = null;
        Proposal? updatedProposal;
        AuditDraft audit;

        if (action == AnswerActions.Reject)
        {
            if (proposal is not { Status: ProposalStatus.Proposed or ProposalStatus.NeedsAnalyst })
                return ApiResults.Conflict(ConflictCodes.Conflict, $"{questionId} has no open proposal to reject.");

            updatedProposal = proposal with { Status = ProposalStatus.Rejected, UpdatedAt = now };
            audit = new AuditDraft
            {
                Actor = AuditDrafts.Person(user),
                Action = AuditActions.ProposalRejected,
                QuestionId = questionId,
                Before = proposal.Value,
                Basis = AuditDrafts.Basis(proposal),
                Reason = reason,
                Proposal = AuditDrafts.Snapshot(updatedProposal),
            };
        }
        else
        {
            string finalValue;
            if (action == AnswerActions.Accept)
            {
                if (proposal is not { Status: ProposalStatus.Proposed, Value.Length: > 0 })
                    return ApiResults.Conflict(ConflictCodes.Conflict, $"{questionId} has no proposed value to accept.");
                finalValue = proposal.Value;
                updatedProposal = proposal with { Status = ProposalStatus.Accepted, UpdatedAt = now };
            }
            else
            {
                finalValue = value!;
                updatedProposal = proposal is { Status: ProposalStatus.Proposed or ProposalStatus.NeedsAnalyst }
                    ? proposal with
                    {
                        Status = string.Equals(proposal.Value, finalValue, StringComparison.Ordinal) ? ProposalStatus.Accepted : ProposalStatus.Rejected,
                        UpdatedAt = now,
                    }
                    : null;
            }

            answer = new FinalAnswer
            {
                Id = ItemIds.Answer(caseId, questionId),
                CaseId = caseId,
                QuestionId = questionId,
                Value = finalValue,
                SetBy = user.UserId,
                SetAt = now,
                Reason = reason,
            };
            audit = new AuditDraft
            {
                Actor = AuditDrafts.Person(user),
                Action = AuditActions.AnswerConfirmed,
                QuestionId = questionId,
                Before = proposal?.Value,
                After = finalValue,
                Basis = AuditDrafts.Basis(proposal),
                Reason = reason,
                Proposal = AuditDrafts.Snapshot(updatedProposal ?? proposal),
            };
        }

        try
        {
            await store.WriteDecisionAsync(record.Case with { ETag = ifMatch }, answer, updatedProposal, audit, ct);
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

        // Events are published only after the write succeeded.
        if (answer is not null)
            await bus.PublishAsync(caseId, CaseEventTypes.AnswerConfirmed, questionId, answer.Value, ct);
        else
            await bus.PublishAsync(caseId, CaseEventTypes.ProposalRejected, questionId, ct: ct);

        var updated = await store.GetCaseRecordAsync(caseId, ct);
        if (updated is null)
            return Results.NotFound();

        response.SetETag(updated.Case.ETag);
        return Results.Ok(views.Build(updated, user));
    }

    /// <summary>Returns an error message, or null when the request is valid for its action.</summary>
    private static string? Validate(string? action, string? value, string? reason)
    {
        if (action is not (AnswerActions.Accept or AnswerActions.Edit or AnswerActions.Reject))
            return $"action must be '{AnswerActions.Accept}', '{AnswerActions.Edit}' or '{AnswerActions.Reject}'.";
        if (action == AnswerActions.Edit && (value is null || reason is null))
            return "edit needs a value and a reason.";
        if (action == AnswerActions.Reject && reason is null)
            return "reject needs a reason.";
        if (value is { Length: > MaxValueLength })
            return $"value is at most {MaxValueLength} characters.";
        if (reason is { Length: > MaxReasonLength })
            return $"reason is at most {MaxReasonLength} characters.";
        return null;
    }
}
