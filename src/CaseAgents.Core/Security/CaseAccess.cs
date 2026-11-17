using CaseAgents.Core.Models;

namespace CaseAgents.Core.Security;

/// <summary>App roles defined on the Entra app registration (and issued by Dev tokens locally).</summary>
public static class AppRoles
{
    /// <summary>Create cases, attach documents, mark ready.</summary>
    public const string Submit = "Case.Submit";

    /// <summary>Analysts: accept / edit / reject, take over, release, audit.</summary>
    public const string Review = "Case.Review";

    /// <summary>The AI backend's identity: reads only the case in its token scope, writes ONLY proposals.</summary>
    public const string Agent = "Case.Agent";

    public static readonly IReadOnlyList<string> All = [Submit, Review, Agent];
}

/// <summary>The caller, reduced to what the business rules need.</summary>
/// <param name="CaseScope">For Case.Agent tokens: the only case the caller may touch (claim "case_id").</param>
public sealed record UserContext(string UserId, IReadOnlySet<string> Roles, string? CaseScope = null)
{
    public bool IsReviewer => Roles.Contains(AppRoles.Review);
    public bool IsSubmitter => Roles.Contains(AppRoles.Submit);
    public bool IsAgent => Roles.Contains(AppRoles.Agent);
}

/// <summary>Ownership rules applied in code after the role check (API and MCP server).</summary>
public static class CaseAccess
{
    /// <summary>Reviewers read any case; submitters only their own; agents only the case in their scope.</summary>
    public static bool CanRead(UserContext user, CaseItem item) =>
        user.IsReviewer
        || (user.IsSubmitter && string.Equals(item.SubmittedBy, user.UserId, StringComparison.Ordinal))
        || IsAgentFor(user, item.CaseId);

    /// <summary>Only an agent scoped to this case may write its proposals.</summary>
    public static bool CanWriteProposal(UserContext user, string caseId) => IsAgentFor(user, caseId);

    private static bool IsAgentFor(UserContext user, string caseId) =>
        user.IsAgent && user.CaseScope is { Length: > 0 } scope && string.Equals(scope, caseId, StringComparison.Ordinal);
}
