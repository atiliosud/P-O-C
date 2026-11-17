using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Core.Operations;
using CaseAgents.Core.Rules;
using CaseAgents.Core.Security;

namespace CaseAgents.Api;

/// <summary>Builds the case view for a caller and works out the "AI pending" flag.</summary>
public sealed class CaseViews(Questionnaire questionnaire, FaultState faults, IEventBus bus, IAiBackendStatus aiBackend)
{
    /// <summary>Shadow proposals are only included for agents.</summary>
    public CaseView Build(CaseRecord record, UserContext user) =>
        CaseViewBuilder.Build(questionnaire, record, IsAiPending(record.Case), includeShadow: user.IsAgent);

    /// <summary>
    /// Ready, not taken over, and the AI backend still has work for the case: it is down, events are waiting on the
    /// bus, or it is processing / holding writes in its outbox.
    /// </summary>
    public bool IsAiPending(CaseItem item) =>
        item.Status == CaseStatus.Ready
        && !item.AiLocked
        && (faults.AiBackendDown || bus.PendingFor(item.CaseId) > 0 || aiBackend.IsPending(item.CaseId));
}

/// <summary>Audit drafts and the pieces they copy from a proposal.</summary>
internal static class AuditDrafts
{
    public static AuditActor Person(UserContext user) => new(ActorTypes.Person, user.UserId);

    public static AuditActor Agent(UserContext user) => new(ActorTypes.Agent, user.UserId);

    public static AuditDraft Simple(AuditActor actor, string action, string? after = null) =>
        new() { Actor = actor, Action = action, After = after };

    public static AuditBasis? Basis(Proposal? proposal) =>
        proposal?.Evidence is { } e
            ? new AuditBasis(e.DocumentId, e.Page, e.Offset, e.Length, e.Hash, proposal.Quote ?? "")
            : null;

    public static ProposalSnapshot? Snapshot(Proposal? p) =>
        p is null
            ? null
            : new ProposalSnapshot(p.Value, p.Confidence, p.ConfidenceBand, p.Agent, p.PromptVersion, p.ModelVersion, p.VerifierPassed, p.Status, p.DependsOnUnconfirmed);
}
