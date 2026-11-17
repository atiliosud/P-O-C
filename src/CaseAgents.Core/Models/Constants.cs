namespace CaseAgents.Core.Models;

/// <summary>Values of the <c>type</c> discriminator stored on every item of the "cases" container.</summary>
public static class ItemTypes
{
    public const string Case = "case";
    public const string Document = "document";
    public const string Answer = "answer";
    public const string Proposal = "proposal";
    public const string Audit = "audit";

    /// <summary>Marker of a processed Idempotency-Key (message id), so a repeated write is not applied twice.</summary>
    public const string Message = "message";
}

/// <summary>Question types used in questionnaire.json.</summary>
public static class QuestionTypes
{
    public const string YesNo = "yes_no";
    public const string FreeText = "free_text";
}

/// <summary>The two values a yes_no question accepts.</summary>
public static class YesNo
{
    public const string Yes = "Yes";
    public const string No = "No";
}

/// <summary>Who performed an audited action.</summary>
public static class ActorTypes
{
    public const string Person = "person";
    public const string Agent = "agent";
    public const string System = "system";
}

/// <summary>Values of AuditEntry.Action.</summary>
public static class AuditActions
{
    public const string CaseCreated = "case.created";
    public const string DocumentAdded = "document.added";
    public const string CaseReady = "case.ready";
    public const string ProposalCreated = "proposal.created";
    public const string ProposalNeedsAnalyst = "proposal.needs_analyst";
    public const string ProposalUpdated = "proposal.updated";
    public const string ProposalWithdrawn = "proposal.withdrawn";
    public const string ProposalRejected = "proposal.rejected";
    public const string AnswerConfirmed = "answer.confirmed";
    public const string CaseTakenOver = "case.takeover";
    public const string CaseReleased = "case.released";
}

/// <summary>Values of AnswerRequest.Action (analyst decisions).</summary>
public static class AnswerActions
{
    /// <summary>Confirm the proposed value unchanged (proposal -> Accepted).</summary>
    public const string Accept = "accept";

    /// <summary>Set a value (different from the proposal, or where there is none); reason required (proposal -> Rejected).</summary>
    public const string Edit = "edit";

    /// <summary>Reject the proposal without setting a value; reason required (proposal -> Rejected).</summary>
    public const string Reject = "reject";
}

/// <summary>
/// Deterministic item ids. All items of a case share the partition key (caseId).
/// </summary>
public static class ItemIds
{
    public static string NewCaseId() => Guid.NewGuid().ToString("N");
    public static string Document(string caseId, string documentId) => $"{caseId}:doc:{documentId}";
    public static string Answer(string caseId, string questionId) => $"{caseId}:answer:{questionId}";
    public static string Proposal(string caseId, string questionId) => $"{caseId}:proposal:{questionId}";
    public static string Audit(string caseId, long seq) => $"{caseId}:audit:{seq:D6}";
    public static string Message(string caseId, string idempotencyKey) => $"{caseId}:msg:{idempotencyKey}";
}
