using System.Net;
using System.Net.Http.Json;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Core.Operations;
using CaseAgents.Tests.TestSupport;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaseAgents.Tests.Api;

public sealed class OpsAndAuditTests(NoAiBackendApi api) : IClassFixture<NoAiBackendApi>
{
    private ApiFactory Factory => api.Factory;

    [Fact]
    public void Audit_has_no_update_or_delete_endpoints()
    {
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var auditRoutes = endpoints
            .Where(e => e.RoutePattern.RawText?.Contains("audit", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        var methods = auditRoutes
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
            .ToList();

        Assert.NotEmpty(auditRoutes);
        Assert.All(auditRoutes, e => Assert.Equal("/api/cases/{caseId}/audit", e.RoutePattern.RawText));
        Assert.Equal(["GET"], methods.Distinct());
    }

    [Fact]
    public async Task Audit_view_has_rows_and_entries_in_order()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var reviewer = Factory.Reviewer();
        var etag = (await agent.GetViewAsync(caseId)).Case.ETag;
        var written = await (await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), etag, "m:Q4")).ReadAsync<ProposalWriteResponse>();
        await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Edit, YesNo.No, Scenario.AnalystReason, written.CaseETag);

        var response = await reviewer.GetAsync($"/api/cases/{caseId}/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = await response.ReadAsync<AuditView>();
        var row = Assert.Single(audit.Rows);
        Assert.Equal("Q4", row.QuestionId);
        Assert.Equal(YesNo.Yes, row.ProposedValue);
        Assert.Equal(YesNo.No, row.FinalValue);
        Assert.Equal(Scenario.Analyst, row.FinalBy);
        Assert.Equal(Scenario.AnalystReason, row.Reason);
        Assert.Equal(audit.Entries.Select(e => e.Seq).Order(), audit.Entries.Select(e => e.Seq));
        Assert.Equal(AuditActions.CaseCreated, audit.Entries[0].Action);
    }

    [Fact]
    public async Task Ops_status_reports_bus_outbox_and_faults()
    {
        var response = await Factory.Reviewer().GetAsync("/api/ops/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.ReadAsync<OpsBody>();
        Assert.NotNull(status.Bus);
        Assert.Equal(0, status.OutboxCount);
        Assert.Equal(new FaultSnapshot(false, false), status.Faults);
    }

    [Fact]
    public async Task Case_app_down_returns_503_to_agents_only()
    {
        using var factory = new ApiFactory(aiBackend: false);
        var caseId = await factory.Submitter().CreateScenarioCaseAsync();
        var anonymous = factory.CreateClient();
        var set = await anonymous.PostAsJsonAsync("/dev/faults", new { caseAppDown = true });
        Assert.Equal(new FaultSnapshot(false, true), await set.ReadAsync<FaultSnapshot>());

        var agent = factory.Agent(caseId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await agent.GetAsync($"/api/cases/{caseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await agent.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), "\"x\"", "m:1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.Reviewer().GetAsync($"/api/cases/{caseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.Submitter().GetAsync($"/api/cases/{caseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);

        await anonymous.PostAsJsonAsync("/dev/faults", new { caseAppDown = false });
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"/api/cases/{caseId}")).StatusCode);
    }

    [Fact]
    public async Task Faults_endpoint_changes_only_the_given_fields()
    {
        using var factory = new ApiFactory(aiBackend: false);
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/dev/faults", new { aiBackendDown = true });
        var after = await (await client.PostAsJsonAsync("/dev/faults", new { caseAppDown = true })).ReadAsync<FaultSnapshot>();
        var read = await (await client.GetAsync("/dev/faults")).ReadAsync<FaultSnapshot>();

        Assert.Equal(new FaultSnapshot(true, true), after);
        Assert.Equal(after, read);
    }

    [Fact]
    public async Task Ai_backend_down_shows_ai_pending_and_analysts_keep_working()
    {
        var events = new RecordingEventHandler();
        using var factory = new ApiFactory(aiBackend: false, configureServices: s => s.AddSingleton<ICaseEventHandler>(events));
        var faults = factory.Services.GetRequiredService<FaultState>();
        faults.AiBackendDown = true;
        var reviewer = factory.Reviewer();

        var caseId = await factory.Submitter().CreateScenarioCaseAsync();
        var view = await reviewer.GetViewAsync(caseId);
        Assert.True(view.AiPending);

        var answered = await reviewer.AnswerAsync(caseId, "Q1", AnswerActions.Edit, "Maria Example", "ID card", view.Case.ETag);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.True((await answered.ReadAsync<CaseView>()).AiPending);
        Assert.Empty(events.Events);

        faults.AiBackendDown = false;
        await events.WaitForAsync(e => e.CaseId == caseId && e.EventType == CaseEventTypes.CaseReady);
        await reviewer.WaitUntilAsync(caseId, v => !v.AiPending);
    }

    [Fact]
    public async Task Ai_pending_needs_a_ready_unlocked_case_and_follows_the_ai_backend_status()
    {
        var status = new StubAiBackendStatus();
        using var factory = new ApiFactory(aiBackend: false, configureServices: s =>
        {
            s.AddSingleton<ICaseEventHandler>(new RecordingEventHandler());
            s.RemoveAll<IAiBackendStatus>();
            s.AddSingleton<IAiBackendStatus>(status);
        });
        var reviewer = factory.Reviewer();
        var draft = await factory.Submitter().CreateCaseWithDocumentsAsync(Scenario.Documents, ready: false);
        var ready = await factory.Submitter().CreateScenarioCaseAsync();
        status.Pending.Add(draft);
        status.Pending.Add(ready);

        Assert.False((await reviewer.GetViewAsync(draft)).AiPending);
        var view = await reviewer.WaitUntilAsync(ready, v => v.AiPending);

        var locked = await (await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{ready}/takeover", etag: view.Case.ETag)).ReadAsync<CaseItem>();
        Assert.False((await reviewer.GetViewAsync(ready)).AiPending);

        await reviewer.SendAsync(HttpMethod.Post, $"/api/cases/{ready}/release", etag: locked.ETag);
        status.Pending.Clear();
        await reviewer.WaitUntilAsync(ready, v => !v.AiPending);
    }

    [Fact]
    public async Task Eval_records_are_for_reviewers()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();

        var records = await (await Factory.Reviewer().GetAsync("/api/eval/records")).ReadAsync<List<CaseRecord>>();

        Assert.Contains(records, r => r.Case.CaseId == caseId && r.Documents.Count == 4);
        Assert.Equal(HttpStatusCode.Forbidden, (await Factory.Agent(caseId).GetAsync("/api/eval/records")).StatusCode);
    }

    private sealed record OpsBody(BusStats Bus, int OutboxCount, FaultSnapshot Faults);

    private sealed class StubAiBackendStatus : IAiBackendStatus
    {
        public HashSet<string> Pending { get; } = [];

        public int OutboxCount => 0;

        public bool IsPending(string caseId) => Pending.Contains(caseId);
    }
}
