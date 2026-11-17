namespace CaseAgents.Core.Models;

/// <summary>One question of a case as the reviewer app shows it.</summary>
/// <param name="Proposal">Null when there is none, or when it is a shadow proposal (never shown to analysts).</param>
public sealed record QuestionState(Question Question, Applicability Applicability, FinalAnswer? Answer, Proposal? Proposal);

public sealed record Gap(GapKind Kind, string Message, string? DocumentKind = null, string? QuestionId = null);

/// <summary>GET /api/cases/{caseId}: everything the case screen needs.</summary>
/// <param name="AiPending">True while the AI backend has unprocessed work for this case (or is down).</param>
public sealed record CaseView(
    CaseItem Case,
    IReadOnlyList<CaseDocument> Documents,
    IReadOnlyList<QuestionState> Questions,
    IReadOnlyList<Gap> Gaps,
    bool AiPending,
    string PromptVersion);

/// <summary>GET /api/cases: one row per case.</summary>
public sealed record CaseSummary(CaseItem Case, int Questions, int Answered, int OpenProposals, int NeedsAnalyst);

/// <summary>One row of the audit view (one per question that has a proposal or a final answer), questionnaire order.</summary>
public sealed record AuditViewRow
{
    public required string QuestionId { get; init; }
    public required string QuestionText { get; init; }

    // Proposed: latest agent proposal before (or without) the decision.
    public string? ProposedValue { get; init; }
    public string? ProposedBy { get; init; }
    public string? PromptVersion { get; init; }
    public ProposalStatus? ProposalStatus { get; init; }

    // Basis
    public AuditBasis? Basis { get; init; }

    // Confidence
    public double? Confidence { get; init; }
    public ConfidenceBand? Band { get; init; }
    public bool? VerifierPassed { get; init; }

    // Final
    public string? FinalValue { get; init; }
    public string? FinalBy { get; init; }
    public DateTimeOffset? FinalAt { get; init; }

    public string? Reason { get; init; }

    /// <summary>E.g. "Q4.1 to Q4.3 proposals withdrawn".</summary>
    public string? Effect { get; init; }
}

/// <summary>GET /api/cases/{caseId}/audit.</summary>
public sealed record AuditView(IReadOnlyList<AuditViewRow> Rows, IReadOnlyList<AuditEntry> Entries);
