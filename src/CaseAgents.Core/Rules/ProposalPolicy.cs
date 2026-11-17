using System.Globalization;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;

namespace CaseAgents.Core.Rules;

/// <summary>Turns an answer draft plus the checks into what the agent may write. A value only appears when every check passed.</summary>
public static class ProposalPolicy
{
    private const string SchemaReason = "Model output did not match the answer schema";

    public static ConfidenceBand Band(Questionnaire q, double confidence) =>
        confidence >= q.HighConfidence ? ConfidenceBand.High
        : confidence >= q.ProposalThreshold ? ConfidenceBand.Medium
        : ConfidenceBand.Low;

    public static ProposalDecision Decide(
        Questionnaire q, Question question, AnswerDraft draft, EvidenceRef? evidence, VerifierResult? verifier)
    {
        var verifierPassed = verifier?.Supports == true;

        ProposalDecision NeedsAnalyst(string reason) => new(
            ProposalStatus.NeedsAnalyst, null, null, null, verifierPassed,
            evidence is null ? null : draft.Quote,
            evidence is null ? null : draft.DocumentId,
            evidence is null ? null : draft.Page,
            evidence, reason);

        if (!draft.SchemaValid)
            return NeedsAnalyst(SchemaReason);
        if (string.IsNullOrWhiteSpace(draft.Value))
            return NeedsAnalyst(draft.Error ?? "No answer found in the documents");
        if (string.IsNullOrWhiteSpace(draft.Quote))
            return NeedsAnalyst("No quote supporting the answer");
        if (question.Type == QuestionTypes.YesNo && draft.Value is not (YesNo.Yes or YesNo.No))
            return NeedsAnalyst(SchemaReason);
        if (evidence is null)
            return NeedsAnalyst("Quote not found on the cited page");
        if (verifier is null || !verifier.Supports)
            return NeedsAnalyst("Verifier: the quote does not support the answer");

        var confidence = question.Type == QuestionTypes.YesNo ? draft.TokenConfidence : verifier.Score;
        if (confidence is not { } c || double.IsNaN(c) || double.IsInfinity(c) || c < 0 || c > 1)
            return NeedsAnalyst(confidence is null ? "Low confidence (0.00)" : "Invalid confidence");
        if (c < q.ProposalThreshold)
            return NeedsAnalyst(string.Create(CultureInfo.InvariantCulture, $"Low confidence ({c:0.00})"));

        return new ProposalDecision(
            ProposalStatus.Proposed, draft.Value, c, Band(q, c), verifierPassed,
            draft.Quote, draft.DocumentId, draft.Page, evidence, null);
    }
}
