using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;

namespace CaseAgents.Agents.Models;

/// <summary>Answerer backed by an Azure OpenAI deployment: one call per question, strict JSON schema, logprob of Yes/No as confidence.</summary>
public sealed class AzureOpenAiAnswerer(AzureOpenAiChatClient client, Questionnaire questionnaire) : IAnswerer
{
    public async Task<AnswerDraft> AnswerAsync(Question question, IReadOnlyList<Passage> passages, CancellationToken ct = default)
    {
        var result = await client.CompleteAsync(
            client.Options.Deployment,
            AnswererPrompt.System(questionnaire.PromptVersion),
            AnswererPrompt.User(question, passages),
            AnswerSchema.JsonSchema,
            600,
            ct);

        var draft = AnswerSchema.Parse(result.Content, question, passages, null, result.Model);
        if (!draft.SchemaValid || question.Type != QuestionTypes.YesNo || draft.Value is null)
            return draft;

        // Confidence is the probability of the token that spells the parsed value; none matching leaves it null.
        foreach (var (token, logprob) in result.Logprobs)
        {
            if (token.Trim() == draft.Value)
                return draft with { TokenConfidence = Math.Exp(logprob) };
        }

        return draft;
    }
}
