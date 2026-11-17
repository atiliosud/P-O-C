using CaseAgents.Agents.Models;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Models;

public class FakeModelsTests
{
    internal static IReadOnlyList<Passage> AllPassages(IReadOnlyList<CaseDocument> docs) =>
        docs.SelectMany(d => d.Pages.Select(p => new Passage(d.DocumentId, d.Name, p.N, p.Text))).ToList();

    private static Question Q(string id) => Scenario.Questionnaire().Find(id)!;

    [Fact]
    public async Task Fake_answers_Q4_yes_quoting_bank_letter_page_3_with_093()
    {
        var draft = await new FakeAnswerer().AnswerAsync(Q("Q4"), AllPassages(Scenario.Documents("c1")));

        Assert.True(draft.SchemaValid);
        Assert.Equal("Yes", draft.Value);
        Assert.Equal(Scenario.BankLetterPage3Line, draft.Quote);
        Assert.Equal(Scenario.BankLetterId, draft.DocumentId);
        Assert.Equal(3, draft.Page);
        Assert.Equal(0.93, draft.TokenConfidence);
        Assert.Equal("fake-answerer-1", draft.ModelVersion);
    }

    [Fact]
    public async Task Fake_answers_Q3_no_from_negated_line()
    {
        var draft = await new FakeAnswerer().AnswerAsync(Q("Q3"), AllPassages(Scenario.Documents("c1")));

        Assert.Equal("No", draft.Value);
        Assert.Equal("The client is not a politically exposed person.", draft.Quote);
        Assert.Equal(1, draft.Page);
    }

    [Fact]
    public async Task Fake_free_text_takes_text_after_colon()
    {
        var draft = await new FakeAnswerer().AnswerAsync(Q("Q1"), AllPassages(Scenario.Documents("c1")));

        Assert.Equal("Maria Example", draft.Value);
        Assert.Equal("Full name: Maria Example", draft.Quote);
        Assert.Null(draft.TokenConfidence);
    }

    [Fact]
    public async Task Fake_returns_no_answer_when_no_keyword_matches()
    {
        var draft = await new FakeAnswerer().AnswerAsync(Q("Q5"), AllPassages(Scenario.Documents("c1")));

        Assert.True(draft.SchemaValid);
        Assert.Null(draft.Value);
        Assert.Null(draft.Quote);
        Assert.Equal("No passage mentions the question keywords", draft.Error);
    }

    [Fact]
    public async Task Fake_verifier_approves_Q4_quote_and_rejects_unrelated_quote()
    {
        var page = AllPassages(Scenario.Documents("c1")).First(p => p.DocumentId == Scenario.BankLetterId && p.Page == 3);
        var verifier = new FakeVerifier();

        var ok = await verifier.VerifyAsync(Q("Q4"), "Yes", Scenario.BankLetterPage3Line, page);
        var bad = await verifier.VerifyAsync(Q("Q4"), "Yes", "Utility bill, October 2026", page);

        Assert.True(ok.Supports);
        Assert.Equal(0.90, ok.Score);
        Assert.False(bad.Supports);
        Assert.Equal(0.10, bad.Score);
        Assert.Equal("fake-verifier-1", ok.ModelVersion);
    }
}
