using CaseAgents.Core.Models;

namespace CaseAgents.Eval;

/// <summary>
/// Metrics for one question type (or "all"). Precision and the rate are NaN when their denominator is 0.
/// </summary>
public sealed record EvalRow(
    string QuestionType,
    int Proposals,
    int Correct,
    double Precision,
    int Decided,
    int AcceptedUnchanged,
    double AcceptedUnchangedRate);

public static class EvalCalculator
{
    public const string All = "all";

    /// <summary>One row per question type (yes_no, free_text) and a final "all" row.</summary>
    public static IReadOnlyList<EvalRow> Compute(Questionnaire questionnaire, IReadOnlyList<CaseRecord> records)
    {
        var rows = new List<EvalRow>();
        foreach (var type in new[] { QuestionTypes.YesNo, QuestionTypes.FreeText })
            rows.Add(Build(type, questionnaire, records, type));
        rows.Add(Build(All, questionnaire, records, null));
        return rows;
    }

    private static EvalRow Build(string label, Questionnaire questionnaire, IReadOnlyList<CaseRecord> records, string? type)
    {
        var proposals = 0;
        var correct = 0;
        var decided = 0;
        var acceptedUnchanged = 0;

        foreach (var record in records)
        {
            foreach (var proposal in record.Proposals)
            {
                var question = questionnaire.Find(proposal.QuestionId);
                if (question is null || (type is not null && question.Type != type))
                    continue;

                // Precision: any proposal with a value (shadow included) against the analyst's final answer.
                var final = record.Answers.FirstOrDefault(a => a.QuestionId == proposal.QuestionId);
                if (proposal.Value is not null && proposal.Status != ProposalStatus.Withdrawn && final is not null)
                {
                    proposals++;
                    if (string.Equals(proposal.Value.Trim(), final.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                        correct++;
                }

                // Decisions: only what analysts actually saw (shadow proposals are never shown).
                if (!proposal.Shadow && proposal.Status is ProposalStatus.Accepted or ProposalStatus.Rejected)
                {
                    decided++;
                    if (proposal.Status == ProposalStatus.Accepted)
                        acceptedUnchanged++;
                }
            }
        }

        return new EvalRow(label, proposals, correct, Ratio(correct, proposals), decided, acceptedUnchanged, Ratio(acceptedUnchanged, decided));
    }

    private static double Ratio(int part, int whole) => whole == 0 ? double.NaN : (double)part / whole;
}
