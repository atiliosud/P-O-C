using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CaseAgents.Core.Models;
using CaseAgents.Core.Security;
using CaseAgents.Infra.Auth;
using Microsoft.IdentityModel.Tokens;

namespace CaseAgents.Tests.Api;

public sealed class AuthTests(NoAiBackendApi api) : IClassFixture<NoAiBackendApi>
{
    private ApiFactory Factory => api.Factory;

    [Fact]
    public async Task Health_is_anonymous()
    {
        var response = await Factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/questionnaire")]
    [InlineData("GET", "/api/cases")]
    [InlineData("POST", "/api/cases")]
    [InlineData("GET", "/api/cases/some-id")]
    [InlineData("POST", "/api/cases/some-id/documents")]
    [InlineData("POST", "/api/cases/some-id/ready")]
    [InlineData("PUT", "/api/cases/some-id/proposals/Q1")]
    [InlineData("POST", "/api/cases/some-id/answers/Q1")]
    [InlineData("POST", "/api/cases/some-id/takeover")]
    [InlineData("POST", "/api/cases/some-id/release")]
    [InlineData("GET", "/api/cases/some-id/audit")]
    [InlineData("GET", "/api/eval/records")]
    [InlineData("GET", "/api/ops/status")]
    public async Task Every_api_endpoint_returns_401_without_token(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
            request.Content = JsonContent.Create(new { });

        var response = await Factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_another_key_returns_401()
    {
        var otherKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("another-test-only-key-0123456789-abcdefgh"));
        var forged = new DevTokenIssuer(otherKey, new DevAuthOptions(), TimeProvider.System).Issue("rita", [AppRoles.Review]);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged.AccessToken);

        var response = await client.GetAsync("/api/questionnaire");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_without_user_id_gets_403()
    {
        var response = await Factory.CreateClientAs("", [AppRoles.Submit]).GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(AppRoles.Submit)]
    [InlineData(AppRoles.Review)]
    [InlineData(AppRoles.Agent)]
    public async Task Any_role_reads_the_questionnaire(string role)
    {
        var response = await Factory.CreateClientAs("someone", [role], caseId: "c1").GetAsync("/api/questionnaire");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var questionnaire = await response.ReadAsync<Questionnaire>();
        Assert.Equal("v12", questionnaire.PromptVersion);
        Assert.Equal(9, questionnaire.Questions.Count);
    }

    [Fact]
    public async Task Dev_token_endpoint_issues_a_token_the_api_accepts()
    {
        var client = Factory.CreateClient();
        var tokenResponse = await client.PostAsJsonAsync("/dev/token", new { userId = "alice", roles = new[] { AppRoles.Submit } });
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = await tokenResponse.ReadAsync<DevTokenResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var response = await client.GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Dev_token_for_an_agent_needs_a_case_id()
    {
        var response = await Factory.CreateClient()
            .PostAsJsonAsync("/dev/token", new { userId = "ai-backend", roles = new[] { AppRoles.Agent } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Agent_gets_403_when_writing_a_final_answer()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var agent = Factory.Agent(caseId);
        var view = await agent.GetViewAsync(caseId);

        var response = await agent.AnswerAsync(caseId, "Q1", AnswerActions.Edit, "Maria Example", "from the id card", view.Case.ETag);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await Factory.Reviewer().GetViewAsync(caseId);
        Assert.Null(after.Question("Q1").Answer);
    }

    [Theory]
    [InlineData("POST", "/takeover")]
    [InlineData("POST", "/release")]
    [InlineData("GET", "/audit")]
    [InlineData("POST", "/ready")]
    public async Task Agent_gets_403_on_analyst_and_submitter_actions(string method, string suffix)
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();

        var response = await Factory.Agent(caseId).SendAsync(new HttpMethod(method), $"/api/cases/{caseId}{suffix}", etag: "\"x\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Agent_gets_403_on_the_case_list()
    {
        var response = await Factory.Agent("c1").GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Agent_cannot_read_another_case()
    {
        var submitter = Factory.Submitter();
        var mine = await submitter.CreateScenarioCaseAsync();
        var other = await submitter.CreateScenarioCaseAsync();
        var agent = Factory.Agent(mine);

        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"/api/cases/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync($"/api/cases/{other}")).StatusCode);
    }

    [Fact]
    public async Task Agent_cannot_write_proposal_to_another_case()
    {
        var submitter = Factory.Submitter();
        var mine = await submitter.CreateScenarioCaseAsync();
        var other = await submitter.CreateScenarioCaseAsync();
        var otherView = await Factory.Reviewer().GetViewAsync(other);

        var response = await Factory.Agent(mine).PutProposalAsync(other, "Q4", Proposals.Q4Yes(), otherView.Case.ETag, "m1:Q4");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await Factory.Reviewer().GetViewAsync(other);
        Assert.Null(after.Question("Q4").Proposal);
    }

    [Fact]
    public async Task Agent_token_without_case_scope_reads_nothing()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();
        var unscoped = Factory.CreateClientAs("ai-backend", [AppRoles.Agent]);
        var view = await Factory.Reviewer().GetViewAsync(caseId);

        Assert.Equal(HttpStatusCode.NotFound, (await unscoped.GetAsync($"/api/cases/{caseId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await unscoped.PutProposalAsync(caseId, "Q4", Proposals.Q4Yes(), view.Case.ETag, "m1:Q4")).StatusCode);
    }

    [Fact]
    public async Task Submitter_reads_only_own_cases()
    {
        var alice = Factory.Submitter("alice-own");
        var bob = Factory.Submitter("bob-own");
        var aliceCase = await alice.CreateScenarioCaseAsync();
        var bobCase = await bob.CreateScenarioCaseAsync();

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/cases/{aliceCase}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/cases/{bobCase}")).StatusCode);

        var list = await (await alice.GetAsync("/api/cases")).ReadAsync<List<CaseSummary>>();
        Assert.Contains(list, s => s.Case.CaseId == aliceCase);
        Assert.DoesNotContain(list, s => s.Case.CaseId == bobCase);
        Assert.All(list, s => Assert.Equal("alice-own", s.Case.SubmittedBy));
    }

    [Fact]
    public async Task Reviewer_lists_every_case_newest_first()
    {
        var first = await Factory.Submitter("carol").CreateScenarioCaseAsync();
        var second = await Factory.Submitter("dave").CreateScenarioCaseAsync();

        var list = await (await Factory.Reviewer().GetAsync("/api/cases")).ReadAsync<List<CaseSummary>>();

        var ids = list.Select(s => s.Case.CaseId).ToList();
        Assert.True(ids.IndexOf(second) < ids.IndexOf(first));
        Assert.Equal(9, list.Single(s => s.Case.CaseId == first).Questions);
    }

    [Fact]
    public async Task Submitter_gets_403_on_analyst_endpoints()
    {
        var submitter = Factory.Submitter();
        var caseId = await submitter.CreateScenarioCaseAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await submitter.AnswerAsync(caseId, "Q1", AnswerActions.Edit, "x", "y", "\"e\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await submitter.GetAsync($"/api/cases/{caseId}/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await submitter.GetAsync("/api/eval/records")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await submitter.GetAsync("/api/ops/status")).StatusCode);
    }

    [Fact]
    public async Task Reviewer_gets_403_on_create_case()
    {
        var response = await Factory.Reviewer().PostAsJsonAsync("/api/cases", new { title = "t" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
