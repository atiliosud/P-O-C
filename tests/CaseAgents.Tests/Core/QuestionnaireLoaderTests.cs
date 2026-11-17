using CaseAgents.Core.Models;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Core;

public class QuestionnaireLoaderTests
{
    private static string Json(string questions, string header = "\"promptVersion\":\"v1\",\"proposalThreshold\":0.6,\"highConfidence\":0.85") =>
        "{" + header + ",\"questions\":[" + questions + "]}";

    private const string A = "{\"id\":\"A\",\"text\":\"a\",\"type\":\"yes_no\"}";

    [Fact]
    public void QuestionnaireLoader_rejects_dependency_on_later_question()
    {
        var json = Json("{\"id\":\"B\",\"text\":\"b\",\"type\":\"free_text\",\"dependsOn\":{\"questionId\":\"A\",\"value\":\"Yes\"}}," + A);

        var ex = Assert.Throws<InvalidOperationException>(() => QuestionnaireLoader.Parse(json));
        Assert.Contains("earlier question", ex.Message);
    }

    [Fact]
    public void QuestionnaireLoader_rejects_duplicate_ids()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => QuestionnaireLoader.Parse(Json(A + "," + A)));
        Assert.Contains("duplicate", ex.Message);
    }

    [Fact]
    public void QuestionnaireLoader_rejects_unknown_type()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => QuestionnaireLoader.Parse(Json("{\"id\":\"A\",\"text\":\"a\",\"type\":\"number\"}")));
        Assert.Contains("unknown type", ex.Message);
    }

    [Fact]
    public void QuestionnaireLoader_rejects_bad_thresholds()
    {
        var header = "\"promptVersion\":\"v1\",\"proposalThreshold\":0.9,\"highConfidence\":0.8";

        var ex = Assert.Throws<InvalidOperationException>(() => QuestionnaireLoader.Parse(Json(A, header)));
        Assert.Contains("thresholds", ex.Message);
    }

    [Fact]
    public void QuestionnaireLoader_loads_seed_questionnaire()
    {
        var q = Scenario.Questionnaire();

        Assert.Equal(9, q.Questions.Count);
        Assert.Equal(4, q.RequiredDocuments.Count);
        Assert.Equal(0.60, q.ProposalThreshold);
        Assert.Equal(0.85, q.HighConfidence);
    }
}
