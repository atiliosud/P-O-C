using System.Text.Json;

namespace CaseAgents.Core.Models;

/// <param name="QuestionId">The parent question.</param>
/// <param name="Value">The parent's final value for which this question applies (e.g. "Yes").</param>
public sealed record QuestionDependency(string QuestionId, string Value);

public sealed record Question
{
    public required string Id { get; init; }
    public required string Text { get; init; }

    /// <summary><see cref="QuestionTypes.YesNo"/> or <see cref="QuestionTypes.FreeText"/>.</summary>
    public required string Type { get; init; }

    public bool Required { get; init; }
    public QuestionDependency? DependsOn { get; init; }

    /// <summary>Words that locate candidate passages in the case index (and drive the Fake answerer).</summary>
    public IReadOnlyList<string> EvidenceKeywords { get; init; } = [];
}

public sealed record RequiredDocument(string Kind, string Name);

/// <summary>The questionnaire (config/questionnaire.json). Question order in the file is the asking order.</summary>
public sealed record Questionnaire
{
    /// <summary>Version of the answerer prompt, recorded on every proposal (seed: "v12").</summary>
    public required string PromptVersion { get; init; }

    /// <summary>Below this confidence a proposal becomes NeedsAnalyst with no value.</summary>
    public double ProposalThreshold { get; init; } = 0.60;

    /// <summary>At or above this confidence the band is High.</summary>
    public double HighConfidence { get; init; } = 0.85;

    public IReadOnlyList<RequiredDocument> RequiredDocuments { get; init; } = [];
    public required IReadOnlyList<Question> Questions { get; init; }

    public Question? Find(string? questionId) =>
        Questions.FirstOrDefault(q => string.Equals(q.Id, questionId, StringComparison.Ordinal));

    /// <summary>Questions whose dependsOn points at <paramref name="parentId"/>, in questionnaire order.</summary>
    public IReadOnlyList<Question> DependentsOf(string parentId) =>
        Questions.Where(q => string.Equals(q.DependsOn?.QuestionId, parentId, StringComparison.Ordinal)).ToList();
}

/// <summary>Loads and validates questionnaire.json. Invalid files fail at startup with a clear message.</summary>
public static class QuestionnaireLoader
{
    public static Questionnaire Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Questionnaire file not found: {path}");
        return Parse(File.ReadAllText(path));
    }

    public static Questionnaire Parse(string json)
    {
        Questionnaire? questionnaire;
        try
        {
            questionnaire = JsonSerializer.Deserialize<Questionnaire>(json, CaseAgentsJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Questionnaire is not valid JSON: {ex.Message}", ex);
        }

        if (questionnaire is null)
            throw new InvalidOperationException("Questionnaire is empty.");
        Validate(questionnaire);
        return questionnaire;
    }

    /// <summary>
    /// Rules: at least one question; unique non-empty ids; type yes_no or free_text; dependsOn refers to an EARLIER
    /// yes_no question and its value is Yes or No; 0 &lt; proposalThreshold &lt;= highConfidence &lt;= 1; promptVersion set.
    /// </summary>
    public static void Validate(Questionnaire q)
    {
        if (string.IsNullOrWhiteSpace(q.PromptVersion))
            throw new InvalidOperationException("Questionnaire: promptVersion is required.");
        if (q.Questions is null || q.Questions.Count == 0)
            throw new InvalidOperationException("Questionnaire: at least one question is required.");
        if (q.ProposalThreshold <= 0 || q.ProposalThreshold > q.HighConfidence || q.HighConfidence > 1)
            throw new InvalidOperationException("Questionnaire: thresholds must satisfy 0 < proposalThreshold <= highConfidence <= 1.");

        var seen = new Dictionary<string, Question>(StringComparer.Ordinal);
        foreach (var question in q.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.Id))
                throw new InvalidOperationException("Questionnaire: every question needs an id.");
            if (seen.ContainsKey(question.Id))
                throw new InvalidOperationException($"Questionnaire: duplicate question id '{question.Id}'.");
            if (question.Type is not (QuestionTypes.YesNo or QuestionTypes.FreeText))
                throw new InvalidOperationException($"Questionnaire: question '{question.Id}' has unknown type '{question.Type}'.");

            if (question.DependsOn is { } dep)
            {
                if (!seen.TryGetValue(dep.QuestionId, out var parent))
                    throw new InvalidOperationException($"Questionnaire: question '{question.Id}' depends on '{dep.QuestionId}', which must be an earlier question.");
                if (parent.Type != QuestionTypes.YesNo || dep.Value is not (YesNo.Yes or YesNo.No))
                    throw new InvalidOperationException($"Questionnaire: question '{question.Id}' must depend on a yes_no question with value Yes or No.");
            }

            seen[question.Id] = question;
        }
    }
}
