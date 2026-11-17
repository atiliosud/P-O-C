using System.Net;
using System.Text;
using System.Text.Json;
using CaseAgents.Agents;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Core.Security;
using CaseAgents.Infra;
using CaseAgents.Infra.Auth;
using CaseAgents.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CaseAgents.Tests.Agents;

public class HttpCaseAppClientTests
{
    private static readonly ProposalWrite Write = new()
    {
        Status = ProposalStatus.NeedsAnalyst,
        Agent = AgentNames.Answerer,
        PromptVersion = "v12",
        ModelVersion = "m",
        NeedsAnalystReason = "No quote",
    };

    [Fact]
    public async Task Put_sends_bearer_token_if_match_and_idempotency_key()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, new ProposalWriteResponse(Proposal(), "\"v2\"", false)));
        var client = Client(handler);

        var result = await client.PutProposalAsync("case-1", "Q4.1", Write, "\"v1\"", "case-1:1:Q4.1", CancellationToken.None);

        Assert.Equal(CaseAppOutcome.Ok, result.Outcome);
        Assert.Equal("\"v2\"", result.Value!.CaseETag);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("http://case-app/api/cases/case-1/proposals/Q4.1", request.Uri);
        Assert.Equal("Bearer token-for-case-1", request.Authorization);
        Assert.Equal("\"v1\"", request.IfMatch);
        Assert.Equal("case-1:1:Q4.1", request.IdempotencyKey);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("NeedsAnalyst", body.RootElement.GetProperty("status").GetString());
        Assert.False(body.RootElement.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task Get_returns_the_case_view_with_its_etag()
    {
        var view = CaseViewBuilder.Build(Scenario.Questionnaire(), Scenario.Record("case-1"), aiPending: false, includeShadow: true);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, view));
        var client = Client(handler);

        var result = await client.GetCaseAsync("case-1", CancellationToken.None);

        Assert.Equal(CaseAppOutcome.Ok, result.Outcome);
        Assert.Equal("\"v1\"", result.Value!.Case.ETag);
        Assert.Equal(9, result.Value.Questions.Count);
        Assert.Equal("http://case-app/api/cases/case-1", handler.Requests.Single().Uri);
        Assert.Equal("Bearer token-for-case-1", handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task Get_takes_the_etag_header_when_the_body_has_none()
    {
        var view = CaseViewBuilder.Build(Scenario.Questionnaire(), Scenario.Record("case-1") with { Case = Scenario.NewCase("case-1") }, false, true);
        var handler = new StubHandler(_ =>
        {
            var response = Json(HttpStatusCode.OK, view);
            response.Headers.TryAddWithoutValidation("ETag", "\"v7\"");
            return response;
        });

        var result = await Client(handler).GetCaseAsync("case-1", CancellationToken.None);

        Assert.Equal("\"v7\"", result.Value!.Case.ETag);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, CaseAppOutcome.Conflict)]
    [InlineData(HttpStatusCode.NotFound, CaseAppOutcome.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CaseAppOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, CaseAppOutcome.Rejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, CaseAppOutcome.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, CaseAppOutcome.Rejected)]
    public async Task Status_codes_map_to_outcomes(HttpStatusCode status, CaseAppOutcome expected)
    {
        var handler = new StubHandler(_ => Json(status, new { error = "nope", code = "ai_locked" }));
        var client = Client(handler);

        var put = await client.PutProposalAsync("case-1", "Q1", Write, "\"v1\"", "k", CancellationToken.None);
        var get = await client.GetCaseAsync("case-1", CancellationToken.None);

        Assert.Equal(expected, put.Outcome);
        Assert.Equal(expected, get.Outcome);
        Assert.Null(put.Value);
        if (expected == CaseAppOutcome.Conflict)
            Assert.Equal("ai_locked", put.Code);
    }

    [Fact]
    public async Task Network_errors_and_timeouts_are_unavailable()
    {
        var refused = Client(new StubHandler(_ => throw new HttpRequestException("connection refused")));
        var timeout = Client(new StubHandler(_ => throw new TaskCanceledException("timeout")));

        Assert.Equal(CaseAppOutcome.Unavailable, (await refused.GetCaseAsync("case-1", CancellationToken.None)).Outcome);
        Assert.Equal(CaseAppOutcome.Unavailable, (await timeout.PutProposalAsync("case-1", "Q1", Write, "\"v1\"", "k", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = Client(new StubHandler(_ => throw new TaskCanceledException("cancelled")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCaseAsync("case-1", cts.Token));
    }

    [Fact]
    public void Dev_token_provider_issues_an_agent_token_scoped_to_the_case()
    {
        var issuer = new DevTokenIssuer(new SymmetricSecurityKey(new byte[32]), new DevAuthOptions(), TimeProvider.System);
        var provider = new DevAgentTokenProvider(issuer, new AiBackendOptions { AgentUserId = "ai-backend" });

        var token = new JsonWebTokenHandler().ReadJsonWebToken(provider.GetToken("case-9"));

        Assert.Equal("ai-backend", token.GetClaim("oid").Value);
        Assert.Equal([AppRoles.Agent], token.Claims.Where(c => c.Type == "roles").Select(c => c.Value));
        Assert.Equal("case-9", token.GetClaim(DevTokenIssuer.CaseScopeClaim).Value);
    }

    [Fact]
    public void Http_client_provider_uses_the_configured_base_address_and_replaceable_handler()
    {
        var provider = new CaseAppHttpClientProvider(new AiBackendOptions { CaseApiBaseUrl = "http://localhost:5080" });
        Assert.Equal(new Uri("http://localhost:5080/"), provider.Client.BaseAddress);
        Assert.Same(provider.Client, provider.Client);

        var before = provider.Client;
        provider.UseHandler(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.NotSame(before, provider.Client);
        Assert.Equal(new Uri("http://localhost:5080/"), provider.Client.BaseAddress);
    }

    [Fact]
    public void AddAiBackend_registers_the_orchestrator_as_event_handler_status_and_flusher()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiBackend:ShadowMode"] = "true",
            ["AiBackend:CaseApiBaseUrl"] = "http://case-app:1234",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaseAgentsCore(config);
        services.AddSingleton(new DevTokenIssuer(new SymmetricSecurityKey(new byte[32]), new DevAuthOptions(), TimeProvider.System));
        services.AddAiBackend(config);

        using var sp = services.BuildServiceProvider();

        var orchestrator = sp.GetRequiredService<CaseOrchestrator>();
        Assert.Same(orchestrator, sp.GetServices<ICaseEventHandler>().Single());
        Assert.IsType<AiBackendStatus>(sp.GetRequiredService<CaseAgents.Core.Abstractions.IAiBackendStatus>());
        Assert.Contains(sp.GetServices<IHostedService>(), s => s is OutboxFlusher);
        Assert.IsType<HttpCaseAppClient>(sp.GetRequiredService<ICaseAppClient>());
        Assert.True(sp.GetRequiredService<AiBackendOptions>().ShadowMode);
        Assert.Equal(new Uri("http://case-app:1234/"), sp.GetRequiredService<CaseAppHttpClientProvider>().Client.BaseAddress);
    }

    private static HttpCaseAppClient Client(StubHandler handler)
    {
        var provider = new CaseAppHttpClientProvider(new AiBackendOptions { CaseApiBaseUrl = "http://case-app" });
        provider.UseHandler(handler);
        return new HttpCaseAppClient(provider, new FixedTokens());
    }

    private static Proposal Proposal() => new()
    {
        Id = ItemIds.Proposal("case-1", "Q4.1"),
        CaseId = "case-1",
        QuestionId = "Q4.1",
        Agent = AgentNames.Answerer,
        PromptVersion = "v12",
        ModelVersion = "m",
        Status = ProposalStatus.NeedsAnalyst,
    };

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, CaseAgentsJson.Options), Encoding.UTF8, "application/json"),
    };

    private sealed class FixedTokens : IAgentTokenProvider
    {
        public string GetToken(string caseId) => $"token-for-{caseId}";
    }

    internal sealed record SeenRequest(HttpMethod Method, string Uri, string? Authorization, string? IfMatch, string? IdempotencyKey, string? Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new SeenRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("If-Match", out var ifMatch) ? ifMatch.Single() : null,
                request.Headers.TryGetValues("Idempotency-Key", out var key) ? key.Single() : null,
                body));
            return respond(request);
        }
    }
}
