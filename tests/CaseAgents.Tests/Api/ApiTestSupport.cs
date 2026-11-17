using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CaseAgents.Agents;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Core.Security;
using CaseAgents.Infra.Auth;
using CaseAgents.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace CaseAgents.Tests.Api;

/// <summary>
/// The real API host with InMemory storage. Dev auth uses a fixed test-only signing key, so tests can mint tokens
/// directly. By default the in-process AI backend runs with the Fake models and calls the API through the test server.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<CaseAgents.Api.ApiMarker>
{
    // Test-only HMAC key used by this in-process host; it protects nothing outside the test run.
    public const string TestSigningKey = "api-tests-signing-key-0123456789abcdef";

    private readonly bool aiBackend;
    private readonly string environment;
    private readonly string authMode;
    private readonly Action<IServiceCollection>? configureServices;

    /// <summary>Development, Dev auth, InMemory, AI backend on with the Fake models.</summary>
    public ApiFactory()
        : this(aiBackend: true)
    {
    }

    internal ApiFactory(
        bool aiBackend = true,
        string environment = "Development",
        string authMode = "Dev",
        Action<IServiceCollection>? configureServices = null)
    {
        this.aiBackend = aiBackend;
        this.environment = environment;
        this.authMode = authMode;
        this.configureServices = configureServices;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Auth:Mode", authMode);
        builder.UseSetting("Auth:Dev:SigningKey", TestSigningKey);
        builder.UseSetting("Storage:Mode", "InMemory");
        builder.UseSetting("Models:Mode", "Fake");
        builder.UseSetting("AiBackend:Enabled", aiBackend ? "true" : "false");
        builder.UseSetting("Bus:PumpInterval", "00:00:00.050");
        builder.UseSetting("AiBackend:OutboxFlushInterval", "00:00:00.200");
        // Placeholder Entra values so the Entra handler can be built; no token is ever validated against them.
        builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
        builder.UseSetting("AzureAd:TenantId", "00000000-0000-0000-0000-000000000001");
        builder.UseSetting("AzureAd:ClientId", "00000000-0000-0000-0000-000000000002");
        builder.UseSetting("AzureAd:Audience", "api://case-agents-tests");

        if (configureServices is not null)
            builder.ConfigureTestServices(configureServices);
    }

    /// <summary>Points the AI backend's HTTP client at the in-process test server.</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        host.Services.GetService<CaseAppHttpClientProvider>()?.UseHandler(host.GetTestServer().CreateHandler());
        return host;
    }

    public HttpClient CreateClientAs(string userId, string[] roles, string? caseId = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var token = new DevTokenIssuer(key, new DevAuthOptions(), TimeProvider.System).Issue(userId, roles, caseId);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    public HttpClient Submitter(string userId = "alice") => CreateClientAs(userId, [AppRoles.Submit]);

    public HttpClient Reviewer(string userId = Scenario.Analyst) => CreateClientAs(userId, [AppRoles.Review]);

    public HttpClient Agent(string caseId, string userId = "ai-backend") => CreateClientAs(userId, [AppRoles.Agent], caseId);
}

/// <summary>Records every event the bus delivers (stands in for the AI backend when it is switched off).</summary>
public sealed class RecordingEventHandler : ICaseEventHandler
{
    private readonly ConcurrentQueue<CaseEvent> events = new();

    public IReadOnlyList<CaseEvent> Events => events.ToList();

    public Task HandleAsync(CaseEvent evt, CancellationToken ct)
    {
        events.Enqueue(evt);
        return Task.CompletedTask;
    }

    public async Task<CaseEvent> WaitForAsync(Func<CaseEvent, bool> match, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var found = events.FirstOrDefault(match);
            if (found is not null)
                return found;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The expected event was not delivered.");
            await Task.Delay(20);
        }
    }
}

/// <summary>
/// Host without the AI backend: tests play the agent themselves. A recording handler consumes bus events,
/// so nothing stays pending on the bus.
/// </summary>
public sealed class NoAiBackendApi : IDisposable
{
    public NoAiBackendApi()
    {
        Factory = new ApiFactory(aiBackend: false, configureServices: s => s.AddSingleton<ICaseEventHandler>(Events));
    }

    public RecordingEventHandler Events { get; } = new();

    public ApiFactory Factory { get; }

    public void Dispose() => Factory.Dispose();
}

public static class ApiClientExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(CaseAgentsJson.Options);
        return value ?? throw new InvalidOperationException($"Empty {typeof(T).Name} response body.");
    }

    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response)
    {
        var error = await response.Content.ReadFromJsonAsync<ErrorBody>(CaseAgentsJson.Options);
        return error?.Code;
    }

    public static Task<HttpResponseMessage> SendAsync(
        this HttpClient client, HttpMethod method, string path, object? body = null, string? etag = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: CaseAgentsJson.Options);
        if (etag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    public static async Task<CaseItem> CreateCaseAsync(this HttpClient submitter, string title = "Accounts outside the EU")
    {
        var response = await submitter.PostAsJsonAsync("/api/cases", new { title });
        response.EnsureSuccessStatusCode();
        return await response.ReadAsync<CaseItem>();
    }

    public static Task<HttpResponseMessage> AddDocumentAsync(this HttpClient submitter, string caseId, CaseDocument document, string etag) =>
        submitter.SendAsync(
            HttpMethod.Post,
            $"/api/cases/{caseId}/documents",
            new AddDocumentRequest(document.DocumentId, document.Name, document.Kind, document.Pages),
            etag);

    /// <summary>Creates a case, attaches the documents and (by default) marks it ready. Returns the case id.</summary>
    public static async Task<string> CreateCaseWithDocumentsAsync(
        this HttpClient submitter, Func<string, IReadOnlyList<CaseDocument>> documents, bool ready = true)
    {
        var item = await submitter.CreateCaseAsync();
        foreach (var document in documents(item.CaseId))
        {
            var added = await submitter.AddDocumentAsync(item.CaseId, document, item.ETag!);
            added.EnsureSuccessStatusCode();
            item = await added.ReadAsync<CaseItem>();
        }

        if (ready)
        {
            var response = await submitter.SendAsync(HttpMethod.Post, $"/api/cases/{item.CaseId}/ready", etag: item.ETag);
            response.EnsureSuccessStatusCode();
        }

        return item.CaseId;
    }

    /// <summary>Create + scenario documents + ready.</summary>
    public static Task<string> CreateScenarioCaseAsync(this HttpClient submitter) =>
        submitter.CreateCaseWithDocumentsAsync(Scenario.Documents);

    public static async Task<CaseView> GetViewAsync(this HttpClient client, string caseId)
    {
        var response = await client.GetAsync($"/api/cases/{caseId}");
        response.EnsureSuccessStatusCode();
        return await response.ReadAsync<CaseView>();
    }

    public static Task<HttpResponseMessage> AnswerAsync(
        this HttpClient client, string caseId, string questionId, string action, string? value, string? reason, string? etag) =>
        client.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/answers/{questionId}", new AnswerRequest(action, value, reason), etag);

    public static Task<HttpResponseMessage> PutProposalAsync(
        this HttpClient agent, string caseId, string questionId, ProposalWrite write, string? etag, string? idempotencyKey) =>
        agent.SendAsync(HttpMethod.Put, $"/api/cases/{caseId}/proposals/{questionId}", write, etag, idempotencyKey);

    /// <summary>Polls until no question is left to propose and the AI backend is idle (10 s max).</summary>
    public static Task<CaseView> WaitForProposalsAsync(this HttpClient reviewer, string caseId)
    {
        var q = Scenario.Questionnaire();
        return reviewer.WaitUntilAsync(caseId, view =>
            !view.AiPending
            && QuestionRules.NextToPropose(
                q,
                view.Questions.Where(s => s.Answer is not null).Select(s => s.Answer!).ToList(),
                view.Questions.Where(s => s.Proposal is not null).Select(s => s.Proposal!).ToList()) is null);
    }

    public static async Task<CaseView> WaitUntilAsync(this HttpClient client, string caseId, Func<CaseView, bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var view = await client.GetViewAsync(caseId);
            if (condition(view))
                return view;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Case {caseId} did not reach the expected state within {seconds} s.");
            await Task.Delay(50);
        }
    }

    public static QuestionState Question(this CaseView view, string questionId) =>
        view.Questions.Single(s => s.Question.Id == questionId);

    private sealed record ErrorBody(string? Error, string? Code);
}

/// <summary>Proposal bodies the tests send while playing the agent.</summary>
public static class Proposals
{
    public static ProposalWrite Q4Yes() => new()
    {
        Status = ProposalStatus.Proposed,
        Value = YesNo.Yes,
        Confidence = 0.93,
        ConfidenceBand = ConfidenceBand.High,
        Quote = Scenario.BankLetterPage3Line,
        DocumentId = Scenario.BankLetterId,
        Page = 3,
        Agent = AgentNames.Answerer,
        PromptVersion = "v12",
        ModelVersion = "test-model",
        VerifierPassed = true,
    };

    public static ProposalWrite Q1Name() => new()
    {
        Status = ProposalStatus.Proposed,
        Value = "Maria Example",
        Confidence = 0.90,
        ConfidenceBand = ConfidenceBand.High,
        Quote = "Full name: Maria Example",
        DocumentId = "id-card",
        Page = 1,
        Agent = AgentNames.Answerer,
        PromptVersion = "v12",
        ModelVersion = "test-model",
        VerifierPassed = true,
    };

    public static ProposalWrite NeedsAnalyst(string reason = "No passage mentions the question keywords") => new()
    {
        Status = ProposalStatus.NeedsAnalyst,
        Agent = AgentNames.Answerer,
        PromptVersion = "v12",
        ModelVersion = "test-model",
        NeedsAnalystReason = reason,
    };
}
