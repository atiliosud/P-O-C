using System.Net;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace CaseAgents.Tests.Api;

public sealed class TakeOverTests(NoAiBackendApi api) : IClassFixture<NoAiBackendApi>
{
    private ApiFactory Factory => api.Factory;

    [Fact]
    public async Task Take_over_blocks_agent_writes_and_bumps_version_so_in_flight_write_fails()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var reviewer = Factory.Reviewer();

        // The agent read the case (E1) and is about to write.
        var e1 = (await agent.GetViewAsync(caseId)).Case.ETag;

        var takeover = await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/takeover", etag: e1);
        Assert.Equal(HttpStatusCode.OK, takeover.StatusCode);
        var locked = await takeover.ReadAsync<CaseItem>();
        Assert.True(locked.AiLocked);
        Assert.Equal(Scenario.Analyst, locked.LockedBy);
        var e2 = locked.ETag;
        Assert.NotEqual(e1, e2);

        var inFlight = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), e1, "m1:Q4");
        Assert.Equal(HttpStatusCode.Conflict, inFlight.StatusCode);
        Assert.Equal("version_changed", await inFlight.ErrorCodeAsync());

        var afterReRead = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), e2, "m2:Q4");
        Assert.Equal(HttpStatusCode.Conflict, afterReRead.StatusCode);
        Assert.Equal("ai_locked", await afterReRead.ErrorCodeAsync());

        var release = await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/release", etag: e2);
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        var released = await release.ReadAsync<CaseItem>();
        Assert.False(released.AiLocked);
        Assert.Null(released.LockedBy);

        var write = await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), released.ETag, "m3:Q4");
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal(ProposalStatus.Proposed, (await reviewer.GetViewAsync(caseId)).Question("Q4").Proposal?.Status);
    }

    [Fact]
    public async Task Take_over_and_release_are_audited_and_published()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var reviewer = Factory.Reviewer();
        var etag = (await reviewer.GetViewAsync(caseId)).Case.ETag;

        var locked = await (await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/takeover", etag: etag)).ReadAsync<CaseItem>();
        await (await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/release", etag: locked.ETag)).ReadAsync<CaseItem>();

        var audit = await Factory.Services.GetRequiredService<ICaseReader>().GetAuditAsync(caseId);
        Assert.Equal([AuditActions.CaseTakenOver, AuditActions.CaseReleased], audit.TakeLast(2).Select(e => e.Action));
        Assert.All(audit.TakeLast(2), e => Assert.Equal(new AuditActor(ActorTypes.Person, Scenario.Analyst), e.Actor));
        await api.Events.WaitForAsync(e => e.CaseId == caseId && e.EventType == CaseEventTypes.CaseTakenOver);
        await api.Events.WaitForAsync(e => e.CaseId == caseId && e.EventType == CaseEventTypes.CaseReleased);
    }

    [Fact]
    public async Task Take_over_twice_or_release_when_not_locked_returns_409()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var reviewer = Factory.Reviewer();
        var etag = (await reviewer.GetViewAsync(caseId)).Case.ETag;

        var notLocked = await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/release", etag: etag);
        var locked = await (await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/takeover", etag: etag)).ReadAsync<CaseItem>();
        var twice = await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/takeover", etag: locked.ETag);
        var stale = await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/release", etag: etag);
        var noIfMatch = await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/release");

        Assert.Equal(HttpStatusCode.Conflict, notLocked.StatusCode);
        Assert.Equal("ai_locked", await twice.ErrorCodeAsync());
        Assert.Equal("version_changed", await stale.ErrorCodeAsync());
        Assert.Equal((HttpStatusCode)428, noIfMatch.StatusCode);
    }

    [Fact]
    public async Task Analysts_can_decide_while_the_case_is_taken_over()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var reviewer = Factory.Reviewer();
        var etag = (await reviewer.GetViewAsync(caseId)).Case.ETag;
        var locked = await (await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/takeover", etag: etag)).ReadAsync<CaseItem>();

        var response = await reviewer.AnswerAsync(caseId, "Q1", AnswerActions.Edit, "Maria Example", "ID card", locked.ETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
