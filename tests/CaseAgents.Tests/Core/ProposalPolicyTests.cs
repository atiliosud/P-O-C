using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Core;

public class ProposalPolicyTests
{
    private static readonly Questionnaire Q = Scenario.Questionnaire();
    private static readonly Question Q4 = Q.Find("Q4")!;
    private static readonly Question Q1 = Q.Find("Q1")!;
    private static readonly IReadOnlyList<CaseDocument> Docs = Scenario.Documents("c1");

    private static AnswerDraft Draft(string? value = "Yes", string? quote = Scenario.BankLetterPage3Line, double? confidence = 0.93, bool valid = true) =>
        new(valid, value, quote, Scenario.BankLetterId, 3, confidence, "fake-answerer-1");

    private static EvidenceRef? Evidence(string quote = Scenario.BankLetterPage3Line) =>
        EvidenceLocator.Locate(Docs, Scenario.BankLetterId, 3, quote);

    private static VerifierResult Verifier(bool supports = true, double score = 0.90) => new(supports, score, "fake-verifier-1");

    private static void AssertNeedsAnalyst(ProposalDecision d, string reason)
    {
        Assert.Equal(ProposalStatus.NeedsAnalyst, d.Status);
        Assert.Null(d.Value);
        Assert.Null(d.Confidence);
        Assert.Null(d.Band);
        Assert.Equal(reason, d.NeedsAnalystReason);
    }

    [Fact]
    public void Band_boundaries_are_inclusive()
    {
        Assert.Equal(ConfidenceBand.High, ProposalPolicy.Band(Q, 0.85));
        Assert.Equal(ConfidenceBand.Medium, ProposalPolicy.Band(Q, 0.60));
        Assert.Equal(ConfidenceBand.Low, ProposalPolicy.Band(Q, 0.59));
    }

    [Fact]
    public void Decide_proposes_Q4_yes_with_high_confidence_when_quote_found_and_verifier_passes()
    {
        var d = ProposalPolicy.Decide(Q, Q4, Draft(), Evidence(), Verifier());

        Assert.Equal(ProposalStatus.Proposed, d.Status);
        Assert.Equal("Yes", d.Value);
        Assert.Equal(0.93, d.Confidence);
        Assert.Equal(ConfidenceBand.High, d.Band);
        Assert.True(d.VerifierPassed);
        Assert.Equal(Scenario.BankLetterPage3Line, d.Quote);
        Assert.Equal(Scenario.BankLetterId, d.DocumentId);
        Assert.Equal(3, d.Page);
        Assert.NotNull(d.Evidence);
        Assert.Null(d.NeedsAnalystReason);
    }

    [Fact]
    public void Decide_needs_analyst_without_value_when_schema_invalid()
    {
        var draft = new AnswerDraft(false, null, null, null, null, null, "m", "Model output did not match the answer schema");

        AssertNeedsAnalyst(ProposalPolicy.Decide(Q, Q4, draft, null, null), "Model output did not match the answer schema");
    }

    [Fact]
    public void Decide_needs_analyst_without_value_when_no_answer_uses_draft_error_or_default()
    {
        AssertNeedsAnalyst(ProposalPolicy.Decide(Q, Q4, Draft(value: null, quote: null), null, null), "No answer found in the documents");
        var withError = Draft(value: null, quote: null) with { Error = "No passage mentions the question keywords" };
        AssertNeedsAnalyst(ProposalPolicy.Decide(Q, Q4, withError, null, null), "No passage mentions the question keywords");
    }

    [Fact]
    public void Decide_needs_analyst_without_value_when_no_quote()
    {
        AssertNeedsAnalyst(ProposalPolicy.Decide(Q, Q4, Draft(quote: ""), null, Verifier()), "No quote supporting the answer");
    }

    [Fact]
    public void Decide_needs_analyst_without_value_when_quote_not_found()
    {
        var d = ProposalPolicy.Decide(Q, Q4, Draft(quote: "invented sentence"), null, Verifier());

        AssertNeedsAnalyst(d, "Quote not found on the cited page");
        Assert.Null(d.Quote);
        Assert.Null(d.Evidence);
    }

    [Fact]
    public void Decide_needs_analyst_without_value_when_verifier_rejects()
    {
        var d = ProposalPolicy.Decide(Q, Q4, Draft(), Evidence(), Verifier(supports: false, score: 0.1));

        AssertNeedsAnalyst(d, "Verifier: the quote does not support the answer");
        Assert.False(d.VerifierPassed);
        Assert.Equal(Scenario.BankLetterPage3Line, d.Quote);
        Assert.NotNull(d.Evidence);
    }

    [Fact]
    public void Decide_needs_analyst_without_value_when_confidence_below_threshold()
    {
        AssertNeedsAnalyst(ProposalPolicy.Decide(Q, Q4, Draft(confidence: 0.55), Evidence(), Verifier()), "Low confidence (0.55)");
        AssertNeedsAnalyst(ProposalPolicy.Decide(Q, Q4, Draft(confidence: null), Evidence(), Verifier()), "Low confidence (0.00)");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void Decide_treats_invalid_confidence_as_needs_analyst(double confidence)
    {
        AssertNeedsAnalyst(ProposalPolicy.Decide(Q, Q4, Draft(confidence: confidence), Evidence(), Verifier()), "Invalid confidence");
    }

    [Fact]
    public void Decide_low_confidence_reason_ignores_the_current_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
            AssertNeedsAnalyst(ProposalPolicy.Decide(Q, Q4, Draft(confidence: 0.55), Evidence(), Verifier()), "Low confidence (0.55)");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void Decide_uses_verifier_score_for_free_text()
    {
        var draft = Draft(value: "Switzerland", confidence: null);

        var ok = ProposalPolicy.Decide(Q, Q1, draft, Evidence(), Verifier(score: 0.90));
        var low = ProposalPolicy.Decide(Q, Q1, draft, Evidence(), Verifier(score: 0.50));

        Assert.Equal(ProposalStatus.Proposed, ok.Status);
        Assert.Equal(0.90, ok.Confidence);
        Assert.Equal(ConfidenceBand.High, ok.Band);
        AssertNeedsAnalyst(low, "Low confidence (0.50)");
    }

    [Fact]
    public void Decide_rejects_yes_no_value_other_than_yes_or_no()
    {
        AssertNeedsAnalyst(ProposalPolicy.Decide(Q, Q4, Draft(value: "Maybe"), Evidence(), Verifier()), "Model output did not match the answer schema");
    }
}
