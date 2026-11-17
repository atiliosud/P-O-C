using System.Net.Http.Headers;
using System.Net.Http.Json;
using CaseAgents.Core.Models;

namespace CaseAgents.Eval;

/// <summary>Loads every case record (shadow proposals included) from the Case API: dev token as "eval" with Case.Review, then GET /api/eval/records.</summary>
public sealed class ApiRecordLoader(HttpClient http)
{
    public async Task<IReadOnlyList<CaseRecord>> LoadAsync(CancellationToken ct = default)
    {
        using var tokenResponse = await http.PostAsJsonAsync(
            "/dev/token", new { userId = "eval", roles = new[] { "Case.Review" } }, CaseAgentsJson.Options, ct);
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(CaseAgentsJson.Options, ct)
                    ?? throw new InvalidOperationException("The API returned an empty token response.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/eval/records");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CaseRecord[]>(CaseAgentsJson.Options, ct) ?? [];
    }

    private sealed record TokenResponse(string AccessToken);
}
