using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;

namespace CaseAgents.Agents.Models;

/// <summary>Deterministic verifier: the quote supports the answer when it contains one of the question's keywords.</summary>
public sealed class FakeVerifier : IVerifier
{
    public const string Version = "fake-verifier-1";

    public Task<VerifierResult> VerifyAsync(Question question, string value, string quote, Passage citedPage, CancellationToken ct = default)
    {
        var supports = !string.IsNullOrWhiteSpace(value)
            && question.EvidenceKeywords.Any(k => quote.Contains(k, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(new VerifierResult(supports, supports ? 0.90 : 0.10, Version));
    }
}
