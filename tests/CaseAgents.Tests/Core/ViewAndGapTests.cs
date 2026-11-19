using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Tests.TestSupport;
using static CaseAgents.Tests.Core.CoreTestData;

namespace CaseAgents.Tests.Core;

public class ViewAndGapTests
{
    private static readonly Questionnaire Q = Scenario.Questionnaire();

    [Fact]
    public void Gaps_report_missing_source_of_funds_document()
    {
        var gaps = GapFinder.Find(Q, Record());

        var gap = Assert.Single(gaps, g => g.Kind == GapKind.MissingDocument);
        Assert.Equal("Missing required document: Source of funds evidence", gap.Message);
    }

    [Fact]
    public void Gaps_report_required_question_without_evidence()
    {
        var gaps = GapFinder.Find(Q, Record(proposals: [Prop("Q1"), Prop("Q2", ProposalStatus.NeedsAnalyst, null)]));

        Assert.DoesNotContain(gaps, g => g.QuestionId == "Q1");
        var gap = Assert.Single(gaps, g => g.Kind == GapKind.NoEvidence);
        Assert.Equal("Q2", gap.QuestionId);
        Assert.Equal("Q2: no evidence found in the documents", gap.Message);
    }

    [Fact]
    public void Gaps_do_not_report_questions_still_waiting_for_a_proposal()
    {
        var gaps = GapFinder.Find(Q, Record());

        Assert.DoesNotContain(gaps, g => g.Kind == GapKind.NoEvidence);
    }

    [Fact]
    public void Gaps_report_no_evidence_only_for_needs_analyst_proposals()
    {
        var gaps = GapFinder.Find(Q, Record(proposals:
        [
            Prop("Q1", ProposalStatus.Withdrawn),
            Prop("Q2", ProposalStatus.Rejected),
            Prop("Q3", ProposalStatus.NeedsAnalyst, null),
        ]));

        Assert.Equal(["Q3"], gaps.Where(g => g.Kind == GapKind.NoEvidence).Select(g => g.QuestionId));
    }

    [Fact]
    public void Gaps_ignore_needs_analyst_questions_that_already_have_a_final_answer()
    {
        var gaps = GapFinder.Find(Q, Record(
            answers: [Final("Q3", "No")],
            proposals: [Prop("Q3", ProposalStatus.NeedsAnalyst, null)]));

        Assert.DoesNotContain(gaps, g => g.QuestionId == "Q3");
    }

    [Fact]
    public void Gaps_ignore_not_applicable_dependents()
    {
        var gaps = GapFinder.Find(Q, Record(answers: [Final("Q4", "No")]));

        Assert.DoesNotContain(gaps, g => g.QuestionId is "Q4.1" or "Q4.2" or "Q4.3");
        Assert.DoesNotContain(gaps, g => g.QuestionId == "Q4");
    }

    [Fact]
    public void Gaps_ignore_shadow_proposals_when_judging_evidence()
    {
        // A shadow NeedsAnalyst proposal is never shown to analysts, so it does not make a gap either.
        var gaps = GapFinder.Find(Q, Record(proposals: [Prop("Q1", ProposalStatus.NeedsAnalyst, null, shadow: true)]));

        Assert.DoesNotContain(gaps, g => g.QuestionId == "Q1");
    }

    [Fact]
    public void Index_finds_passages_only_from_this_case_in_document_and_page_order()
    {
        var index = CaseIndex.Build(Scenario.Documents("c1"));

        var passages = index.FindPassages(["Account country", "closing balance", "ACCOUNT HELD WITH"], max: 10);

        Assert.Equal(new[] { 1, 2, 3 }, passages.Select(p => p.Page));
        Assert.All(passages, p => Assert.Equal(Scenario.BankLetterId, p.DocumentId));
        Assert.Contains("Closing balance", passages[1].Text);
        Assert.Empty(index.FindPassages(["Net salary"]));
    }

    [Fact]
    public void Index_respects_max()
    {
        var index = CaseIndex.Build(Scenario.Documents("c1"));

        Assert.Equal(2, index.FindPassages(["account"], max: 2).Count);
        Assert.Equal(3, index.FindPassages(["account"]).Count);
    }

    [Fact]
    public void View_hides_shadow_proposals_from_analysts_but_not_from_agents()
    {
        var record = Record(proposals: [Prop("Q1", shadow: true), Prop("Q2")]);

        var analyst = CaseViewBuilder.Build(Q, record, aiPending: false, includeShadow: false);
        var agent = CaseViewBuilder.Build(Q, record, aiPending: true, includeShadow: true);

        Assert.Null(analyst.Questions[0].Proposal);
        Assert.NotNull(analyst.Questions[1].Proposal);
        Assert.NotNull(agent.Questions[0].Proposal);
        Assert.True(agent.AiPending);
        Assert.Equal(Q.Questions.Select(x => x.Id), analyst.Questions.Select(x => x.Question.Id));
        Assert.Equal("v12", analyst.PromptVersion);
    }

    [Fact]
    public void View_marks_applicability_and_answers()
    {
        var view = CaseViewBuilder.Build(Q, Record(answers: [Final("Q4", "No")]), false, false);

        Assert.Equal("No", view.Questions.Single(s => s.Question.Id == "Q4").Answer!.Value);
        Assert.Equal(Applicability.NotApplicable, view.Questions.Single(s => s.Question.Id == "Q4.1").Applicability);
    }

    [Fact]
    public void Summary_counts_answers_open_proposals_and_needs_analyst()
    {
        var record = Record(
            answers: [Final("Q1", "Maria")],
            proposals: [Prop("Q2"), Prop("Q3"), Prop("Q4", shadow: true), Prop("Q5", ProposalStatus.NeedsAnalyst, null), Prop("Q6", ProposalStatus.Accepted)]);

        var summary = CaseViewBuilder.Summarize(Q, record);

        Assert.Equal(Q.Questions.Count, summary.Questions);
        Assert.Equal(1, summary.Answered);
        Assert.Equal(2, summary.OpenProposals);
        Assert.Equal(1, summary.NeedsAnalyst);
    }

    [Fact]
    public void Final_answer_under_a_parent_changed_later_is_kept_but_set_aside()
    {
        // Q4.1 was answered while Q4 was Yes; Q4 was then corrected to No.
        var answers = new[] { Final("Q4", "No"), Final("Q4.1", "Switzerland") };
        var record = Record(answers: answers);

        var view = CaseViewBuilder.Build(Q, record, false, false);
        var summary = CaseViewBuilder.Summarize(Q, record);

        var q41 = view.Questions.Single(s => s.Question.Id == "Q4.1");
        Assert.Equal(Applicability.NotApplicable, q41.Applicability);
        Assert.Equal("Switzerland", q41.Answer?.Value);
        Assert.True(QuestionRules.IsSetAside(Q, answers[1], answers));
        Assert.False(QuestionRules.IsSetAside(Q, answers[0], answers));
        Assert.Equal(1, summary.Answered);
        Assert.DoesNotContain(GapFinder.Find(Q, record), g => g.QuestionId == "Q4.1");
    }

    [Fact]
    public void Set_aside_answer_applies_again_when_the_parent_changes_back()
    {
        var answers = new[] { Final("Q4", "Yes"), Final("Q4.1", "Switzerland") };

        Assert.False(QuestionRules.IsSetAside(Q, answers[1], answers));
        Assert.Equal(2, CaseViewBuilder.Summarize(Q, Record(answers: answers)).Answered);
    }
}
