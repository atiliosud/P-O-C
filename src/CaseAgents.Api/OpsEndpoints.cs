using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Operations;
using CaseAgents.Infra.Auth;

namespace CaseAgents.Api;

/// <summary>GET /api/ops/status body: bus counters (incl. dead letters), AI backend outbox size and simulated faults.</summary>
public sealed record OpsStatus(BusStats Bus, int OutboxCount, FaultSnapshot Faults);

public static class OpsEndpoints
{
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ops/status", (IEventBus bus, IAiBackendStatus aiBackend, FaultState faults) =>
                Results.Ok(new OpsStatus(bus.GetStats(), aiBackend.OutboxCount, faults.Snapshot())))
            .RequireAuthorization(AuthPolicies.Review);
        return app;
    }
}
