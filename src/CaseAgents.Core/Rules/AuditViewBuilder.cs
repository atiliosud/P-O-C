using CaseAgents.Core.Models;

namespace CaseAgents.Core.Rules;

/// <summary>Builds the audit view: one row per question that has a proposal or a decision, plus every raw entry.</summary>
public static class AuditViewBuilder
{
    private static readonly string[] ProposalWrites =
        [AuditActions.ProposalCreated, AuditActions.ProposalUpdated, AuditActions.ProposalNeedsAnalyst];

    private static readonly string[] RowActions =
        [.. ProposalWrites, AuditActions.ProposalWithdrawn, AuditActions.ProposalRejected, AuditActions.AnswerConfirmed];

    public static AuditView Build(Questionnaire q, IReadOnlyList<AuditEntry> entries)
    {
        var ordered = entries.OrderBy(e => e.Seq).ToList();
        var rows = new List<AuditViewRow>();

        foreach (var question in q.Questions)
        {
            var mine = ordered.Where(e => e.QuestionId == question.Id && RowActions.Contains(e.Action)).ToList();
            if (mine.Count > 0)
                rows.Add(BuildRow(q, question, mine, ordered));
        }

        return new AuditView(rows, ordered);
    }

    private static AuditViewRow BuildRow(Questionnaire q, Question question, List<AuditEntry> mine, List<AuditEntry> all)
    {
        var latest = mine[^1];
        var proposed = mine.LastOrDefault(e => ProposalWrites.Contains(e.Action));
        var decision = mine.LastOrDefault(e => e.Action == AuditActions.AnswerConfirmed);
        var snapshot = proposed?.Proposal;

        // Reason: the decision's, else the rejection's, else (for a withdrawn row) the withdrawal's.
        var reason = decision?.Reason
            ?? mine.LastOrDefault(e => e.Action == AuditActions.ProposalRejected)?.Reason
            ?? (latest.Action == AuditActions.ProposalWithdrawn ? latest.Reason : null);

        return new AuditViewRow
        {
            QuestionId = question.Id,
            QuestionText = question.Text,
            ProposedValue = snapshot?.Value,
            ProposedBy = snapshot is null ? null : $"{snapshot.Agent} agent",
            PromptVersion = snapshot?.PromptVersion,
            ProposalStatus = StatusOf(latest, snapshot),
            Basis = proposed?.Basis,
            Confidence = snapshot?.Confidence,
            Band = snapshot?.Band,
            VerifierPassed = snapshot?.VerifierPassed,
            FinalValue = decision?.After,
            FinalBy = decision?.Actor.Id,
            FinalAt = decision?.At,
            Reason = reason,
            Effect = Effect(q, question.Id, all),
        };
    }

    private static ProposalStatus? StatusOf(AuditEntry latest, ProposalSnapshot? snapshot) => latest.Action switch
    {
        AuditActions.ProposalWithdrawn => ProposalStatus.Withdrawn,
        AuditActions.ProposalRejected => ProposalStatus.Rejected,
        AuditActions.AnswerConfirmed when snapshot is null => null,
        AuditActions.AnswerConfirmed => latest.After == snapshot.Value ? ProposalStatus.Accepted : ProposalStatus.Rejected,
        _ => latest.Proposal?.Status,
    };

    private static string? Effect(Questionnaire q, string causeId, List<AuditEntry> all)
    {
        var parts = new List<string>();
        AddEffect(q, causeId, all, [AuditActions.ProposalWithdrawn], withdrawn: true, parts);
        // A confirmed dependent keeps its status, so a NeedsAnalyst one is audited as proposal.needs_analyst.
        AddEffect(q, causeId, all, [AuditActions.ProposalUpdated, AuditActions.ProposalNeedsAnalyst], withdrawn: false, parts);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static void AddEffect(Questionnaire q, string causeId, List<AuditEntry> all, string[] actions, bool withdrawn, List<string> parts)
    {
        var ids = all
            .Where(e => actions.Contains(e.Action) && e.CausedBy == causeId && e.QuestionId is not null)
            .Select(e => e.QuestionId!)
            .Distinct()
            .OrderBy(id => q.Questions.ToList().FindIndex(x => x.Id == id))
            .ToList();
        if (ids.Count == 0)
            return;

        var names = ids.Count >= 3 ? $"{ids[0]} to {ids[^1]}" : string.Join(" and ", ids);
        var plural = ids.Count > 1;
        parts.Add(withdrawn
            ? $"{names} {(plural ? "proposals" : "proposal")} withdrawn"
            : $"{names} {(plural ? "proposals now normal proposals" : "proposal now a normal proposal")}");
    }
}
