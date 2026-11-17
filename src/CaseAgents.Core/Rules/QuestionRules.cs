using CaseAgents.Core.Models;

namespace CaseAgents.Core.Rules;

public enum DependentActionKind
{
    Withdraw,
    Confirm,
}

public sealed record DependentAction(Question Question, Proposal Current, DependentActionKind Kind, string Reason);

/// <summary>Which questions apply, which one the agents propose next, and what a final answer does to its dependents.</summary>
public static class QuestionRules
{
    public static Applicability GetApplicability(Questionnaire q, Question question, IReadOnlyList<FinalAnswer> answers)
    {
        if (question.DependsOn is not { } dep)
            return Applicability.Applicable;

        // A question under a parent that does not apply does not apply either.
        var parentQuestion = q.Find(dep.QuestionId);
        if (parentQuestion is not null && GetApplicability(q, parentQuestion, answers) == Applicability.NotApplicable)
            return Applicability.NotApplicable;

        var parent = answers.FirstOrDefault(a => a.QuestionId == dep.QuestionId);
        if (parent is null)
            return Applicability.ParentUnconfirmed;

        return parent.Value == dep.Value ? Applicability.Applicable : Applicability.NotApplicable;
    }

    /// <summary>
    /// First question (file order) with no final answer, that applies, and with no proposal or a withdrawn one.
    /// People's values win: agents only propose on untouched questions.
    /// </summary>
    public static Question? NextToPropose(Questionnaire q, IReadOnlyList<FinalAnswer> answers, IReadOnlyList<Proposal> proposals)
    {
        foreach (var question in q.Questions)
        {
            if (answers.Any(a => a.QuestionId == question.Id))
                continue;
            if (GetApplicability(q, question, answers) == Applicability.NotApplicable)
                continue;

            var proposal = proposals.FirstOrDefault(p => p.QuestionId == question.Id);
            if (proposal is null || proposal.Status == ProposalStatus.Withdrawn)
                return question;
        }

        return null;
    }

    /// <summary>What a final answer on <paramref name="parentId"/> means for the open proposals of its dependents.</summary>
    public static IReadOnlyList<DependentAction> DependentActions(
        Questionnaire q, string parentId, IReadOnlyList<FinalAnswer> answers, IReadOnlyList<Proposal> proposals)
    {
        var result = new List<DependentAction>();
        var parent = answers.FirstOrDefault(a => a.QuestionId == parentId);
        if (parent is null)
            return result;

        foreach (var child in q.DependentsOf(parentId))
        {
            if (answers.Any(a => a.QuestionId == child.Id))
                continue;

            var proposal = proposals.FirstOrDefault(p => p.QuestionId == child.Id);
            if (proposal is null || proposal.Status is not (ProposalStatus.Proposed or ProposalStatus.NeedsAnalyst))
                continue;

            var required = child.DependsOn!.Value;
            if (parent.Value != required)
            {
                var reason = $"{parentId} confirmed as {parent.Value}; {child.Id} applies only if {parentId} = {required}";
                result.Add(new DependentAction(child, proposal, DependentActionKind.Withdraw, reason));
            }
            else if (proposal.DependsOnUnconfirmed)
            {
                result.Add(new DependentAction(child, proposal, DependentActionKind.Confirm, $"{parentId} confirmed as {parent.Value}"));
            }
        }

        return result;
    }
}
