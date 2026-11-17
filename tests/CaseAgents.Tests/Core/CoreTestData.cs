using CaseAgents.Core.Models;

namespace CaseAgents.Tests.Core;

internal static class CoreTestData
{
    public const string CaseId = "c1";

    public static FinalAnswer Final(string questionId, string value, string by = "A. Silva") => new()
    {
        Id = ItemIds.Answer(CaseId, questionId),
        CaseId = CaseId,
        QuestionId = questionId,
        Value = value,
        SetBy = by,
        SetAt = new DateTimeOffset(2026, 11, 17, 10, 42, 0, TimeSpan.Zero),
    };

    public static Proposal Prop(
        string questionId,
        ProposalStatus status = ProposalStatus.Proposed,
        string? value = "Yes",
        bool dependsOnUnconfirmed = false,
        bool shadow = false) => new()
    {
        Id = ItemIds.Proposal(CaseId, questionId),
        CaseId = CaseId,
        QuestionId = questionId,
        Value = value,
        Agent = "Answerer",
        PromptVersion = "v12",
        ModelVersion = "fake-answerer-1",
        Status = status,
        DependsOnUnconfirmed = dependsOnUnconfirmed,
        Shadow = shadow,
    };

    public static CaseRecord Record(
        IReadOnlyList<FinalAnswer>? answers = null,
        IReadOnlyList<Proposal>? proposals = null,
        IReadOnlyList<CaseDocument>? documents = null) =>
        new(
            CaseAgents.Tests.TestSupport.Scenario.NewCase(CaseId),
            documents ?? CaseAgents.Tests.TestSupport.Scenario.Documents(CaseId),
            answers ?? [],
            proposals ?? []);
}
