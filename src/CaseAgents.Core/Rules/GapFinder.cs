using CaseAgents.Core.Models;

namespace CaseAgents.Core.Rules;

/// <summary>
/// Reports what is missing: required documents, and required applicable questions without a final answer whose
/// (non-shadow) proposal is NeedsAnalyst. A question with no proposal yet is not a gap: the AI may still be working on it.
/// </summary>
public static class GapFinder
{
    public static IReadOnlyList<Gap> Find(Questionnaire q, CaseRecord record)
    {
        var gaps = new List<Gap>();

        foreach (var required in q.RequiredDocuments)
        {
            if (!record.Documents.Any(d => d.Kind == required.Kind))
                gaps.Add(new Gap(GapKind.MissingDocument, $"Missing required document: {required.Name}", DocumentKind: required.Kind));
        }

        foreach (var question in q.Questions.Where(x => x.Required))
        {
            if (QuestionRules.GetApplicability(q, question, record.Answers) == Applicability.NotApplicable)
                continue;
            if (record.Answers.Any(a => a.QuestionId == question.Id))
                continue;

            var proposal = record.Proposals.FirstOrDefault(p => p.QuestionId == question.Id && !p.Shadow);
            if (proposal is { Status: ProposalStatus.NeedsAnalyst })
                gaps.Add(new Gap(GapKind.NoEvidence, $"{question.Id}: no evidence found in the documents", QuestionId: question.Id));
        }

        return gaps;
    }
}
