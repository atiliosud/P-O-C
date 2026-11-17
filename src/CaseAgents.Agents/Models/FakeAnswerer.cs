using System.Text.RegularExpressions;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;

namespace CaseAgents.Agents.Models;

/// <summary>
/// Deterministic answerer for demos and tests: the first line that contains an evidence keyword is the quote.
/// It never calls a model, so it makes the same plausible mistakes every run (for example Q4 = "Yes" from a line about a closed account).
/// </summary>
public sealed class FakeAnswerer : IAnswerer
{
    public const string Version = "fake-answerer-1";

    private static readonly Regex Negation = new(@"\b(not|no|never|none)\b", RegexOptions.IgnoreCase);

    public Task<AnswerDraft> AnswerAsync(Question question, IReadOnlyList<Passage> passages, CancellationToken ct = default)
    {
        foreach (var passage in passages)
        {
            foreach (var rawLine in passage.Text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || !question.EvidenceKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (question.Type == QuestionTypes.YesNo)
                {
                    var value = Negation.IsMatch(line) ? YesNo.No : YesNo.Yes;
                    return Task.FromResult(new AnswerDraft(true, value, line, passage.DocumentId, passage.Page, 0.93, Version));
                }

                var colon = line.IndexOf(':');
                var text = colon >= 0 ? line[(colon + 1)..].Trim() : line;
                return Task.FromResult(new AnswerDraft(true, text, line, passage.DocumentId, passage.Page, null, Version));
            }
        }

        return Task.FromResult(new AnswerDraft(true, null, null, null, null, null, Version, "No passage mentions the question keywords"));
    }
}
