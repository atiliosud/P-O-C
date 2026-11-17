namespace CaseAgents.Core.Models;

/// <summary>Lifecycle of a case. Serialized as the enum name (e.g. "Ready").</summary>
public enum CaseStatus
{
    /// <summary>Created by a submitter; documents can be attached.</summary>
    Draft,

    /// <summary>Marked ready: the AI backend may propose answers.</summary>
    Ready,

    /// <summary>Closed; no further writes.</summary>
    Closed,
}

/// <summary>State of a proposal written by the AI backend. Only people move it to Accepted or Rejected.</summary>
public enum ProposalStatus
{
    /// <summary>A value with evidence that passed both checks, waiting for an analyst.</summary>
    Proposed,

    /// <summary>No usable answer (no quote, a check failed, or low confidence). Never carries a value.</summary>
    NeedsAnalyst,

    /// <summary>No longer applies (e.g. the parent question was confirmed with another value). Kept, never deleted.</summary>
    Withdrawn,

    /// <summary>An analyst accepted the proposed value unchanged.</summary>
    Accepted,

    /// <summary>An analyst rejected the proposal or set a different value.</summary>
    Rejected,
}

/// <summary>Confidence shown to the analyst. High at or above Questionnaire.HighConfidence, Medium at or above the proposal threshold, otherwise Low.</summary>
public enum ConfidenceBand
{
    High,
    Medium,
    Low,
}

/// <summary>Whether a question applies, given its dependency and the parent's FINAL answer.</summary>
public enum Applicability
{
    /// <summary>No dependency, or the parent's final answer matches dependsOn.value.</summary>
    Applicable,

    /// <summary>The parent's final answer differs from dependsOn.value.</summary>
    NotApplicable,

    /// <summary>The parent has no final answer yet ("depends on Q4, not confirmed yet").</summary>
    ParentUnconfirmed,
}

public enum GapKind
{
    /// <summary>A required document kind is not attached to the case.</summary>
    MissingDocument,

    /// <summary>A required, applicable question has no final answer and no proposal with evidence.</summary>
    NoEvidence,
}
