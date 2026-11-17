using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;
using CaseAgents.Tests.Messaging;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Storage;

/// <summary>
/// The ICaseStore contract as test scenarios. Run against InMemoryCaseStore (always) and CosmosCaseStore (emulator only).
/// </summary>
public static class StoreScenarios
{
    public static async Task Create_sets_draft_audit_seq_1_and_etag(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();

        var created = await store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated));

        Assert.Equal(CaseStatus.Draft, created.Status);
        Assert.Equal(1, created.AuditSeq);
        Assert.False(string.IsNullOrEmpty(created.ETag));
        Assert.Equal(caseId, created.Id);

        var read = await store.GetCaseAsync(caseId);
        Assert.Equal(created.ETag, read?.ETag);
        Assert.Equal(1, read?.AuditSeq);

        var audit = await store.GetAuditAsync(caseId);
        var entry = Assert.Single(audit);
        Assert.Equal(ItemIds.Audit(caseId, 1), entry.Id);
        Assert.Equal(1, entry.Seq);
        Assert.Equal(AuditActions.CaseCreated, entry.Action);
    }

    public static async Task Create_with_existing_id_throws_conflict(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        await store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated));

        await Assert.ThrowsAsync<CaseConflictException>(() =>
            store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated)));

        Assert.Single(await store.GetAuditAsync(caseId));
    }

    public static async Task Every_write_changes_the_case_etag_and_appends_one_audit_entry(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var etags = new List<string?>();

        var v1 = await store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated));
        etags.Add(v1.ETag);

        var v2 = await store.AddDocumentAsync(v1, BankLetter(caseId), Audit(AuditActions.DocumentAdded));
        etags.Add(v2.ETag);
        await AssertSeq(store, v2, 2);

        var v3 = await store.UpdateCaseAsync(v2, v2 with { Status = CaseStatus.Ready }, Audit(AuditActions.CaseReady));
        etags.Add(v3.ETag);
        await AssertSeq(store, v3, 3);

        var v4 = (await store.WriteProposalAsync(v3, NewProposal(caseId, "Q4", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4"), "k1")).Case;
        etags.Add(v4.ETag);
        await AssertSeq(store, v4, 4);

        var v5 = await store.WriteDecisionAsync(v4, NewAnswer(caseId, "Q4", YesNo.No), null, Audit(AuditActions.AnswerConfirmed, "Q4"));
        etags.Add(v5.ETag);
        await AssertSeq(store, v5, 5);

        Assert.Equal(5, etags.Distinct().Count());
        Assert.All(etags, e => Assert.False(string.IsNullOrEmpty(e)));
    }

    public static async Task Write_with_stale_etag_throws_conflict_and_writes_nothing(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var stale = await store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated));
        var latest = await store.UpdateCaseAsync(stale, stale with { Status = CaseStatus.Ready }, Audit(AuditActions.CaseReady));

        await Assert.ThrowsAsync<CaseConflictException>(() =>
            store.AddDocumentAsync(stale, BankLetter(caseId), Audit(AuditActions.DocumentAdded)));
        await Assert.ThrowsAsync<CaseConflictException>(() =>
            store.UpdateCaseAsync(stale, stale with { Status = CaseStatus.Closed }, Audit(AuditActions.CaseReady)));
        await Assert.ThrowsAsync<CaseConflictException>(() =>
            store.WriteProposalAsync(stale, NewProposal(caseId, "Q4", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4"), "k1"));
        await Assert.ThrowsAsync<CaseConflictException>(() =>
            store.WriteDecisionAsync(stale, NewAnswer(caseId, "Q4", YesNo.No), null, Audit(AuditActions.AnswerConfirmed, "Q4")));

        var record = (await store.GetCaseRecordAsync(caseId))!;
        Assert.Equal(latest.ETag, record.Case.ETag);
        Assert.Equal(CaseStatus.Ready, record.Case.Status);
        Assert.Equal(2, record.Case.AuditSeq);
        Assert.Empty(record.Documents);
        Assert.Empty(record.Proposals);
        Assert.Empty(record.Answers);
        Assert.Equal(2, (await store.GetAuditAsync(caseId)).Count);
        Assert.False(await store.IsMessageProcessedAsync(caseId, "k1"));
    }

    public static async Task Update_copies_only_status_and_lock_fields(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var created = await store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated));

        var updated = await store.UpdateCaseAsync(
            created,
            created with { Status = CaseStatus.Ready, AiLocked = true, LockedBy = Scenario.Analyst, Title = "changed", SubmittedBy = "mallory" },
            Audit(AuditActions.CaseTakenOver));

        var read = (await store.GetCaseAsync(caseId))!;
        Assert.Equal(CaseStatus.Ready, read.Status);
        Assert.True(read.AiLocked);
        Assert.Equal(Scenario.Analyst, read.LockedBy);
        Assert.Equal(created.Title, read.Title);
        Assert.Equal("alice", read.SubmittedBy);
        Assert.Equal(created.CreatedAt, read.CreatedAt);
        Assert.Equal(updated.ETag, read.ETag);
    }

    public static async Task Proposal_upsert_replaces_previous_proposal_of_the_question(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var v1 = await CreateReady(store, caseId);

        var first = await store.WriteProposalAsync(v1, NewProposal(caseId, "Q4", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4"), "k1");
        var replacement = NewProposal(caseId, "Q4", null) with
        {
            Status = ProposalStatus.NeedsAnalyst, Confidence = null, ConfidenceBand = null, NeedsAnalystReason = "No quote supporting the answer",
        };
        var second = await store.WriteProposalAsync(first.Case, replacement, Audit(AuditActions.ProposalNeedsAnalyst, "Q4"), "k2");

        Assert.False(second.Duplicate);
        var record = (await store.GetCaseRecordAsync(caseId))!;
        var proposal = Assert.Single(record.Proposals);
        Assert.Equal(ItemIds.Proposal(caseId, "Q4"), proposal.Id);
        Assert.Equal(ProposalStatus.NeedsAnalyst, proposal.Status);
        Assert.Null(proposal.Value);
        Assert.Equal("No quote supporting the answer", proposal.NeedsAnalystReason);
    }

    public static async Task Duplicate_idempotency_key_writes_nothing_and_reports_duplicate(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var v1 = await CreateReady(store, caseId);

        var first = await store.WriteProposalAsync(v1, NewProposal(caseId, "Q4", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4"), "k1");
        Assert.False(first.Duplicate);
        Assert.True(await store.IsMessageProcessedAsync(caseId, "k1"));
        Assert.False(await store.IsMessageProcessedAsync(caseId, "k2"));

        var again = await store.WriteProposalAsync(first.Case, NewProposal(caseId, "Q4", YesNo.No), Audit(AuditActions.ProposalUpdated, "Q4"), "k1");

        Assert.True(again.Duplicate);
        Assert.Equal(first.Case.ETag, again.Case.ETag);
        var record = (await store.GetCaseRecordAsync(caseId))!;
        Assert.Equal(first.Case.ETag, record.Case.ETag);
        Assert.Equal(YesNo.Yes, Assert.Single(record.Proposals).Value);
        Assert.Equal(first.Case.AuditSeq, (await store.GetAuditAsync(caseId)).Count);
    }

    public static async Task Duplicate_key_wins_over_stale_etag(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var stale = await CreateReady(store, caseId);
        var first = await store.WriteProposalAsync(stale, NewProposal(caseId, "Q4", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4"), "k1");

        // "stale" carries the ETag from before the first write; the repeated key must still be reported as a duplicate.
        var again = await store.WriteProposalAsync(stale, NewProposal(caseId, "Q4", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4"), "k1");

        Assert.True(again.Duplicate);
        Assert.Equal(first.Case.ETag, again.Case.ETag);
        Assert.Equal(first.Case.AuditSeq, (await store.GetAuditAsync(caseId)).Count);
    }

    public static async Task Decision_writes_answer_and_proposal_atomically(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var v1 = await CreateReady(store, caseId);
        var proposed = await store.WriteProposalAsync(v1, NewProposal(caseId, "Q4", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4"), "k1");
        var auditBefore = (await store.GetAuditAsync(caseId)).Count;

        var rejected = NewProposal(caseId, "Q4", YesNo.Yes) with { Status = ProposalStatus.Rejected };
        var decided = await store.WriteDecisionAsync(
            proposed.Case, NewAnswer(caseId, "Q4", YesNo.No), rejected, Audit(AuditActions.AnswerConfirmed, "Q4", Scenario.AnalystReason));

        var record = (await store.GetCaseRecordAsync(caseId))!;
        var answer = Assert.Single(record.Answers);
        Assert.Equal(ItemIds.Answer(caseId, "Q4"), answer.Id);
        Assert.Equal(YesNo.No, answer.Value);
        Assert.Equal(Scenario.Analyst, answer.SetBy);
        Assert.Equal(ProposalStatus.Rejected, Assert.Single(record.Proposals).Status);
        Assert.Equal(decided.ETag, record.Case.ETag);

        var audit = await store.GetAuditAsync(caseId);
        Assert.Equal(auditBefore + 1, audit.Count);
        Assert.Equal(AuditActions.AnswerConfirmed, audit[^1].Action);
        Assert.Equal(Scenario.AnalystReason, audit[^1].Reason);

        // Neither item is written when the unit fails.
        await Assert.ThrowsAsync<CaseConflictException>(() => store.WriteDecisionAsync(
            proposed.Case, NewAnswer(caseId, "Q4.1", YesNo.Yes), NewProposal(caseId, "Q4.1", YesNo.Yes) with { Status = ProposalStatus.Accepted },
            Audit(AuditActions.AnswerConfirmed, "Q4.1")));
        record = (await store.GetCaseRecordAsync(caseId))!;
        Assert.Single(record.Answers);
        Assert.Single(record.Proposals);
    }

    public static async Task Audit_entries_have_sequential_ids_and_store_clock_time(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var t0 = clock.GetUtcNow();

        var v1 = await store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated));
        clock.Advance(TimeSpan.FromMinutes(1));
        var v2 = await store.AddDocumentAsync(v1, BankLetter(caseId), Audit(AuditActions.DocumentAdded));
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.UpdateCaseAsync(v2, v2 with { Status = CaseStatus.Ready }, Audit(AuditActions.CaseReady, reason: "ready"));

        var audit = await store.GetAuditAsync(caseId);

        Assert.Equal(new long[] { 1, 2, 3 }, audit.Select(a => a.Seq));
        Assert.Equal(new[] { ItemIds.Audit(caseId, 1), ItemIds.Audit(caseId, 2), ItemIds.Audit(caseId, 3) }, audit.Select(a => a.Id));
        Assert.Equal(new[] { t0, t0.AddMinutes(1), t0.AddMinutes(2) }, audit.Select(a => a.At));
        Assert.Equal(new[] { AuditActions.CaseCreated, AuditActions.DocumentAdded, AuditActions.CaseReady }, audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.Equal(caseId, a.CaseId));
        Assert.Equal(new AuditActor(ActorTypes.Person, "alice"), audit[2].Actor);
        Assert.Equal("ready", audit[2].Reason);
    }

    public static async Task Add_document_with_existing_document_id_throws_conflict(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var v1 = await store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated));
        var v2 = await store.AddDocumentAsync(v1, BankLetter(caseId), Audit(AuditActions.DocumentAdded));

        await Assert.ThrowsAsync<CaseConflictException>(() =>
            store.AddDocumentAsync(v2, BankLetter(caseId) with { Name = "Another letter" }, Audit(AuditActions.DocumentAdded)));

        var record = (await store.GetCaseRecordAsync(caseId))!;
        Assert.Equal(v2.ETag, record.Case.ETag);
        Assert.Equal(Scenario.BankLetterId, Assert.Single(record.Documents).DocumentId);
        Assert.Equal(2, (await store.GetAuditAsync(caseId)).Count);
    }

    public static async Task List_cases_filters_by_submitter_newest_first(ICaseStore store, ManualTimeProvider clock)
    {
        var start = ManualTimeProvider.DefaultStart;
        var older = await store.CreateCaseAsync(NewDraft(ItemIds.NewCaseId()) with { CreatedAt = start.AddMinutes(-3) }, Audit(AuditActions.CaseCreated));
        var bobs = await store.CreateCaseAsync(NewDraft(ItemIds.NewCaseId(), "bob") with { CreatedAt = start.AddMinutes(-2) }, Audit(AuditActions.CaseCreated));
        var newer = await store.CreateCaseAsync(NewDraft(ItemIds.NewCaseId()) with { CreatedAt = start.AddMinutes(-1) }, Audit(AuditActions.CaseCreated));

        var alices = await store.ListCasesAsync("alice");
        var all = await store.ListCasesAsync();

        Assert.Equal(new[] { newer.CaseId, older.CaseId }, alices.Select(c => c.CaseId));
        Assert.Equal(new[] { newer.CaseId, bobs.CaseId, older.CaseId }, all.Select(c => c.CaseId));
        Assert.Empty(await store.ListCasesAsync("nobody"));
    }

    public static async Task Readers_order_documents_by_added_at_answers_by_id_and_records_oldest_first(ICaseStore store, ManualTimeProvider clock)
    {
        var start = ManualTimeProvider.DefaultStart;
        var caseId = ItemIds.NewCaseId();
        var other = await store.CreateCaseAsync(NewDraft(ItemIds.NewCaseId()) with { CreatedAt = start.AddMinutes(-1) }, Audit(AuditActions.CaseCreated));
        var v = await store.CreateCaseAsync(NewDraft(caseId) with { CreatedAt = start.AddMinutes(-5) }, Audit(AuditActions.CaseCreated));

        var docs = Scenario.Documents(caseId);
        // Added in file order, but stamped with decreasing AddedAt: readers must follow AddedAt, not insertion order.
        for (var i = 0; i < docs.Count; i++)
            v = await store.AddDocumentAsync(v, docs[i] with { AddedAt = start.AddSeconds(docs.Count - i) }, Audit(AuditActions.DocumentAdded));
        v = await store.WriteDecisionAsync(v, NewAnswer(caseId, "Q4.1", YesNo.No), null, Audit(AuditActions.AnswerConfirmed, "Q4.1"));
        await store.WriteDecisionAsync(v, NewAnswer(caseId, "Q4", YesNo.No), null, Audit(AuditActions.AnswerConfirmed, "Q4"));

        var record = (await store.GetCaseRecordAsync(caseId))!;
        Assert.Equal(docs.Reverse().Select(d => d.DocumentId), record.Documents.Select(d => d.DocumentId));
        Assert.Equal(new[] { "Q4", "Q4.1" }, record.Answers.Select(a => a.QuestionId));
        Assert.Null(await store.GetCaseRecordAsync("missing"));
        Assert.Empty(await store.GetAuditAsync("missing"));

        var records = await store.ListCaseRecordsAsync();
        Assert.Equal(new[] { caseId, other.CaseId }, records.Select(r => r.Case.CaseId));
    }

    public static async Task Write_with_tampered_copy_keeps_the_stored_lock_and_title(ICaseStore store, ManualTimeProvider clock)
    {
        var caseId = ItemIds.NewCaseId();
        var created = await store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated));
        var locked = await store.UpdateCaseAsync(
            created, created with { Status = CaseStatus.Ready, AiLocked = true, LockedBy = Scenario.Analyst }, Audit(AuditActions.CaseTakenOver));

        // Same ETag, other fields changed: only the ETag of the caller's copy may count.
        static CaseItem Tamper(CaseItem c) => c with
        {
            AiLocked = false, LockedBy = null, Title = "hijacked", SubmittedBy = "mallory", Status = CaseStatus.Closed, AuditSeq = 99,
        };

        var v = (await store.WriteProposalAsync(Tamper(locked), NewProposal(caseId, "Q4", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4"), "k1")).Case;
        AssertUntouched(v, 3);
        v = await store.AddDocumentAsync(Tamper(v), BankLetter(caseId), Audit(AuditActions.DocumentAdded));
        AssertUntouched(v, 4);
        v = await store.WriteDecisionAsync(Tamper(v), NewAnswer(caseId, "Q4", YesNo.No), null, Audit(AuditActions.AnswerConfirmed, "Q4"));
        AssertUntouched(v, 5);

        // UpdateCaseAsync copies only Status/AiLocked/LockedBy from "updated"; the rest comes from the stored case.
        var tamperedCurrent = Tamper(v);
        v = await store.UpdateCaseAsync(tamperedCurrent, tamperedCurrent with { AiLocked = true, LockedBy = Scenario.Analyst, Status = CaseStatus.Ready }, Audit(AuditActions.CaseTakenOver));
        AssertUntouched(v, 6);

        var read = (await store.GetCaseAsync(caseId))!;
        AssertUntouched(read, 6);
        Assert.Equal(6, (await store.GetAuditAsync(caseId)).Count);

        void AssertUntouched(CaseItem c, long seq)
        {
            Assert.True(c.AiLocked);
            Assert.Equal(Scenario.Analyst, c.LockedBy);
            Assert.Equal(created.Title, c.Title);
            Assert.Equal("alice", c.SubmittedBy);
            Assert.Equal(CaseStatus.Ready, c.Status);
            Assert.Equal(created.CreatedAt, c.CreatedAt);
            Assert.Equal(seq, c.AuditSeq);
        }
    }

    public static async Task Ids_with_characters_cosmos_rejects_throw_argument_exception(ICaseStore store, ManualTimeProvider clock)
    {
        foreach (var bad in new[] { "a/b", "a\\b", "a?b", "a#b" })
            await Assert.ThrowsAsync<ArgumentException>(() => store.CreateCaseAsync(NewDraft(bad), Audit(AuditActions.CaseCreated)));

        var caseId = ItemIds.NewCaseId();
        var v = await CreateReady(store, caseId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AddDocumentAsync(v, BankLetter(caseId) with { DocumentId = "bank/letter" }, Audit(AuditActions.DocumentAdded)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteProposalAsync(v, NewProposal(caseId, "Q4", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4"), "a/b"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteProposalAsync(v, NewProposal(caseId, "Q4#1", YesNo.Yes), Audit(AuditActions.ProposalCreated, "Q4#1"), "k1"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteDecisionAsync(v, NewAnswer(caseId, "Q4?", YesNo.No), null, Audit(AuditActions.AnswerConfirmed, "Q4?")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteDecisionAsync(v, null, NewProposal(caseId, "Q4\\x", YesNo.No), Audit(AuditActions.ProposalWithdrawn, "Q4\\x")));

        var record = (await store.GetCaseRecordAsync(caseId))!;
        Assert.Equal(v.ETag, record.Case.ETag);
        Assert.Empty(record.Documents);
        Assert.Empty(record.Proposals);
        Assert.Empty(record.Answers);
        Assert.Equal(2, (await store.GetAuditAsync(caseId)).Count);

        // Readers treat such ids as "not found".
        Assert.Null(await store.GetCaseAsync("a/b"));
        Assert.Null(await store.GetCaseRecordAsync("a/b"));
        Assert.Empty(await store.GetAuditAsync("a/b"));
        Assert.False(await store.IsMessageProcessedAsync(caseId, "a/b"));
    }

    // ---- helpers ----

    public static CaseItem NewDraft(string caseId, string submittedBy = "alice") =>
        Scenario.NewCase(caseId, submittedBy, CaseStatus.Draft);

    public static AuditDraft Audit(string action, string? questionId = null, string? reason = null) => new()
    {
        Actor = new AuditActor(ActorTypes.Person, "alice"),
        Action = action,
        QuestionId = questionId,
        Reason = reason,
    };

    public static CaseDocument BankLetter(string caseId) =>
        Scenario.Documents(caseId).Single(d => d.DocumentId == Scenario.BankLetterId);

    public static Proposal NewProposal(string caseId, string questionId, string? value) => new()
    {
        Id = ItemIds.Proposal(caseId, questionId),
        CaseId = caseId,
        QuestionId = questionId,
        Value = value,
        Confidence = 0.93,
        ConfidenceBand = ConfidenceBand.High,
        Agent = "Answerer",
        PromptVersion = "v12",
        ModelVersion = "fake",
        VerifierPassed = true,
        Status = ProposalStatus.Proposed,
    };

    public static FinalAnswer NewAnswer(string caseId, string questionId, string value) => new()
    {
        Id = ItemIds.Answer(caseId, questionId),
        CaseId = caseId,
        QuestionId = questionId,
        Value = value,
        SetBy = Scenario.Analyst,
        Reason = Scenario.AnalystReason,
    };

    private static async Task<CaseItem> CreateReady(ICaseStore store, string caseId)
    {
        var created = await store.CreateCaseAsync(NewDraft(caseId), Audit(AuditActions.CaseCreated));
        return await store.UpdateCaseAsync(created, created with { Status = CaseStatus.Ready }, Audit(AuditActions.CaseReady));
    }

    private static async Task AssertSeq(ICaseStore store, CaseItem returned, long expected)
    {
        Assert.Equal(expected, returned.AuditSeq);
        var read = (await store.GetCaseAsync(returned.CaseId))!;
        Assert.Equal(expected, read.AuditSeq);
        Assert.Equal(returned.ETag, read.ETag);
        Assert.Equal(expected, (await store.GetAuditAsync(returned.CaseId)).Count);
    }
}
