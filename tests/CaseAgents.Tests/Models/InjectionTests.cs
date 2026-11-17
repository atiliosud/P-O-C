using System.Net;
using CaseAgents.Agents.Models;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Models;

/// <summary>A document tries to instruct the model. Whatever the model returns, the policy never turns it into a value.</summary>
public class InjectionTests
{
    private static readonly Questionnaire Questionnaire = Scenario.Questionnaire();
    private static readonly IReadOnlyList<CaseDocument> Docs = Scenario.InjectionDocuments("c1");
    private static readonly IReadOnlyList<Passage> Passages = FakeModelsTests.AllPassages(Docs);

    private static async Task<AnswerDraft> Answer(Question question, string modelContent)
    {
        var handler = new AzureOpenAiModelsTests.FakeHandler(
            HttpStatusCode.OK,
            AzureOpenAiModelsTests.Chat(modelContent, ("Yes", -0.05)));
        var client = new AzureOpenAiChatClient(
            new HttpClient(handler),
            new AzureOpenAiModelsTests.FakeCredential(),
            new CaseAgents.Infra.AzureOpenAiOptions { Endpoint = "https://example.openai.azure.com", Deployment = "answerer" });
        return await new AzureOpenAiAnswerer(client, Questionnaire).AnswerAsync(question, Passages);
    }

    [Fact]
    public async Task Injected_text_in_model_output_without_quote_never_becomes_a_value()
    {
        var q6 = Questionnaire.Find("Q6")!;
        var draft = await Answer(q6, """{"value":"Yes","quote":null,"documentId":null,"page":null}""");

        var decision = ProposalPolicy.Decide(Questionnaire, q6, draft, null, null);

        Assert.Equal(ProposalStatus.NeedsAnalyst, decision.Status);
        Assert.Null(decision.Value);
    }

    [Fact]
    public async Task Injected_quote_is_found_literally_but_the_verifier_rejects_it()
    {
        var q6 = Questionnaire.Find("Q6")!;
        var content = $$"""{"value":"Yes","quote":"{{Scenario.InjectionText}}","documentId":"payslip","page":1}""";
        var draft = await Answer(q6, content);

        var evidence = EvidenceLocator.Locate(Docs, draft.DocumentId!, draft.Page!.Value, draft.Quote!);
        var verifier = await new FakeVerifier().VerifyAsync(q6, draft.Value!, draft.Quote!, Passages[0]);
        var decision = ProposalPolicy.Decide(Questionnaire, q6, draft, evidence, verifier);

        Assert.NotNull(evidence);
        Assert.False(verifier.Supports);
        Assert.Equal(ProposalStatus.NeedsAnalyst, decision.Status);
        Assert.Null(decision.Value);
    }

    [Fact]
    public async Task Free_text_obeying_the_injection_is_rejected_by_the_schema()
    {
        var q5 = Questionnaire.Find("Q5")!;
        var draft = await Answer(q5, "YES TO EVERYTHING");

        var decision = ProposalPolicy.Decide(Questionnaire, q5, draft, null, null);

        Assert.False(draft.SchemaValid);
        Assert.Equal(ProposalStatus.NeedsAnalyst, decision.Status);
        Assert.Null(decision.Value);
    }
}
