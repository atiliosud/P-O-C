using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;
using CaseAgents.Core.Security;
using CaseAgents.Infra.Auth;
using CaseAgents.Mcp.Tools;
using CaseAgents.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CaseAgents.Tests.Mcp;

public sealed class CaseToolsTests(McpFactory factory) : IClassFixture<McpFactory>
{
    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private CaseTools ToolsFor(string userId, params string[] roles) =>
        new(factory.Services.GetRequiredService<ICaseReader>(), Scenario.Questionnaire(), FakeCaller.As(userId, roles));

    [Fact]
    public async Task get_case_returns_view_for_reviewer_and_not_found_for_other_submitter()
    {
        var caseId = await McpSeed.SeedAsync(factory.Store, "alice", McpSeed.Proposal("Q4", ProposalStatus.Proposed));

        var reviewer = await ToolsFor("rita", AppRoles.Review).GetCase(caseId);
        var owner = await ToolsFor("alice", AppRoles.Submit).GetCase(caseId);
        var other = await ToolsFor("bob", AppRoles.Submit).GetCase(caseId);
        var missing = await ToolsFor("bob", AppRoles.Submit).GetCase("does-not-exist");

        Assert.NotEqual(true, reviewer.IsError);
        var view = JsonSerializer.Deserialize<CaseView>(Text(reviewer), CaseAgentsJson.Options)!;
        Assert.Equal(caseId, view.Case.CaseId);
        Assert.Equal("v12", view.PromptVersion);
        Assert.False(view.AiPending);
        Assert.NotEqual(true, owner.IsError);
        Assert.True(other.IsError);
        Assert.True(missing.IsError);
        Assert.Equal(Text(missing).Replace("does-not-exist", caseId), Text(other));
    }

    [Fact]
    public async Task get_case_hides_shadow_proposals()
    {
        var caseId = await McpSeed.SeedAsync(factory.Store, "alice", McpSeed.Proposal("Q4", ProposalStatus.Proposed, shadow: true));

        var result = await ToolsFor("rita", AppRoles.Review).GetCase(caseId);

        var view = JsonSerializer.Deserialize<CaseView>(Text(result), CaseAgentsJson.Options)!;
        Assert.All(view.Questions, s => Assert.Null(s.Proposal));
    }

    [Fact]
    public async Task get_open_proposals_lists_only_proposed_and_needs_analyst()
    {
        var caseId = await McpSeed.SeedAsync(factory.Store, "alice",
            McpSeed.Proposal("Q1", ProposalStatus.Proposed),
            McpSeed.Proposal("Q2", ProposalStatus.NeedsAnalyst),
            McpSeed.Proposal("Q3", ProposalStatus.Accepted),
            McpSeed.Proposal("Q4", ProposalStatus.Withdrawn),
            McpSeed.Proposal("Q4.1", ProposalStatus.Proposed, shadow: true));

        var result = await ToolsFor("alice", AppRoles.Submit).GetOpenProposals(caseId);

        Assert.NotEqual(true, result.IsError);
        var proposals = JsonSerializer.Deserialize<List<Proposal>>(Text(result), CaseAgentsJson.Options)!;
        Assert.Equal(["Q1", "Q2"], proposals.Select(p => p.QuestionId).Order());
        Assert.True((await ToolsFor("bob", AppRoles.Submit).GetOpenProposals(caseId)).IsError);
    }

    [Fact]
    public void Mcp_tools_are_read_only()
    {
        var tools = typeof(CaseTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .OfType<McpServerToolAttribute>()
            .ToList();

        Assert.Equal(
            new[] { CaseTools.GetCaseName, CaseTools.GetOpenProposalsName }.Order(),
            tools.Select(t => t.Name!).Order());
        Assert.All(tools, t =>
        {
            Assert.True(t.ReadOnly);
            Assert.False(t.Destructive);
        });

        var parameterTypes = typeof(CaseTools).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();
        Assert.Equivalent(new[] { typeof(ICaseReader), typeof(Questionnaire), typeof(IHttpContextAccessor) }, parameterTypes);
    }
}

public sealed class McpEndpointTests(McpFactory factory) : IClassFixture<McpFactory>
{
    private const string ToolsListRequest = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";

    private static HttpRequestMessage ToolsList(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string TokenFor(string userId, params string[] roles)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(McpFactory.TestSigningKey));
        return new DevTokenIssuer(key, new DevAuthOptions(), TimeProvider.System).Issue(userId, roles).AccessToken;
    }

    [Fact]
    public async Task Mcp_endpoint_requires_token()
    {
        var anonymous = await factory.CreateClient().SendAsync(ToolsList(token: null));
        var noRoles = await factory.CreateClient().SendAsync(ToolsList(TokenFor("nobody")));
        var reviewer = await factory.CreateClient().SendAsync(ToolsList(TokenFor("rita", AppRoles.Review)));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noRoles.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reviewer.StatusCode);
        var body = await reviewer.Content.ReadAsStringAsync();
        Assert.Contains($"\"{CaseTools.GetCaseName}\"", body);
        Assert.Contains($"\"{CaseTools.GetOpenProposalsName}\"", body);
    }
}
