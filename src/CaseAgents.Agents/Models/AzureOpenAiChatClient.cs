using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using CaseAgents.Infra;

namespace CaseAgents.Agents.Models;

public sealed record ChatResult(string Content, IReadOnlyList<(string Token, double Logprob)> Logprobs, string Model);

/// <summary>
/// Azure OpenAI chat completions through the REST API. No SDK and no API key: bearer token from Entra.
/// Always asks for logprobs and temperature 0.
/// </summary>
public sealed class AzureOpenAiChatClient(HttpClient http, TokenCredential credential, AzureOpenAiOptions options)
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    public AzureOpenAiOptions Options => options;

    public async Task<ChatResult> CompleteAsync(string deployment, string system, string user, object? jsonSchema, int maxTokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured.");
        if (string.IsNullOrWhiteSpace(deployment))
            throw new InvalidOperationException("AzureOpenAI:Deployment is not configured.");

        var url = $"{options.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={Uri.EscapeDataString(options.ApiVersion)}";
        var messages = new[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user },
        };

        var body = jsonSchema is null
            ? JsonSerializer.Serialize(new { messages, temperature = 0, max_tokens = maxTokens, logprobs = true })
            : JsonSerializer.Serialize(new
            {
                messages,
                temperature = 0,
                max_tokens = maxTokens,
                logprobs = true,
                response_format = new { type = "json_schema", json_schema = new { name = "answer", strict = true, schema = jsonSchema } },
            });

        var accessToken = await credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azure OpenAI deployment '{deployment}' returned HTTP {(int)response.StatusCode}: {Truncate(text)}");

        return Parse(deployment, text);
    }

    private static ChatResult Parse(string deployment, string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Azure OpenAI deployment '{deployment}' returned malformed JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0
                || choices[0].ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Azure OpenAI deployment '{deployment}' returned no choices.");

            var choice = choices[0];
            var content = choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()!
                : "";

            if (!choice.TryGetProperty("logprobs", out var logprobs) || logprobs.ValueKind != JsonValueKind.Object
                || !logprobs.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                throw new InvalidOperationException(
                    $"Deployment '{deployment}' does not return logprobs. Logprobs are required to compute the confidence; use a chat model deployment that supports them.");

            var list = new List<(string, double)>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("token", out var t) || t.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("logprob", out var l) || l.ValueKind != JsonValueKind.Number)
                    throw new InvalidOperationException($"Azure OpenAI deployment '{deployment}' returned logprobs without token and logprob values.");
                list.Add((t.GetString()!, l.GetDouble()));
            }

            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return new ChatResult(content, list, string.IsNullOrWhiteSpace(model) ? deployment : model);
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
