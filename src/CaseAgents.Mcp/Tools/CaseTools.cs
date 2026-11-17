using System.ComponentModel;
using System.Text.Json;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Core.Security;
using CaseAgents.Infra.Auth;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CaseAgents.Mcp.Tools;

/// <summary>
/// Read-only MCP tools over the case store. Depends on <see cref="ICaseReader"/> only, so no tool can write.
/// The endpoint already requires a token with any app role; these tools apply the same read rule as the API.
/// </summary>
[McpServerToolType]
public sealed class CaseTools(ICaseReader reader, Questionnaire questionnaire, IHttpContextAccessor http)
{
    public const string GetCaseName = "get_case";
    public const string GetOpenProposalsName = "get_open_proposals";

    [McpServerTool(Name = GetCaseName, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns a case as JSON: documents, every question with its final answer and current proposal, and the gaps. Shadow proposals are not included.")]
    public async Task<CallToolResult> GetCase(
        [Description("The case id (caseId).")] string caseId,
        CancellationToken ct = default)
    {
        var record = await ReadableRecordAsync(caseId, ct);
        if (record is null)
            return NotFound(caseId);

        return Json(CaseViewBuilder.Build(questionnaire, record, aiPending: false, includeShadow: false));
    }

    [McpServerTool(Name = GetOpenProposalsName, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists the proposals still waiting for an analyst (status Proposed or NeedsAnalyst) as JSON. Shadow proposals are not included.")]
    public async Task<CallToolResult> GetOpenProposals(
        [Description("The case id (caseId).")] string caseId,
        CancellationToken ct = default)
    {
        var record = await ReadableRecordAsync(caseId, ct);
        if (record is null)
            return NotFound(caseId);

        var open = record.Proposals
            .Where(p => !p.Shadow && p.Status is ProposalStatus.Proposed or ProposalStatus.NeedsAnalyst)
            .ToList();
        return Json(open);
    }

    /// <summary>Missing and not-readable both return null, so a caller cannot probe for other users' case ids.</summary>
    private async Task<CaseRecord?> ReadableRecordAsync(string caseId, CancellationToken ct)
    {
        var user = http.HttpContext?.User.ToUserContext() ?? new UserContext("", new HashSet<string>());
        var record = string.IsNullOrWhiteSpace(caseId) ? null : await reader.GetCaseRecordAsync(caseId.Trim(), ct);
        return record is not null && CaseAccess.CanRead(user, record.Case) ? record : null;
    }

    private static CallToolResult NotFound(string caseId) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Case '{caseId}' not found." }],
    };

    private static CallToolResult Json<T>(T value) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, CaseAgentsJson.Options) }],
    };
}
