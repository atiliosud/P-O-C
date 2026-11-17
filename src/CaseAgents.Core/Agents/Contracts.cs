using CaseAgents.Core.Models;

namespace CaseAgents.Core.Agents;

/// <summary>Logical agent roles, recorded on proposals and audit entries.</summary>
public static class AgentNames
{
    public const string Extractor = "Extractor";
    public const string Orchestrator = "Orchestrator";
    public const string Answerer = "Answerer";
    public const string Verifier = "Verifier";
    public const string GapFinder = "Gap finder";
}

/// <summary>One page of one case document, selected by the Extractor's per-case index for a question.</summary>
public sealed record Passage(string DocumentId, string DocumentName, int Page, string Text);

/// <summary>Output of the Answerer for one question, already checked against the fixed schema.</summary>
/// <param name="SchemaValid">False when the model output did not fit the schema; then every other field is null.</param>
/// <param name="TokenConfidence">yes_no only: p = exp(logprob) of the answer token. Null for free_text.</param>
/// <param name="Error">Why there is no usable answer (invalid output, no passage), for the NeedsAnalyst reason.</param>
public sealed record AnswerDraft(
    bool SchemaValid,
    string? Value,
    string? Quote,
    string? DocumentId,
    int? Page,
    double? TokenConfidence,
    string ModelVersion,
    string? Error = null);

/// <param name="Supports">The second model judged that the quote supports the answer.</param>
/// <param name="Score">Confidence of that judgement in [0, 1]; used as the confidence of free_text answers.</param>
public sealed record VerifierResult(bool Supports, double Score, string ModelVersion);

/// <summary>Answers ONE question from the given passages. Implementations: Fake (default) and AzureOpenAI.</summary>
public interface IAnswerer
{
    Task<AnswerDraft> AnswerAsync(Question question, IReadOnlyList<Passage> passages, CancellationToken ct = default);
}

/// <summary>Second-model check: does the quote support the answer? Implementations: Fake (default) and AzureOpenAI.</summary>
public interface IVerifier
{
    Task<VerifierResult> VerifyAsync(Question question, string value, string quote, Passage citedPage, CancellationToken ct = default);
}

/// <summary>What the orchestrator will write for one question (see ProposalPolicy).</summary>
public sealed record ProposalDecision(
    ProposalStatus Status,
    string? Value,
    double? Confidence,
    ConfidenceBand? Band,
    bool VerifierPassed,
    string? Quote,
    string? DocumentId,
    int? Page,
    EvidenceRef? Evidence,
    string? NeedsAnalystReason);
