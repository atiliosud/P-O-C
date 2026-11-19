using CaseAgents.Core.Models;

namespace CaseAgents.Core.Rules;

/// <summary>Builds what the reviewer app shows for a case.</summary>
public static class CaseViewBuilder
{
    public static CaseView Build(Questionnaire q, CaseRecord record, bool aiPending, bool includeShadow)
    {
        var states = q.Questions.Select(question =>
        {
            var proposal = record.Proposals.FirstOrDefault(p => p.QuestionId == question.Id);
            if (proposal is { Shadow: true } && !includeShadow)
                proposal = null;

            return new QuestionState(
                question,
                QuestionRules.GetApplicability(q, question, record.Answers),
                record.Answers.FirstOrDefault(a => a.QuestionId == question.Id),
                proposal);
        }).ToList();

        return new CaseView(record.Case, record.Documents, states, GapFinder.Find(q, record), aiPending, q.PromptVersion);
    }

    /// <summary>
    /// Answered counts only final answers on questions that still apply. A final answer under a parent that was later
    /// changed (Q4.1 after Q4 moves to No) is kept, never deleted, but it no longer counts: see <see cref="QuestionRules.IsSetAside"/>.
    /// </summary>
    public static CaseSummary Summarize(Questionnaire q, CaseRecord record)
    {
        var visible = record.Proposals.Where(p => !p.Shadow).ToList();
        return new CaseSummary(
            record.Case,
            q.Questions.Count,
            record.Answers.Count(a => !QuestionRules.IsSetAside(q, a, record.Answers)),
            visible.Count(p => p.Status == ProposalStatus.Proposed),
            visible.Count(p => p.Status == ProposalStatus.NeedsAnalyst));
    }
}
