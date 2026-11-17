using CaseAgents.Infra;
using CaseAgents.Infra.Auth;
using CaseAgents.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.AddCaseAgentsAuth();
builder.Services.AddCaseAgentsCore(builder.Configuration);
builder.Services.AddCaseAgentsStorage(builder.Configuration);
builder.Services.AddHttpContextAccessor();

// Stateless streamable HTTP: every request is self-contained (no session affinity behind APIM),
// and the tools read the caller from the current request's HttpContext.
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<CaseTools>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapDevTokenEndpoint();
app.MapMcp("/mcp").RequireAuthorization(AuthPolicies.AnyRole);

app.Run();

namespace CaseAgents.Mcp
{
    /// <summary>Marker type for WebApplicationFactory in tests.</summary>
    public sealed class McpMarker;
}
