using System.Text.Json.Serialization;
using CaseAgents.Agents;
using CaseAgents.Api;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;
using CaseAgents.Infra;
using CaseAgents.Infra.Auth;

var builder = WebApplication.CreateBuilder(args);

// Throws at startup when Auth:Mode=Dev outside the Development environment.
builder.AddCaseAgentsAuth();
builder.Services.AddCaseAgentsCore(builder.Configuration);
builder.Services.AddCaseAgentsStorage(builder.Configuration);
builder.Services.AddCaseAgentsMessaging(builder.Configuration);
if (builder.Environment.IsDevelopment())
    builder.Services.AddDevClock();

// The in-process AI backend needs Dev tokens to call this API; with Entra auth it runs as its own service.
var aiBackendEnabled = IsDevAuth(builder.Configuration) && builder.Configuration.GetValue("AiBackend:Enabled", true);
if (aiBackendEnabled)
    builder.Services.AddAiBackend(builder.Configuration);
else
    builder.Services.AddSingleton<IAiBackendStatus, NoAiBackendStatus>();

builder.Services.AddSingleton<CaseViews>();

// Same JSON shape as CaseAgentsJson.Options: camelCase, enums as names, nulls omitted.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = CaseAgentsJson.Options.DefaultIgnoreCondition;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

if (!aiBackendEnabled)
    app.Logger.LogInformation("AI backend disabled");

app.UseAuthentication();
app.UseMiddleware<CaseAppDownMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapDevTokenEndpoint();
if (app.Environment.IsDevelopment() && IsDevAuth(app.Configuration))
    app.MapDevEndpoints();

app.MapCaseEndpoints();
app.MapProposalEndpoints();
app.MapAnswerEndpoints();
app.MapOpsEndpoints();

app.Run();

static bool IsDevAuth(IConfiguration config) =>
    string.Equals(config["Auth:Mode"], AuthModes.Dev, StringComparison.OrdinalIgnoreCase);

namespace CaseAgents.Api
{
    /// <summary>Marker type for WebApplicationFactory in tests.</summary>
    public sealed class ApiMarker;
}
