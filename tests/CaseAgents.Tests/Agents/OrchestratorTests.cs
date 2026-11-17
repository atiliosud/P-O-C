using CaseAgents.Agents;
using CaseAgents.Agents.Models;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Agents;

public class OrchestratorTests
{
    private const string CaseId = AgentsKit.CaseId;

    [Fact]
    public async Task Run_proposes_questions_in_order_and_Q4_yes_with_evidence()
    {
        var kit = new AgentsKit();

        await kit.ReadyAsync();

        Assert.Equal(AgentsKit.AllQuestions, kit.App.Puts.Select(p => p.QuestionId));
        Assert.All(kit.App.Puts, p => Assert.Equal($"{CaseId}:1:{p.QuestionId}", p.IdempotencyKey));
        Assert.All(kit.App.Puts, p => Assert.Equal(CaseAppOutcome.Ok, p.Outcome));

        var q4 = kit.Proposal("Q4");
        Assert.Equal(ProposalStatus.Proposed, q4.Status);
        Assert.Equal(YesNo.Yes, q4.Value);
        Assert.Equal(ConfidenceBand.High, q4.ConfidenceBand);
        Assert.True(q4.VerifierPassed);
        Assert.Equal(Scenario.BankLetterPage3Line, q4.Quote);
        Assert.Equal(Scenario.BankLetterId, q4.DocumentId);
        Assert.Equal(3, q4.Page);
        Assert.NotNull(q4.Evidence);
        Assert.Equal("v12", q4.PromptVersion);
        Assert.Equal(AgentNames.Answerer, q4.Agent);
        Assert.Equal(FakeAnswerer.Version, q4.ModelVersion);
        Assert.False(q4.DependsOnUnconfirmed);
        Assert.False(q4.Shadow);
    }

    [Fact]
    public async Task Run_marks_Q4_dependents_depends_on_unconfirmed()
    {
        var kit = new AgentsKit();

        await kit.ReadyAsync();

        foreach (var id in AgentsKit.Q4Dependents)
        {
            var p = kit.Proposal(id);
            Assert.Equal(ProposalStatus.Proposed, p.Status);
            Assert.True(p.DependsOnUnconfirmed, id);
        }

        Assert.Equal("Switzerland", kit.Proposal("Q4.1").Value);
        Assert.False(kit.Proposal("Q1").DependsOnUnconfirmed);
    }

    [Fact]
    public async Task Run_writes_needs_analyst_without_value_for_Q5()
    {
        var kit = new AgentsKit();

        await kit.ReadyAsync();

        var q5 = kit.Proposal("Q5");
        Assert.Equal(ProposalStatus.NeedsAnalyst, q5.Status);
        Assert.Null(q5.Value);
        Assert.Null(q5.ConfidenceBand);
        Assert.False(string.IsNullOrWhiteSpace(q5.NeedsAnalystReason));
    }

    [Fact]
    public async Task Run_stops_when_case_is_taken_over()
    {
        var kit = new AgentsKit();
        // The analyst takes over while the agent's Q3 write is in flight.
        kit.App.BeforeWrite = (caseId, qid) =>
        {
            if (qid == "Q3" && !kit.App.Record(caseId).Case.AiLocked)
                kit.App.TakeOver(caseId);
        };

        await kit.ReadyAsync();

        Assert.Equal(["Q1", "Q2", "Q3"], kit.App.Puts.Select(p => p.QuestionId));
        Assert.Equal("version_changed", kit.App.Puts[^1].Code);
        Assert.Null(kit.ProposalOrNull("Q3"));
        Assert.Equal(2, kit.App.AppliedWrites);
    }

    [Fact]
    public async Task Run_does_nothing_on_a_locked_or_not_ready_case()
    {
        var kit = new AgentsKit();
        kit.App.TakeOver(CaseId);

        await kit.ReadyAsync();
        Assert.Empty(kit.App.Puts);

        kit.App.Add(Scenario.Record("draft-case") with { Case = Scenario.NewCase("draft-case", status: CaseStatus.Draft) with { ETag = "\"d1\"" } });
        await kit.Orchestrator.RunAsync("draft-case", "m", CancellationToken.None);
        Assert.Empty(kit.App.Puts);
    }

    [Fact]
    public async Task Run_rereads_after_409_and_never_forces()
    {
        var kit = new AgentsKit();
        // A. Silva confirms Q4 = No while the agent's Q4 write is in flight.
        kit.App.BeforeWrite = (caseId, qid) =>
        {
            if (qid == "Q4" && kit.App.Record(caseId).Answers.Count == 0)
                kit.App.Confirm(caseId, "Q4", YesNo.No);
        };

        await kit.ReadyAsync();

        var q4Puts = kit.App.Puts.Where(p => p.QuestionId == "Q4").ToList();
        Assert.Single(q4Puts);
        Assert.Equal(CaseAppOutcome.Conflict, q4Puts[0].Outcome);
        Assert.Equal("version_changed", q4Puts[0].Code);

        // The final answer stands, no proposal was written for Q4, and Q4.1-Q4.3 no longer apply.
        Assert.Equal(YesNo.No, kit.App.Record(CaseId).Answers.Single(a => a.QuestionId == "Q4").Value);
        Assert.Null(kit.ProposalOrNull("Q4"));
        Assert.All(AgentsKit.Q4Dependents, id => Assert.Null(kit.ProposalOrNull(id)));
        Assert.NotNull(kit.ProposalOrNull("Q5"));
        Assert.NotNull(kit.ProposalOrNull("Q6"));
        Assert.All(kit.App.Puts, p => Assert.False(string.IsNullOrEmpty(p.IfMatch)));
    }

    [Fact]
    public async Task Run_gives_up_after_max_consecutive_conflicts()
    {
        var kit = new AgentsKit(maxConflictRetries: 3);
        // Someone else changes the case before every write.
        kit.App.BeforeWrite = (caseId, _) => kit.App.Release(caseId);

        await kit.ReadyAsync();

        Assert.Equal(3, kit.App.Puts.Count);
        Assert.All(kit.App.Puts, p => Assert.Equal("version_changed", p.Code));
        Assert.Equal(0, kit.App.AppliedWrites);
    }

    [Fact]
    public async Task Answer_confirmed_no_withdraws_dependents_with_reason_and_keeps_values()
    {
        var kit = new AgentsKit();
        await kit.ReadyAsync();
        var before = AgentsKit.Q4Dependents.ToDictionary(id => id, kit.Proposal);
        kit.App.Puts.Clear();

        kit.App.Confirm(CaseId, "Q4", YesNo.No);
        await kit.Orchestrator.HandleAsync(AgentsKit.Event(CaseEventTypes.AnswerConfirmed, 2, "Q4", YesNo.No), CancellationToken.None);

        foreach (var id in AgentsKit.Q4Dependents)
        {
            var p = kit.Proposal(id);
            Assert.Equal(ProposalStatus.Withdrawn, p.Status);
            Assert.Equal($"Q4 confirmed as No; {id} applies only if Q4 = Yes", p.WithdrawnReason);
            Assert.Equal(before[id].Value, p.Value);
            Assert.Equal(before[id].Quote, p.Quote);
            Assert.Equal(before[id].Evidence, p.Evidence);
        }

        Assert.Equal(AgentsKit.Q4Dependents, kit.App.Puts.Select(p => p.QuestionId));
        Assert.All(kit.App.Puts, p => Assert.Equal("Q4", p.Write.CausedBy));
        Assert.All(kit.App.Puts, p => Assert.Equal($"{CaseId}:2:withdraw:{p.QuestionId}", p.IdempotencyKey));
    }

    [Fact]
    public async Task Answer_confirmed_yes_turns_dependents_into_normal_proposals()
    {
        var kit = new AgentsKit();
        await kit.ReadyAsync();
        kit.App.Puts.Clear();

        kit.App.Confirm(CaseId, "Q4", YesNo.Yes);
        await kit.Orchestrator.HandleAsync(AgentsKit.Event(CaseEventTypes.AnswerConfirmed, 2, "Q4", YesNo.Yes), CancellationToken.None);

        foreach (var id in AgentsKit.Q4Dependents)
        {
            var p = kit.Proposal(id);
            Assert.Equal(ProposalStatus.Proposed, p.Status);
            Assert.False(p.DependsOnUnconfirmed);
            Assert.NotNull(p.Value);
        }

        Assert.All(kit.App.Puts, p => Assert.Equal("Q4", p.Write.CausedBy));
        Assert.All(kit.App.Puts, p => Assert.Equal($"{CaseId}:2:confirm:{p.QuestionId}", p.IdempotencyKey));
        Assert.Equal(3, kit.App.Puts.Count);
    }

    [Fact]
    public async Task Release_after_a_decision_during_take_over_rechecks_dependents()
    {
        var kit = new AgentsKit();
        await kit.ReadyAsync();
        kit.App.TakeOver(CaseId);
        kit.App.Confirm(CaseId, "Q4", YesNo.No);

        await kit.Orchestrator.HandleAsync(AgentsKit.Event(CaseEventTypes.AnswerConfirmed, 3, "Q4", YesNo.No), CancellationToken.None);
        Assert.Equal(ProposalStatus.Proposed, kit.Proposal("Q4.1").Status);

        kit.App.Release(CaseId);
        await kit.Orchestrator.HandleAsync(AgentsKit.Event(CaseEventTypes.CaseReleased, 4), CancellationToken.None);

        Assert.All(AgentsKit.Q4Dependents, id => Assert.Equal(ProposalStatus.Withdrawn, kit.Proposal(id).Status));
    }

    [Fact]
    public async Task Redelivered_event_does_not_duplicate_writes()
    {
        var kit = new AgentsKit();
        var ready = AgentsKit.Event(CaseEventTypes.CaseReady, 1);
        // Both deliveries read the same case version before either writes, so both pick the same question.
        kit.App.HoldFirstWrites(2);

        await Task.WhenAll(
            Task.Run(() => kit.Orchestrator.HandleAsync(ready, CancellationToken.None)),
            Task.Run(() => kit.Orchestrator.HandleAsync(ready, CancellationToken.None)));
        await kit.Orchestrator.HandleAsync(ready, CancellationToken.None);

        Assert.True(kit.App.DuplicateKeys >= 1);
        Assert.Equal(AgentsKit.AllQuestions.Length, kit.App.AppliedWrites);
        Assert.Equal(AgentsKit.AllQuestions.Length, kit.App.Record(CaseId).Proposals.Count);
        Assert.Equal(AgentsKit.AllQuestions, kit.App.Record(CaseId).Proposals.Select(p => p.QuestionId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Case_app_down_puts_proposals_in_outbox_and_flush_sends_them_once()
    {
        var kit = new AgentsKit();
        kit.App.WritesDown = true;

        await kit.ReadyAsync();

        Assert.Equal(1, kit.Outbox.Count);
        var entry = kit.Outbox.Snapshot().Single();
        Assert.Equal("Q1", entry.QuestionId);
        Assert.NotNull(entry.Write);
        Assert.Equal($"{CaseId}:1:Q1", entry.IdempotencyKey);
        Assert.Equal(0, kit.App.AppliedWrites);

        kit.App.WritesDown = false;
        await kit.Flusher.FlushOnceAsync(CancellationToken.None);
        await kit.Flusher.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(0, kit.Outbox.Count);
        Assert.Equal(AgentsKit.AllQuestions.Length, kit.App.AppliedWrites);
        foreach (var id in AgentsKit.AllQuestions)
            Assert.Single(kit.App.Puts, p => p.QuestionId == id && p.Outcome == CaseAppOutcome.Ok);
    }

    [Fact]
    public async Task Write_applied_but_response_lost_is_not_applied_twice()
    {
        var kit = new AgentsKit();
        kit.App.LoseNextWriteResponse = true;

        await kit.ReadyAsync();
        Assert.Equal(1, kit.Outbox.Count);
        Assert.Equal(1, kit.App.AppliedWrites);

        await kit.Flusher.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(0, kit.Outbox.Count);
        Assert.Equal(1, kit.App.DuplicateKeys);
        Assert.Equal(AgentsKit.AllQuestions.Length, kit.App.AppliedWrites);
    }

    [Fact]
    public async Task Case_app_down_on_read_queues_the_event_and_flush_replays_it()
    {
        var kit = new AgentsKit();
        kit.App.Down = true;

        await kit.ReadyAsync();
        var entry = Assert.Single(kit.Outbox.Snapshot());
        Assert.NotNull(entry.Event);
        Assert.Null(entry.Write);

        await kit.Flusher.FlushOnceAsync(CancellationToken.None);
        Assert.Equal(1, kit.Outbox.Count);

        kit.App.Down = false;
        await kit.Flusher.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(0, kit.Outbox.Count);
        Assert.Equal(AgentsKit.AllQuestions.Length, kit.App.AppliedWrites);
    }

    [Fact]
    public async Task Any_later_event_catches_up_a_lost_answer_confirmed()
    {
        var kit = new AgentsKit();
        await kit.ReadyAsync();
        // The answer.confirmed for Q4 = No never arrives (dropped as late); the next event is about another question.
        kit.App.Confirm(CaseId, "Q4", YesNo.No);
        kit.App.Confirm(CaseId, "Q1", "Maria Example");

        await kit.Orchestrator.HandleAsync(AgentsKit.Event(CaseEventTypes.AnswerConfirmed, 3, "Q1", "Maria Example"), CancellationToken.None);

        Assert.All(AgentsKit.Q4Dependents, id => Assert.Equal(ProposalStatus.Withdrawn, kit.Proposal(id).Status));
    }

    [Fact]
    public async Task Duplicate_key_stops_the_run_instead_of_looping_on_one_question()
    {
        var kit = new AgentsKit();
        await kit.ReadyAsync();
        kit.App.Confirm(CaseId, "Q4", YesNo.No);
        await kit.Orchestrator.HandleAsync(AgentsKit.Event(CaseEventTypes.AnswerConfirmed, 2, "Q4", YesNo.No), CancellationToken.None);
        // Q4 is changed back to Yes, so the withdrawn Q4.1 is open again. The old case.ready is replayed (outbox):
        // its key for Q4.1 was already used, so the API answers Duplicate.
        kit.App.Confirm(CaseId, "Q4", YesNo.Yes);
        kit.App.Puts.Clear();

        await kit.ReadyAsync();

        var put = Assert.Single(kit.App.Puts);
        Assert.Equal("Q4.1", put.QuestionId);
        Assert.Equal($"{CaseId}:1:Q4.1", put.IdempotencyKey);
        Assert.Equal(ProposalStatus.Withdrawn, kit.Proposal("Q4.1").Status);

        // The real trigger (a new answer.confirmed) re-proposes the dependents.
        await kit.Orchestrator.HandleAsync(AgentsKit.Event(CaseEventTypes.AnswerConfirmed, 3, "Q4", YesNo.Yes), CancellationToken.None);
        Assert.All(AgentsKit.Q4Dependents, id => Assert.Equal(ProposalStatus.Proposed, kit.Proposal(id).Status));
    }

    [Fact]
    public async Task Outbox_replay_that_throws_stays_in_the_outbox_and_is_retried()
    {
        var answerer = new SwitchableAnswerer();
        var kit = new AgentsKit(answerer: answerer);
        kit.App.Down = true;
        await kit.ReadyAsync();
        Assert.Equal(1, kit.Outbox.Count);

        kit.App.Down = false;
        answerer.Throw = true;
        await kit.Flusher.FlushOnceAsync(CancellationToken.None);

        var entry = Assert.Single(kit.Outbox.Snapshot());
        Assert.Equal(1, entry.Attempts);
        Assert.NotNull(entry.Event);

        answerer.Throw = false;
        await kit.Flusher.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(0, kit.Outbox.Count);
        Assert.Equal(AgentsKit.AllQuestions.Length, kit.App.AppliedWrites);
    }

    [Fact]
    public async Task Outbox_entry_is_dropped_after_max_failed_replays()
    {
        var answerer = new SwitchableAnswerer();
        var kit = new AgentsKit(answerer: answerer);
        kit.App.Down = true;
        await kit.ReadyAsync();
        kit.App.Down = false;
        answerer.Throw = true;

        for (var i = 1; i < OutboxFlusher.MaxReplayAttempts; i++)
        {
            await kit.Flusher.FlushOnceAsync(CancellationToken.None);
            Assert.Equal(i, Assert.Single(kit.Outbox.Snapshot()).Attempts);
        }

        await kit.Flusher.FlushOnceAsync(CancellationToken.None);
        Assert.Equal(0, kit.Outbox.Count);
    }

    [Fact]
    public async Task Outbox_is_not_flushed_while_the_ai_backend_is_down()
    {
        var kit = new AgentsKit();
        kit.App.WritesDown = true;
        await kit.ReadyAsync();
        kit.App.WritesDown = false;
        kit.Faults.AiBackendDown = true;

        await kit.Flusher.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(1, kit.Outbox.Count);
        Assert.Equal(0, kit.App.AppliedWrites);

        kit.Faults.AiBackendDown = false;
        await kit.Flusher.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(0, kit.Outbox.Count);
        Assert.Equal(AgentsKit.AllQuestions.Length, kit.App.AppliedWrites);
    }

    [Fact]
    public async Task Shadow_mode_marks_proposals_shadow()
    {
        var kit = new AgentsKit(shadowMode: true);

        await kit.ReadyAsync();

        Assert.Equal(AgentsKit.AllQuestions.Length, kit.App.Record(CaseId).Proposals.Count);
        Assert.All(kit.App.Record(CaseId).Proposals, p => Assert.True(p.Shadow));
    }

    [Fact]
    public async Task Status_is_pending_while_outbox_has_entries()
    {
        var kit = new AgentsKit();
        Assert.False(kit.Status.IsPending(CaseId));
        kit.App.WritesDown = true;

        await kit.ReadyAsync();

        Assert.True(kit.Status.IsPending(CaseId));
        Assert.False(kit.Status.IsPending("other-case"));
        Assert.Equal(1, kit.Status.OutboxCount);

        kit.App.WritesDown = false;
        await kit.Flusher.FlushOnceAsync(CancellationToken.None);

        Assert.False(kit.Status.IsPending(CaseId));
        Assert.Equal(0, kit.Status.OutboxCount);
    }

    [Fact]
    public async Task Status_is_pending_while_a_run_is_in_progress()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kit = new AgentsKit(answerer: new GatedAnswerer(gate.Task));

        var run = kit.ReadyAsync();
        Assert.True(kit.Status.IsPending(CaseId));

        gate.SetResult();
        await run;
        Assert.False(kit.Status.IsPending(CaseId));
    }

    [Fact]
    public async Task Model_exception_propagates_so_the_bus_retries()
    {
        var kit = new AgentsKit(answerer: new ThrowingAnswerer());

        await Assert.ThrowsAsync<InvalidOperationException>(kit.ReadyAsync);

        Assert.Empty(kit.App.Puts);
        Assert.Equal(0, kit.Outbox.Count);
        Assert.False(kit.Status.IsPending(CaseId));
    }

    [Fact]
    public async Task Other_events_do_nothing()
    {
        var kit = new AgentsKit();

        await kit.Orchestrator.HandleAsync(AgentsKit.Event(CaseEventTypes.CaseTakenOver, 1), CancellationToken.None);
        await kit.Orchestrator.HandleAsync(AgentsKit.Event(CaseEventTypes.ProposalRejected, 2, "Q1"), CancellationToken.None);

        Assert.Equal(0, kit.App.Reads);
        Assert.Empty(kit.App.Puts);
    }

    [Fact]
    public void Outbox_counts_per_case_and_removes_by_id()
    {
        var outbox = new Outbox();
        outbox.Add(new OutboxEntry("e1", "a", "m1", null, null, null, null, AgentsKit.Event(CaseEventTypes.CaseReady, 1)));
        outbox.Add(new OutboxEntry("e2", "b", "m2", null, null, null, null, AgentsKit.Event(CaseEventTypes.CaseReady, 2)));

        Assert.Equal(2, outbox.Count);
        Assert.Equal(1, outbox.CountFor("a"));
        Assert.True(outbox.Remove("e1"));
        Assert.False(outbox.Remove("e1"));
        Assert.Equal(0, outbox.CountFor("a"));
        Assert.Equal(["e2"], outbox.Snapshot().Select(e => e.Id));
    }

    private sealed class ThrowingAnswerer : IAnswerer
    {
        public Task<AnswerDraft> AnswerAsync(Question question, IReadOnlyList<Passage> passages, CancellationToken ct = default) =>
            throw new InvalidOperationException("model unavailable");
    }

    private sealed class SwitchableAnswerer : IAnswerer
    {
        private readonly FakeAnswerer inner = new();

        public bool Throw { get; set; }

        public Task<AnswerDraft> AnswerAsync(Question question, IReadOnlyList<Passage> passages, CancellationToken ct = default) =>
            Throw ? throw new InvalidOperationException("model timeout") : inner.AnswerAsync(question, passages, ct);
    }

    private sealed class GatedAnswerer(Task gate) : IAnswerer
    {
        private readonly FakeAnswerer inner = new();

        public async Task<AnswerDraft> AnswerAsync(Question question, IReadOnlyList<Passage> passages, CancellationToken ct = default)
        {
            await gate;
            return await inner.AnswerAsync(question, passages, ct);
        }
    }
}
