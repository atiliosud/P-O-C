using CaseAgents.Core.Operations;
using CaseAgents.Infra.Auth;

namespace CaseAgents.Api;

/// <summary>
/// Simulated "case app down" fault: while it is on, every /api request from the AI backend (role Case.Agent) gets 503,
/// so its writes go to the outbox. People keep working. Runs after authentication, so the caller is known.
/// </summary>
public sealed class CaseAppDownMiddleware(RequestDelegate next, FaultState faults)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (faults.CaseAppDown
            && context.Request.Path.StartsWithSegments("/api")
            && context.User.ToUserContext().IsAgent)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new ApiError("The case application is unavailable (simulated fault)."));
            return;
        }

        await next(context);
    }
}
