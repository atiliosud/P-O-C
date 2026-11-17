using CaseAgents.Agents;
using CaseAgents.Agents.Models;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Core.Operations;
using CaseAgents.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaseAgents.Tests.Agents;

/// <summary>The AI backend wired to the in-memory fake case app, with the Fake models and the scenario case.</summary>
internal sealed class AgentsKit
{
    public const string CaseId = "case-1";

    public static readonly string[] AllQuestions = ["Q1", "Q2", "Q3", "Q4", "Q4.1", "Q4.2", "Q4.3", "Q5", "Q6"];
    public static readonly string[] Q4Dependents = ["Q4.1", "Q4.2", "Q4.3"];

    public AgentsKit(bool shadowMode = false, IAnswerer? answerer = null, int maxConflictRetries = 5)
    {
        Questionnaire = Scenario.Questionnaire();
        App = new FakeCaseApp(Questionnaire);
        App.Add(Scenario.Record(CaseId));
        Options = new AiBackendOptions { ShadowMode = shadowMode, MaxConflictRetries = maxConflictRetries };
        Orchestrator = new CaseOrchestrator(
            App, answerer ?? new FakeAnswerer(), new FakeVerifier(), Questionnaire, Outbox, Options, NullLogger<CaseOrchestrator>.Instance);
        Status = new AiBackendStatus(Outbox, Orchestrator);
        Flusher = new OutboxFlusher(Outbox, App, Orchestrator, Options, Faults, NullLogger<OutboxFlusher>.Instance);
    }

    public Questionnaire Questionnaire { get; }
    public FakeCaseApp App { get; }
    public Outbox Outbox { get; } = new();
    public FaultState Faults { get; } = new();
    public AiBackendOptions Options { get; }
    public CaseOrchestrator Orchestrator { get; }
    public AiBackendStatus Status { get; }
    public OutboxFlusher Flusher { get; }

    public static CaseEvent Event(string type, long sequence, string? questionId = null, string? value = null) =>
        new($"{CaseId}:{sequence}", CaseId, sequence, type, new DateTimeOffset(2026, 11, 17, 10, 40, 0, TimeSpan.Zero), questionId, value);

    public Task ReadyAsync() => Orchestrator.HandleAsync(Event(CaseEventTypes.CaseReady, 1), CancellationToken.None);

    public Proposal Proposal(string questionId) =>
        App.Proposal(CaseId, questionId) ?? throw new Xunit.Sdk.XunitException($"No proposal for {questionId}");

    public Proposal? ProposalOrNull(string questionId) => App.Proposal(CaseId, questionId);
}
