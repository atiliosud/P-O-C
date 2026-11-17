using System.Net;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;
using CaseAgents.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace CaseAgents.Tests.Api;

/// <summary>The tests play the AI backend: they write proposals with an agent token scoped to the case.</summary>
public sealed class ProposalTests(NoAiBackendApi api) : IClassFixture<NoAiBackendApi>
{
    private ApiFactory Factory => api.Factory;

    private ICaseReader Reader => Factory.Services.GetRequiredService<ICaseReader>();

    [Fact]
    public async Task Proposal_write_requires_if_match_and_idempotency_key()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var noIfMatch = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), null, "m1:Q4");
        var noKey = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, null);

        Assert.Equal((HttpStatusCode)428, noIfMatch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Null((await agent.GetViewAsync(caseId)).Question("Q4").Proposal);
    }

    [Fact]
    public async Task Idempotency_key_with_a_reserved_character_returns_400_and_nothing_is_written()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var response = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, "msg/1:Q4");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await agent.GetViewAsync(caseId)).Question("Q4").Proposal);
    }

    [Fact]
    public async Task Valid_proposal_is_stored_with_evidence_and_audited()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId, "ai-backend");
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var response = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, "m1:Q4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadAsync<ProposalWriteResponse>();
        Assert.False(body.Duplicate);
        Assert.NotEqual(etag, body.CaseETag);
        Assert.Equal(ProposalStatus.Proposed, body.Proposal.Status);
        Assert.Equal(YesNo.Yes, body.Proposal.Value);
        Assert.NotNull(body.Proposal.Evidence);
        Assert.Equal(Scenario.BankLetterId, body.Proposal.Evidence!.DocumentId);
        Assert.Equal(3, body.Proposal.Evidence.Page);
        Assert.NotEqual(default, body.Proposal.CreatedAt);
        Assert.Equal(body.Proposal.CreatedAt, body.Proposal.UpdatedAt);

        var entry = (await Reader.GetAuditAsync(caseId)).Last();
        Assert.Equal(AuditActions.ProposalCreated, entry.Action);
        Assert.Equal(new AuditActor(ActorTypes.Agent, "ai-backend"), entry.Actor);
        Assert.Equal("Q4", entry.QuestionId);
        Assert.Null(entry.Before);
        Assert.Equal(YesNo.Yes, entry.After);
        Assert.Equal(Scenario.BankLetterPage3Line, entry.Basis?.Quote);
        Assert.Equal(body.Proposal.Evidence.Hash, entry.Basis?.Hash);
        Assert.Equal(AgentNames.Answerer, entry.Proposal?.Agent);
        Assert.Equal("v12", entry.Proposal?.PromptVersion);
        Assert.Equal(ConfidenceBand.High, entry.Proposal?.Band);
    }

    [Fact]
    public async Task Same_idempotency_key_twice_creates_one_proposal_and_one_audit_entry()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var first = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, "msg-7:Q4");
        var second = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, "msg-7:Q4");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var replay = await second.ReadAsync<ProposalWriteResponse>();
        Assert.True(replay.Duplicate);
        Assert.Equal(YesNo.Yes, replay.Proposal.Value);
        var audit = await Reader.GetAuditAsync(caseId);
        Assert.Single(audit, e => e.QuestionId == "Q4" && e.Action == AuditActions.ProposalCreated);
    }

    [Fact]
    public async Task Stale_version_returns_409_and_nothing_is_written()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var e1 = (await agent.GetViewAsync(caseId)).Case.ETag;
        Assert.Equal(HttpStatusCode.OK, (await agent.PutProposalAsync(caseId, "Q1", Proposals.Q1Name(), e1, "m:Q1")).StatusCode);
        var auditBefore = (await Reader.GetAuditAsync(caseId)).Count;

        var stale = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), e1, "m:Q4");

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version_changed", await stale.ErrorCodeAsync());
        Assert.Equal(auditBefore, (await Reader.GetAuditAsync(caseId)).Count);
        Assert.Null((await agent.GetViewAsync(caseId)).Question("Q4").Proposal);
        Assert.False(await Reader.IsMessageProcessedAsync(caseId, "m:Q4"));
    }

    [Fact]
    public async Task Proposal_with_quote_not_on_page_is_rejected_422()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var wrongPage = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { Page = 2 }, etag, "m:1");
        var invented = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { Quote = "The client holds an account in Zurich." }, etag, "m:2");
        var noQuote = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { Quote = null }, etag, "m:3");
        var noValue = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { Value = null }, etag, "m:4");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongPage.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invented.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noQuote.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noValue.StatusCode);
        Assert.Null((await agent.GetViewAsync(caseId)).Question("Q4").Proposal);
    }

    [Fact]
    public async Task Quote_with_other_line_breaks_is_located()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var quote = "The account held with Example Bank\n(Zurich)   was closed on 14 March 2024.";

        var response = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { Quote = quote }, etag, "m:Q4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Agents_cannot_set_accepted_or_rejected()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var accepted = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { Status = ProposalStatus.Accepted }, etag, "m:1");
        var rejected = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { Status = ProposalStatus.Rejected }, etag, "m:2");

        Assert.Equal(HttpStatusCode.BadRequest, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Needs_analyst_with_a_value_is_rejected_400_and_without_one_is_stored()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var withValue = await agent.PutProposalAsync(caseId, "Q5", Proposals.NeedsAnalyst() with { Value = "Salary" }, etag, "m:1");
        var withoutValue = await agent.PutProposalAsync(caseId, "Q5", Proposals.NeedsAnalyst(), etag, "m:2");

        Assert.Equal(HttpStatusCode.BadRequest, withValue.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withoutValue.StatusCode);
        var entry = (await Reader.GetAuditAsync(caseId)).Last();
        Assert.Equal(AuditActions.ProposalNeedsAnalyst, entry.Action);
        Assert.Equal("No passage mentions the question keywords", entry.Reason);
    }

    [Fact]
    public async Task Proposed_yes_no_value_must_be_yes_or_no()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var response = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { Value = "Maybe" }, etag, "m:1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_question_or_case_returns_404()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var unknownQuestion = await agent.PutProposalAsync(caseId, "Q99", Proposals.Q4Yes(), etag, "m:1");
        var missingCase = await Factory.Agent("missing").PutProposalAsync("missing", "Q4", Proposals.Q4Yes(), etag, "m:1");

        Assert.Equal(HttpStatusCode.NotFound, unknownQuestion.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingCase.StatusCode);
    }

    [Fact]
    public async Task Proposal_on_a_draft_case_returns_409_case_not_ready()
    {
        var caseId = await Factory.Submitter().CreateCaseWithDocumentsAsync(Scenario.Documents, ready: false);
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var response = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, "m:1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("case_not_ready", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Proposal_never_overwrites_a_final_answer()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var reviewer = Factory.Reviewer();
        var etag = (await reviewer.GetViewAsync(caseId)).Case.ETag;
        var edited = await reviewer.AnswerAsync(caseId, "Q4", "edit", YesNo.No, Scenario.AnalystReason, etag);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var fresh = (await agent.GetViewAsync(caseId)).Case.ETag;
        var response = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), fresh, "m:Q4");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("final_answer_exists", await response.ErrorCodeAsync());
        var view = await reviewer.GetViewAsync(caseId);
        Assert.Equal(YesNo.No, view.Question("Q4").Answer?.Value);
        Assert.Null(view.Question("Q4").Proposal);
    }

    [Fact]
    public async Task Proposal_after_an_analyst_rejected_it_returns_409_touched()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var reviewer = Factory.Reviewer();
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var written = await (await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, "m:1")).ReadAsync<ProposalWriteResponse>();
        Assert.Equal(HttpStatusCode.OK, (await reviewer.AnswerAsync(caseId, "Q4", "reject", null, "Quote is about a closed account", written.CaseETag)).StatusCode);

        var fresh = (await agent.GetViewAsync(caseId)).Case.ETag;
        var response = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), fresh, "m:2");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("touched", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Withdrawn_keeps_the_previous_value_and_evidence()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var written = await (await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, "m:1")).ReadAsync<ProposalWriteResponse>();
        var withdraw = new ProposalWrite
        {
            Status = ProposalStatus.Withdrawn,
            Agent = AgentNames.Answerer,
            PromptVersion = "v12",
            ModelVersion = "test-model",
            WithdrawnReason = "Parent changed",
            CausedBy = "Q3",
        };

        var response = await agent.PutProposalAsync(caseId, "Q4", withdraw, written.CaseETag, "m:withdraw:Q4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = (await response.ReadAsync<ProposalWriteResponse>()).Proposal;
        Assert.Equal(ProposalStatus.Withdrawn, proposal.Status);
        Assert.Equal(YesNo.Yes, proposal.Value);
        Assert.Equal(written.Proposal.Evidence, proposal.Evidence);
        Assert.Equal("Parent changed", proposal.WithdrawnReason);
        Assert.Equal(written.Proposal.CreatedAt, proposal.CreatedAt);
        var entry = (await Reader.GetAuditAsync(caseId)).Last();
        Assert.Equal(AuditActions.ProposalWithdrawn, entry.Action);
        Assert.Equal("Q3", entry.CausedBy);
        Assert.Equal("Parent changed", entry.Reason);
        Assert.Equal(Scenario.BankLetterPage3Line, entry.Basis?.Quote);
    }

    [Fact]
    public async Task Withdrawing_a_dependent_clears_its_depends_on_unconfirmed_flag()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var written = await (await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { DependsOnUnconfirmed = true }, etag, "m:1")).ReadAsync<ProposalWriteResponse>();
        Assert.True(written.Proposal.DependsOnUnconfirmed);

        var response = await agent.PutProposalAsync(caseId, "Q4", Withdraw("Parent confirmed as No"), written.CaseETag, "m:withdraw:Q4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = (await response.ReadAsync<ProposalWriteResponse>()).Proposal;
        Assert.Equal(ProposalStatus.Withdrawn, proposal.Status);
        Assert.False(proposal.DependsOnUnconfirmed);
    }

    [Fact]
    public async Task Withdrawing_a_proposal_that_does_not_exist_returns_409_and_stores_nothing()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        var response = await agent.PutProposalAsync(caseId, "Q6", Withdraw("nothing") with { Value = YesNo.Yes, Quote = "anything" }, etag, "m:withdraw:Q6");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("conflict", await response.ErrorCodeAsync());
        Assert.Null((await agent.GetViewAsync(caseId)).Question("Q6").Proposal);
    }

    [Theory]
    [InlineData(0.10, true)]
    [InlineData(0.93, false)]
    [InlineData(1.50, true)]
    public async Task Proposed_needs_a_band_and_a_confidence_at_or_above_the_threshold(double confidence, bool withBand)
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var write = Proposals.Q4Yes() with { Confidence = confidence, ConfidenceBand = withBand ? ConfidenceBand.High : null };

        var response = await agent.PutProposalAsync(caseId, "Q4", write, etag, "m:1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null((await agent.GetViewAsync(caseId)).Question("Q4").Proposal);
    }

    private static ProposalWrite Withdraw(string reason) => new()
    {
        Status = ProposalStatus.Withdrawn,
        Agent = AgentNames.Answerer,
        PromptVersion = "v12",
        ModelVersion = "test-model",
        WithdrawnReason = reason,
        CausedBy = "Q3",
    };

    [Fact]
    public async Task Second_proposal_on_a_question_is_an_update()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var written = await (await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { DependsOnUnconfirmed = true }, etag, "m:1")).ReadAsync<ProposalWriteResponse>();

        var response = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { CausedBy = "Q3" }, written.CaseETag, "m:confirm:Q4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = (await Reader.GetAuditAsync(caseId)).Last();
        Assert.Equal(AuditActions.ProposalUpdated, entry.Action);
        Assert.Equal(YesNo.Yes, entry.Before);
        Assert.Equal("Q3", entry.CausedBy);
    }

    [Fact]
    public async Task Shadow_proposals_are_visible_to_agents_and_eval_only()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var reviewer = Factory.Reviewer();
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;

        Assert.Equal(HttpStatusCode.OK, (await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes() with { Shadow = true }, etag, "m:1")).StatusCode);

        Assert.True((await agent.GetViewAsync(caseId)).Question("Q4").Proposal?.Shadow);
        Assert.Null((await reviewer.GetViewAsync(caseId)).Question("Q4").Proposal);
        var records = await (await reviewer.GetAsync("/api/eval/records")).ReadAsync<List<CaseRecord>>();
        Assert.Contains(records.Single(r => r.Case.CaseId == caseId).Proposals, p => p.QuestionId == "Q4" && p.Shadow);
    }

    [Fact]
    public async Task Proposal_from_the_wrong_page_can_be_stored_as_needs_analyst_without_evidence()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var write = Proposals.NeedsAnalyst("Quote not found on the cited page") with
        {
            Quote = Scenario.BankLetterPage3Line,
            DocumentId = Scenario.BankLetterId,
            Page = 1,
        };

        var response = await agent.PutProposalAsync(caseId, "Q4", write, etag, "m:1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = (await response.ReadAsync<ProposalWriteResponse>()).Proposal;
        Assert.Null(proposal.Evidence);
        Assert.Null(proposal.Value);
        Assert.Equal(1, proposal.Page);
    }
}
