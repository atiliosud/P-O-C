using System.Net;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace CaseAgents.Tests.Api;

public sealed class AnswerTests(NoAiBackendApi api) : IClassFixture<NoAiBackendApi>
{
    private ApiFactory Factory => api.Factory;

    private ICaseReader Reader => Factory.Services.GetRequiredService<ICaseReader>();

    /// <summary>A ready scenario case where the "agent" has proposed Q4 = Yes. Returns the case id and the current ETag.</summary>
    private async Task<(string CaseId, string ETag)> CaseWithQ4ProposalAsync()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var written = await (await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, "m:Q4")).ReadAsync<ProposalWriteResponse>();
        return (caseId, written.CaseETag);
    }

    [Fact]
    public async Task Accept_marks_proposal_accepted_and_publishes_answer_confirmed()
    {
        var (caseId, etag) = await CaseWithQ4ProposalAsync();

        var response = await Factory.Reviewer().AnswerAsync(caseId, "Q4", AnswerActions.Accept, null, null, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.ReadAsync<CaseView>();
        Assert.Equal(view.Case.ETag, response.Headers.ETag?.Tag);
        var q4 = view.Question("Q4");
        Assert.Equal(YesNo.Yes, q4.Answer?.Value);
        Assert.Equal(Scenario.Analyst, q4.Answer?.SetBy);
        Assert.NotEqual(default, q4.Answer!.SetAt);
        Assert.Equal(ProposalStatus.Accepted, q4.Proposal?.Status);

        var entry = (await Reader.GetAuditAsync(caseId)).Last();
        Assert.Equal(AuditActions.AnswerConfirmed, entry.Action);
        Assert.Equal(new AuditActor(ActorTypes.Person, Scenario.Analyst), entry.Actor);
        Assert.Equal(YesNo.Yes, entry.Before);
        Assert.Equal(YesNo.Yes, entry.After);
        Assert.Equal(Scenario.BankLetterPage3Line, entry.Basis?.Quote);
        Assert.NotNull(entry.Proposal);

        var evt = await api.Events.WaitForAsync(e => e.CaseId == caseId && e.EventType == CaseEventTypes.AnswerConfirmed);
        Assert.Equal("Q4", evt.QuestionId);
        Assert.Equal(YesNo.Yes, evt.Value);
    }

    [Fact]
    public async Task Edit_requires_reason_and_needs_analyst_value_is_not_prefilled()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var reviewer = Factory.Reviewer();
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var written = await (await agent.PutProposalAsync(caseId, "Q5", Proposals.NeedsAnalyst(), etag, "m:Q5")).ReadAsync<ProposalWriteResponse>();

        var before = await reviewer.GetViewAsync(caseId);
        Assert.Equal(ProposalStatus.NeedsAnalyst, before.Question("Q5").Proposal?.Status);
        Assert.Null(before.Question("Q5").Proposal?.Value);
        Assert.Null(before.Question("Q5").Answer);

        var noReason = await reviewer.AnswerAsync(caseId, "Q5", AnswerActions.Edit, "Employment income", null, written.CaseETag);
        var noValue = await reviewer.AnswerAsync(caseId, "Q5", AnswerActions.Edit, null, "From the salary statement", written.CaseETag);
        var ok = await reviewer.AnswerAsync(caseId, "Q5", AnswerActions.Edit, "Employment income", "From the salary statement", written.CaseETag);

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noValue.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var q5 = (await ok.ReadAsync<CaseView>()).Question("Q5");
        Assert.Equal("Employment income", q5.Answer?.Value);
        Assert.Equal("From the salary statement", q5.Answer?.Reason);
        Assert.Equal(ProposalStatus.Rejected, q5.Proposal?.Status);
    }

    [Fact]
    public async Task Edit_to_another_value_rejects_the_proposal_and_publishes_the_new_value()
    {
        var (caseId, etag) = await CaseWithQ4ProposalAsync();

        var response = await Factory.Reviewer().AnswerAsync(caseId, "Q4", AnswerActions.Edit, YesNo.No, Scenario.AnalystReason, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var q4 = (await response.ReadAsync<CaseView>()).Question("Q4");
        Assert.Equal(YesNo.No, q4.Answer?.Value);
        Assert.Equal(Scenario.AnalystReason, q4.Answer?.Reason);
        Assert.Equal(ProposalStatus.Rejected, q4.Proposal?.Status);
        var entry = (await Reader.GetAuditAsync(caseId)).Last();
        Assert.Equal(AuditActions.AnswerConfirmed, entry.Action);
        Assert.Equal(YesNo.Yes, entry.Before);
        Assert.Equal(YesNo.No, entry.After);
        Assert.Equal(Scenario.AnalystReason, entry.Reason);
        var evt = await api.Events.WaitForAsync(e => e.CaseId == caseId && e.EventType == CaseEventTypes.AnswerConfirmed);
        Assert.Equal(YesNo.No, evt.Value);
    }

    [Fact]
    public async Task Edit_with_the_proposed_value_accepts_the_proposal()
    {
        var (caseId, etag) = await CaseWithQ4ProposalAsync();

        var response = await Factory.Reviewer().AnswerAsync(caseId, "Q4", AnswerActions.Edit, YesNo.Yes, "Checked the letter", etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ProposalStatus.Accepted, (await response.ReadAsync<CaseView>()).Question("Q4").Proposal?.Status);
    }

    [Fact]
    public async Task Reject_needs_a_reason_marks_the_proposal_rejected_and_sets_no_value()
    {
        var (caseId, etag) = await CaseWithQ4ProposalAsync();
        var reviewer = Factory.Reviewer();

        var noReason = await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Reject, null, " ", etag);
        var ok = await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Reject, null, "Quote is about a closed account", etag);

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var q4 = (await ok.ReadAsync<CaseView>()).Question("Q4");
        Assert.Null(q4.Answer);
        Assert.Equal(ProposalStatus.Rejected, q4.Proposal?.Status);
        var entry = (await Reader.GetAuditAsync(caseId)).Last();
        Assert.Equal(AuditActions.ProposalRejected, entry.Action);
        Assert.Equal("Quote is about a closed account", entry.Reason);
        var evt = await api.Events.WaitForAsync(e => e.CaseId == caseId && e.EventType == CaseEventTypes.ProposalRejected);
        Assert.Equal("Q4", evt.QuestionId);
    }

    [Fact]
    public async Task Reject_without_an_open_proposal_returns_409_conflict()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var etag = (await Factory.Reviewer().GetViewAsync(caseId)).Case.ETag;

        var response = await Factory.Reviewer().AnswerAsync(caseId, "Q4", AnswerActions.Reject, null, "No", etag);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("conflict", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Accept_without_a_proposed_value_returns_409_conflict()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var reviewer = Factory.Reviewer();
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var noProposal = await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Accept, null, null, etag);
        var written = await (await agent.PutProposalAsync(caseId, "Q5", Proposals.NeedsAnalyst(), etag, "m:Q5")).ReadAsync<ProposalWriteResponse>();
        var needsAnalyst = await reviewer.AnswerAsync(caseId, "Q5", AnswerActions.Accept, null, null, written.CaseETag);
        var shadow = await (await agent.PutProposalAsync(caseId, "Q1", Proposals.Q1Name() with { Shadow = true }, written.CaseETag, "m:Q1")).ReadAsync<ProposalWriteResponse>();
        var shadowAccept = await reviewer.AnswerAsync(caseId, "Q1", AnswerActions.Accept, null, null, shadow.CaseETag);

        Assert.Equal("conflict", await noProposal.ErrorCodeAsync());
        Assert.Equal("conflict", await needsAnalyst.ErrorCodeAsync());
        Assert.Equal("conflict", await shadowAccept.ErrorCodeAsync());
    }

    [Fact]
    public async Task Answer_validation_errors()
    {
        var (caseId, etag) = await CaseWithQ4ProposalAsync();
        var reviewer = Factory.Reviewer();

        Assert.Equal((HttpStatusCode)428, (await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Accept, null, null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.AnswerAsync(caseId, "Q4", "approve", null, null, etag)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Edit, "Maybe", "unsure", etag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reviewer.AnswerAsync(caseId, "Q99", AnswerActions.Edit, "x", "y", etag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reviewer.AnswerAsync("missing", "Q4", AnswerActions.Edit, "Yes", "y", etag)).StatusCode);
        Assert.Null((await reviewer.GetViewAsync(caseId)).Question("Q4").Answer);
    }

    [Fact]
    public async Task Answer_with_a_stale_version_returns_409_version_changed()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var reviewer = Factory.Reviewer();
        var e1 = (await reviewer.GetViewAsync(caseId)).Case.ETag;
        Assert.Equal(HttpStatusCode.OK, (await reviewer.AnswerAsync(caseId, "Q1", AnswerActions.Edit, "Maria Example", "ID card", e1)).StatusCode);

        var stale = await reviewer.AnswerAsync(caseId, "Q2", AnswerActions.Edit, "Portugal", "Utility bill", e1);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version_changed", await stale.ErrorCodeAsync());
        Assert.Null((await reviewer.GetViewAsync(caseId)).Question("Q2").Answer);
    }

    [Fact]
    public async Task Answer_on_a_draft_case_returns_409_case_not_ready()
    {
        var caseId = await Factory.Submitter().CreateCaseWithDocumentsAsync(Scenario.Documents, ready: false);
        var etag = (await Factory.Reviewer().GetViewAsync(caseId)).Case.ETag;

        var response = await Factory.Reviewer().AnswerAsync(caseId, "Q1", AnswerActions.Edit, "Maria Example", "ID card", etag);

        Assert.Equal("case_not_ready", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Answer_on_a_question_that_does_not_apply_returns_409_not_applicable()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var reviewer = Factory.Reviewer();
        var etag = (await reviewer.GetViewAsync(caseId)).Case.ETag;
        var afterQ4 = await (await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Edit, YesNo.No, Scenario.AnalystReason, etag)).ReadAsync<CaseView>();

        var response = await reviewer.AnswerAsync(caseId, "Q4.1", AnswerActions.Edit, "Switzerland", "Bank letter", afterQ4.Case.ETag);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_applicable", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Analyst_can_change_a_final_answer_later()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var reviewer = Factory.Reviewer();
        var etag = (await reviewer.GetViewAsync(caseId)).Case.ETag;
        var first = await (await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Edit, YesNo.No, Scenario.AnalystReason, etag)).ReadAsync<CaseView>();

        var second = await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Edit, YesNo.Yes, "Second account found", first.Case.ETag);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(YesNo.Yes, (await second.ReadAsync<CaseView>()).Question("Q4").Answer?.Value);
    }
}
