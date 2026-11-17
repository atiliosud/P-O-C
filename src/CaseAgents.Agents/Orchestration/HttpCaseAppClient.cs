using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CaseAgents.Core.Models;

namespace CaseAgents.Agents;

/// <summary>
/// Calls the Case API over HTTP with a Case.Agent token for the case.
/// 200 -> Ok; 409 -> Conflict (+ code); 404 -> NotFound; 503 (or other 5xx), network error or timeout -> Unavailable;
/// other 4xx -> Rejected.
/// </summary>
public sealed class HttpCaseAppClient(CaseAppHttpClientProvider http, IAgentTokenProvider tokens) : ICaseAppClient
{
    public async Task<CaseAppResult<CaseView>> GetCaseAsync(string caseId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/cases/{Uri.EscapeDataString(caseId)}");
        var (result, etagHeader) = await SendAsync<CaseView>(request, caseId, ct);

        // The case version travels in the body (case._etag) and in the ETag header; the body wins.
        if (result.Value is { } view && string.IsNullOrEmpty(view.Case.ETag) && !string.IsNullOrEmpty(etagHeader))
            result = result with { Value = view with { Case = view.Case with { ETag = etagHeader } } };
        return result;
    }

    public async Task<CaseAppResult<ProposalWriteResponse>> PutProposalAsync(
        string caseId, string questionId, ProposalWrite write, string ifMatch, string idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"api/cases/{Uri.EscapeDataString(caseId)}/proposals/{Uri.EscapeDataString(questionId)}")
        {
            Content = JsonContent.Create(write, options: CaseAgentsJson.Options),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        var (result, _) = await SendAsync<ProposalWriteResponse>(request, caseId, ct);
        return result;
    }

    private async Task<(CaseAppResult<T> Result, string? ETag)> SendAsync<T>(HttpRequestMessage request, string caseId, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetToken(caseId));
        try
        {
            using var response = await http.Client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(CaseAgentsJson.Options, ct)
                            ?? throw new InvalidOperationException($"Empty response from {request.RequestUri}.");
                var etag = response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;
                return (new CaseAppResult<T>(CaseAppOutcome.Ok, value, null, null), etag);
            }

            var (error, code) = await ReadErrorAsync(response, ct);
            var outcome = response.StatusCode switch
            {
                HttpStatusCode.Conflict => CaseAppOutcome.Conflict,
                HttpStatusCode.NotFound => CaseAppOutcome.NotFound,
                >= HttpStatusCode.InternalServerError => CaseAppOutcome.Unavailable,
                _ => CaseAppOutcome.Rejected,
            };
            return (new CaseAppResult<T>(outcome, default, error ?? $"HTTP {(int)response.StatusCode}", code), null);
        }
        catch (HttpRequestException ex)
        {
            return (new CaseAppResult<T>(CaseAppOutcome.Unavailable, default, ex.Message, null), null);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout (not the caller cancelling).
            return (new CaseAppResult<T>(CaseAppOutcome.Unavailable, default, ex.Message, null), null);
        }
    }

    /// <summary>Reads the { error, code } body of a failed call; tolerates empty or non-JSON bodies.</summary>
    private static async Task<(string? Error, string? Code)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (text, null);
            var error = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : text;
            var code = doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return (error, code);
        }
        catch (JsonException)
        {
            return (text, null);
        }
    }
}
