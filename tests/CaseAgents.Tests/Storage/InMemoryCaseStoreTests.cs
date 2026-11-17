using System.Reflection;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;
using CaseAgents.Infra;
using CaseAgents.Infra.Storage;
using CaseAgents.Infra.Storage.Cosmos;
using CaseAgents.Tests.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaseAgents.Tests.Storage;

public sealed class InMemoryCaseStoreTests
{
    private readonly ManualTimeProvider clock = new();
    private readonly InMemoryCaseStore store;

    public InMemoryCaseStoreTests() => store = new InMemoryCaseStore(clock);

    [Fact]
    public Task Create_sets_draft_audit_seq_1_and_etag() => StoreScenarios.Create_sets_draft_audit_seq_1_and_etag(store, clock);

    [Fact]
    public Task Create_with_existing_id_throws_conflict() => StoreScenarios.Create_with_existing_id_throws_conflict(store, clock);

    [Fact]
    public Task Every_write_changes_the_case_etag_and_appends_one_audit_entry() =>
        StoreScenarios.Every_write_changes_the_case_etag_and_appends_one_audit_entry(store, clock);

    [Fact]
    public Task Write_with_stale_etag_throws_conflict_and_writes_nothing() =>
        StoreScenarios.Write_with_stale_etag_throws_conflict_and_writes_nothing(store, clock);

    [Fact]
    public Task Update_copies_only_status_and_lock_fields() => StoreScenarios.Update_copies_only_status_and_lock_fields(store, clock);

    [Fact]
    public Task Proposal_upsert_replaces_previous_proposal_of_the_question() =>
        StoreScenarios.Proposal_upsert_replaces_previous_proposal_of_the_question(store, clock);

    [Fact]
    public Task Duplicate_idempotency_key_writes_nothing_and_reports_duplicate() =>
        StoreScenarios.Duplicate_idempotency_key_writes_nothing_and_reports_duplicate(store, clock);

    [Fact]
    public Task Duplicate_key_wins_over_stale_etag() => StoreScenarios.Duplicate_key_wins_over_stale_etag(store, clock);

    [Fact]
    public Task Decision_writes_answer_and_proposal_atomically() => StoreScenarios.Decision_writes_answer_and_proposal_atomically(store, clock);

    [Fact]
    public Task Audit_entries_have_sequential_ids_and_store_clock_time() =>
        StoreScenarios.Audit_entries_have_sequential_ids_and_store_clock_time(store, clock);

    [Fact]
    public Task Add_document_with_existing_document_id_throws_conflict() =>
        StoreScenarios.Add_document_with_existing_document_id_throws_conflict(store, clock);

    [Fact]
    public Task List_cases_filters_by_submitter_newest_first() => StoreScenarios.List_cases_filters_by_submitter_newest_first(store, clock);

    [Fact]
    public Task Readers_order_documents_by_added_at_answers_by_id_and_records_oldest_first() =>
        StoreScenarios.Readers_order_documents_by_added_at_answers_by_id_and_records_oldest_first(store, clock);

    [Fact]
    public Task Write_with_tampered_copy_keeps_the_stored_lock_and_title() =>
        StoreScenarios.Write_with_tampered_copy_keeps_the_stored_lock_and_title(store, clock);

    [Fact]
    public Task Ids_with_characters_cosmos_rejects_throw_argument_exception() =>
        StoreScenarios.Ids_with_characters_cosmos_rejects_throw_argument_exception(store, clock);

    [Fact]
    public async Task Parallel_writes_with_the_same_etag_let_exactly_one_win()
    {
        var caseId = ItemIds.NewCaseId();
        var created = await store.CreateCaseAsync(StoreScenarios.NewDraft(caseId), StoreScenarios.Audit(AuditActions.CaseCreated));
        using var start = new ManualResetEventSlim(false);

        var writes = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            start.Wait();
            try
            {
                await store.WriteProposalAsync(created, StoreScenarios.NewProposal(caseId, "Q4", YesNo.Yes),
                    StoreScenarios.Audit(AuditActions.ProposalCreated, "Q4"), $"key-{i}");
                return "ok";
            }
            catch (CaseConflictException)
            {
                return "conflict";
            }
        })).ToList();
        start.Set();
        var outcomes = await Task.WhenAll(writes);

        Assert.Equal(1, outcomes.Count(o => o == "ok"));
        Assert.Equal(19, outcomes.Count(o => o == "conflict"));
        var audit = await store.GetAuditAsync(caseId);
        Assert.Equal(2, audit.Count);
        Assert.Equal(2, (await store.GetCaseAsync(caseId))!.AuditSeq);
        Assert.Single((await store.GetCaseRecordAsync(caseId))!.Proposals);
    }

    [Fact]
    public void Audit_has_no_update_or_delete_methods()
    {
        var types = new[] { typeof(ICaseReader), typeof(ICaseStore), typeof(InMemoryCaseStore), typeof(CosmosCaseStore) };

        foreach (var type in types)
        {
            var names = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(m => m.Name).ToList();
            Assert.NotEmpty(names);
            Assert.All(names, name =>
            {
                Assert.False(name.Contains("Audit", StringComparison.OrdinalIgnoreCase) && name != nameof(ICaseReader.GetAuditAsync),
                    $"{type.Name}.{name} touches the audit trail");
                Assert.False(name.StartsWith("Delete", StringComparison.OrdinalIgnoreCase), $"{type.Name}.{name} deletes");
                Assert.False(name.StartsWith("Remove", StringComparison.OrdinalIgnoreCase), $"{type.Name}.{name} removes");
            });
        }
    }

    [Fact]
    public void Storage_registration_defaults_to_in_memory_with_one_singleton_for_store_and_reader()
    {
        var config = new ConfigurationBuilder().Build();
        using var provider = new ServiceCollection().AddCaseAgentsStorage(config).BuildServiceProvider();

        var caseStore = provider.GetRequiredService<ICaseStore>();
        Assert.IsType<InMemoryCaseStore>(caseStore);
        Assert.Same(caseStore, provider.GetRequiredService<ICaseReader>());
    }

    [Fact]
    public void Storage_registration_rejects_unknown_mode()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Mode"] = "Sql" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCaseAgentsStorage(config));
    }
}
