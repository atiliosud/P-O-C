using System.Security.Claims;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;
using CaseAgents.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CaseAgents.Tests.Mcp;

/// <summary>IHttpContextAccessor whose request carries a principal shaped like a validated token (oid + roles).</summary>
public static class FakeCaller
{
    public static IHttpContextAccessor As(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new("oid", userId) };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
    }
}

/// <summary>The real MCP host: Development, Dev tokens with a fixed test-only signing key, InMemory storage.</summary>
public sealed class McpFactory : WebApplicationFactory<CaseAgents.Mcp.McpMarker>
{
    // Test-only HMAC key used by this in-process host; it protects nothing outside the test run.
    public const string TestSigningKey = "mcp-tests-signing-key-0123456789abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:Mode", "Dev");
        builder.UseSetting("Auth:Dev:SigningKey", TestSigningKey);
        builder.UseSetting("Storage:Mode", "InMemory");
    }

    public ICaseStore Store => Services.GetRequiredService<ICaseStore>();
}

public static class McpSeed
{
    private static readonly AuditActor TestActor = new(ActorTypes.System, "test");

    private static AuditDraft Audit(string action, string? questionId = null) =>
        new() { Actor = TestActor, Action = action, QuestionId = questionId };

    /// <summary>Creates a Ready scenario case with its documents and the given proposals; returns the case id.</summary>
    public static async Task<string> SeedAsync(ICaseStore store, string submittedBy, params Proposal[] proposals)
    {
        var caseId = ItemIds.NewCaseId();
        var current = await store.CreateCaseAsync(Scenario.NewCase(caseId, submittedBy, CaseStatus.Draft), Audit(AuditActions.CaseCreated));
        foreach (var document in Scenario.Documents(caseId))
            current = await store.AddDocumentAsync(current, document, Audit(AuditActions.DocumentAdded));
        foreach (var p in proposals)
        {
            var proposal = p with { Id = ItemIds.Proposal(caseId, p.QuestionId), CaseId = caseId };
            current = (await store.WriteProposalAsync(current, proposal, Audit(AuditActions.ProposalCreated, p.QuestionId), null)).Case;
        }

        return caseId;
    }

    public static Proposal Proposal(string questionId, ProposalStatus status, bool shadow = false) => new()
    {
        Id = "x",
        CaseId = "x",
        QuestionId = questionId,
        Value = status == ProposalStatus.NeedsAnalyst ? null : "Yes",
        Agent = "Answerer",
        PromptVersion = "v12",
        ModelVersion = "fake-answerer-1",
        Status = status,
        Shadow = shadow,
    };
}
