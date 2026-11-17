using System.Text.Json;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;

namespace CaseAgents.Agents.Models;

/// <summary>The fixed output schema of the Answerer, and the check of what the model returned.</summary>
public static class AnswerSchema
{
    public const string InvalidReason = "Model output did not match the answer schema";

    private static readonly string[] Properties = ["value", "quote", "documentId", "page"];

    public static object JsonSchema { get; } = new
    {
        type = "object",
        properties = new
        {
            value = new { type = new[] { "string", "null" } },
            quote = new { type = new[] { "string", "null" } },
            documentId = new { type = new[] { "string", "null" } },
            page = new { type = new[] { "integer", "null" } },
        },
        required = Properties,
        additionalProperties = false,
    };

    public static AnswerDraft Parse(string content, Question q, IReadOnlyList<Passage> passages, double? tokenConfidence, string modelVersion) =>
        TryParse(content, q, passages, tokenConfidence, modelVersion)
        ?? new AnswerDraft(false, null, null, null, null, null, modelVersion, InvalidReason);

    private static AnswerDraft? TryParse(string content, Question q, IReadOnlyList<Passage> passages, double? tokenConfidence, string modelVersion)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var names = root.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count != Properties.Length || Properties.Any(p => !names.Contains(p)))
                return null;

            if (!TryString(root.GetProperty("value"), out var value)
                || !TryString(root.GetProperty("quote"), out var quote)
                || !TryString(root.GetProperty("documentId"), out var documentId))
                return null;

            int? page = null;
            var pageElement = root.GetProperty("page");
            if (pageElement.ValueKind == JsonValueKind.Number)
            {
                if (!pageElement.TryGetInt32(out var n) || n < 1)
                    return null;
                page = n;
            }
            else if (pageElement.ValueKind != JsonValueKind.Null)
            {
                return null;
            }

            if (q.Type == QuestionTypes.YesNo && value is not (null or YesNo.Yes or YesNo.No))
                return null;

            if ((documentId is not null || page is not null)
                && !passages.Any(p => p.DocumentId == documentId && p.Page == page))
                return null;

            var confidence = q.Type == QuestionTypes.YesNo ? tokenConfidence : null;
            return new AnswerDraft(true, value, quote, documentId, page, confidence, modelVersion);
        }
    }

    private static bool TryString(JsonElement element, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return true;
    }
}
