using CaseAgents.Core.Models;
using CaseAgents.Core.Security;
using CaseAgents.Infra.Auth;

namespace CaseAgents.Agents;

/// <summary>How a call to the case app ended, as the orchestrator needs to know it.</summary>
public enum CaseAppOutcome
{
    /// <summary>200.</summary>
    Ok,

    /// <summary>409: the case changed, is locked, or the question is already decided. Re-read and decide again.</summary>
    Conflict,

    /// <summary>404: the case does not exist or is outside the token scope.</summary>
    NotFound,

    /// <summary>503, network error or timeout: try again later (outbox).</summary>
    Unavailable,

    /// <summary>Any other 4xx: the request itself is wrong; retrying will not help.</summary>
    Rejected,
}

/// <param name="Code">The "code" of a 409 body (e.g. "version_changed", "ai_locked").</param>
public sealed record CaseAppResult<T>(CaseAppOutcome Outcome, T? Value, string? Error, string? Code);

/// <summary>The only way the AI backend talks to the case app (besides the event bus).</summary>
public interface ICaseAppClient
{
    Task<CaseAppResult<CaseView>> GetCaseAsync(string caseId, CancellationToken ct);

    Task<CaseAppResult<ProposalWriteResponse>> PutProposalAsync(
        string caseId, string questionId, ProposalWrite write, string ifMatch, string idempotencyKey, CancellationToken ct);
}

/// <summary>Gives the AI backend a Case.Agent token scoped to one case.</summary>
public interface IAgentTokenProvider
{
    string GetToken(string caseId);
}

/// <summary>Development: signs agent tokens locally. In Azure this would be the backend's managed identity.</summary>
public sealed class DevAgentTokenProvider(DevTokenIssuer issuer, AiBackendOptions options) : IAgentTokenProvider
{
    public string GetToken(string caseId) => issuer.Issue(options.AgentUserId, [AppRoles.Agent], caseId).AccessToken;
}
