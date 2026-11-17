using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Tests.TestSupport;
using static CaseAgents.Tests.Core.CoreTestData;

namespace CaseAgents.Tests.Core;

public class QuestionRulesTests
{
    private static readonly Questionnaire Q = Scenario.Questionnaire();

    [Fact]
    public void Q4_dependents_are_parent_unconfirmed_until_Q4_has_a_final_answer()
    {
        Assert.All(Q.DependentsOf("Q4"), d => Assert.Equal(Applicability.ParentUnconfirmed, QuestionRules.GetApplicability(Q, d, [])));
        Assert.Equal(Applicability.Applicable, QuestionRules.GetApplicability(Q, Q.Find("Q4")!, []));
    }

    [Fact]
    public void Q4_dependents_are_not_applicable_when_Q4_is_No()
    {
        Assert.All(Q.DependentsOf("Q4"), d => Assert.Equal(Applicability.NotApplicable, QuestionRules.GetApplicability(Q, d, [Final("Q4", "No")])));
    }

    [Fact]
    public void Q4_dependents_are_applicable_when_Q4_is_Yes()
    {
        Assert.All(Q.DependentsOf("Q4"), d => Assert.Equal(Applicability.Applicable, QuestionRules.GetApplicability(Q, d, [Final("Q4", "Yes")])));
    }

    [Fact]
    public void Grandchild_of_a_not_applicable_question_is_not_applicable()
    {
        var q = QuestionnaireLoader.Parse("""
            {"promptVersion":"v1","questions":[
              {"id":"A","text":"a","type":"yes_no"},
              {"id":"B","text":"b","type":"yes_no","dependsOn":{"questionId":"A","value":"Yes"}},
              {"id":"C","text":"c","type":"free_text","dependsOn":{"questionId":"B","value":"Yes"}}]}
            """);
        var c = q.Find("C")!;

        Assert.Equal(Applicability.NotApplicable, QuestionRules.GetApplicability(q, c, [Final("A", "No")]));
        Assert.Equal(Applicability.ParentUnconfirmed, QuestionRules.GetApplicability(q, c, [Final("A", "Yes")]));
        Assert.Equal(Applicability.Applicable, QuestionRules.GetApplicability(q, c, [Final("A", "Yes"), Final("B", "Yes")]));
        Assert.Null(QuestionRules.NextToPropose(q, [Final("A", "No")], []));
    }

    [Fact]
    public void NextToPropose_follows_questionnaire_order()
    {
        Assert.Equal("Q1", QuestionRules.NextToPropose(Q, [], [])!.Id);
        Assert.Equal("Q2", QuestionRules.NextToPropose(Q, [], [Prop("Q1")])!.Id);
    }

    [Fact]
    public void NextToPropose_skips_questions_with_final_answer_or_active_proposal()
    {
        var next = QuestionRules.NextToPropose(Q, [Final("Q1", "Maria")], [Prop("Q2"), Prop("Q3", ProposalStatus.NeedsAnalyst, null)]);

        Assert.Equal("Q4", next!.Id);
    }

    [Fact]
    public void NextToPropose_skips_accepted_and_rejected_proposals()
    {
        var next = QuestionRules.NextToPropose(Q, [], [Prop("Q1", ProposalStatus.Accepted), Prop("Q2", ProposalStatus.Rejected)]);

        Assert.Equal("Q3", next!.Id);
    }

    [Fact]
    public void NextToPropose_reproposes_withdrawn_when_applicable_again()
    {
        var all = Q.Questions.Where(x => x.Id is not "Q4.1").Select(x => Prop(x.Id)).Append(Prop("Q4.1", ProposalStatus.Withdrawn)).ToList();

        Assert.Equal("Q4.1", QuestionRules.NextToPropose(Q, [Final("Q4", "Yes")], all)!.Id);
    }

    [Fact]
    public void NextToPropose_skips_not_applicable_dependents()
    {
        var proposals = new[] { "Q1", "Q2", "Q3", "Q4" }.Select(id => Prop(id)).ToList();

        Assert.Equal("Q5", QuestionRules.NextToPropose(Q, [Final("Q4", "No")], proposals)!.Id);
    }

    [Fact]
    public void NextToPropose_returns_null_when_done()
    {
        var proposals = Q.Questions.Select(x => Prop(x.Id)).ToList();

        Assert.Null(QuestionRules.NextToPropose(Q, [], proposals));
    }

    [Fact]
    public void DependentActions_withdraw_Q4_1_to_Q4_3_when_Q4_is_No()
    {
        var proposals = new[] { Prop("Q4.1", dependsOnUnconfirmed: true), Prop("Q4.2", dependsOnUnconfirmed: true), Prop("Q4.3", ProposalStatus.NeedsAnalyst, null, true) };

        var actions = QuestionRules.DependentActions(Q, "Q4", [Final("Q4", "No")], proposals);

        Assert.Equal(new[] { "Q4.1", "Q4.2", "Q4.3" }, actions.Select(a => a.Question.Id));
        Assert.All(actions, a => Assert.Equal(DependentActionKind.Withdraw, a.Kind));
        Assert.Equal("Q4 confirmed as No; Q4.1 applies only if Q4 = Yes", actions[0].Reason);
    }

    [Fact]
    public void DependentActions_confirm_flagged_dependents_when_Q4_is_Yes()
    {
        var proposals = new[] { Prop("Q4.1", dependsOnUnconfirmed: true), Prop("Q4.2") };

        var actions = QuestionRules.DependentActions(Q, "Q4", [Final("Q4", "Yes")], proposals);

        var action = Assert.Single(actions);
        Assert.Equal("Q4.1", action.Question.Id);
        Assert.Equal(DependentActionKind.Confirm, action.Kind);
        Assert.Equal("Q4 confirmed as Yes", action.Reason);
    }

    [Fact]
    public void DependentActions_ignore_dependents_with_final_answers_or_rejected()
    {
        var proposals = new[] { Prop("Q4.1", dependsOnUnconfirmed: true), Prop("Q4.2", ProposalStatus.Rejected), Prop("Q4.3", ProposalStatus.Withdrawn) };

        var actions = QuestionRules.DependentActions(Q, "Q4", [Final("Q4", "No"), Final("Q4.1", "x")], proposals);

        Assert.Empty(actions);
    }

    [Fact]
    public void DependentActions_are_empty_when_parent_has_no_final_answer()
    {
        Assert.Empty(QuestionRules.DependentActions(Q, "Q4", [], [Prop("Q4.1", dependsOnUnconfirmed: true)]));
    }
}
