using CaseAgents.Core.Models;

namespace CaseAgents.Tests.TestSupport;

public class ScenarioTests
{
    [Fact]
    public void Questionnaire_has_Q4_with_three_dependents_and_prompt_v12()
    {
        var q = Scenario.Questionnaire();

        Assert.Equal("v12", q.PromptVersion);
        Assert.Equal("Does the client hold accounts outside the EU?", q.Find("Q4")!.Text);
        Assert.Equal(new[] { "Q4.1", "Q4.2", "Q4.3" }, q.DependentsOf("Q4").Select(d => d.Id));
        Assert.All(q.DependentsOf("Q4"), d => Assert.Equal(YesNo.Yes, d.DependsOn!.Value));
    }

    [Fact]
    public void Bank_letter_page_3_contains_the_closed_account_line()
    {
        var letter = Scenario.Documents("c1").Single(d => d.DocumentId == Scenario.BankLetterId);

        Assert.Contains(Scenario.BankLetterPage3Line, letter.Pages.Single(p => p.N == 3).Text);
    }
}
