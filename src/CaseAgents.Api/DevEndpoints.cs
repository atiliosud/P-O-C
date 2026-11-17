using CaseAgents.Core.Models;
using CaseAgents.Core.Operations;

namespace CaseAgents.Api;

public sealed record DevClockResponse(DateTimeOffset Now);

/// <summary>Anonymous demo controls. Mapped only in Development with Dev auth (POST /dev/token is mapped by AuthSetup).</summary>
public static class DevEndpoints
{
    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dev/faults", (FaultState faults) => Results.Ok(faults.Snapshot())).AllowAnonymous();

        app.MapPost("/dev/faults", (FaultsRequest request, FaultState faults) =>
            {
                if (request.AiBackendDown is { } aiDown)
                    faults.AiBackendDown = aiDown;
                if (request.CaseAppDown is { } appDown)
                    faults.CaseAppDown = appDown;
                return Results.Ok(faults.Snapshot());
            })
            .AllowAnonymous();

        app.MapPost("/dev/clock", (DevClockRequest request, DevClock clock) => Results.Ok(new DevClockResponse(clock.Set(request.At))))
            .AllowAnonymous();

        return app;
    }
}
