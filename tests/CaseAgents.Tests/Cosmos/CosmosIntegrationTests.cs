using CaseAgents.Core.Models;
using CaseAgents.Infra;
using CaseAgents.Infra.Storage.Cosmos;
using CaseAgents.Tests.Messaging;
using CaseAgents.Tests.Storage;
using Microsoft.Azure.Cosmos;

namespace CaseAgents.Tests.Cosmos;

/// <summary>Runs only when CASEAGENTS_COSMOS_TESTS=1 and the emulator is up (docker compose up -d).</summary>
public sealed class CosmosFactAttribute : FactAttribute
{
    public CosmosFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CASEAGENTS_COSMOS_TESTS") != "1")
            Skip = "Set CASEAGENTS_COSMOS_TESTS=1 and start the Cosmos DB emulator (docker compose up -d) to run.";
    }
}

/// <summary>One throwaway database per test class instance, deleted afterwards.</summary>
public sealed class CosmosTestDatabase : IAsyncLifetime
{
    // Well-known key of the local Cosmos DB emulator, published in Microsoft's documentation. Not a secret.
    private const string EmulatorKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public CosmosOptions Options { get; } = new()
    {
        ConnectionString = ConnectionString(),
        DatabaseName = $"caseagents-tests-{Guid.NewGuid():N}",
        EmulatorMode = true,
    };

    public ManualTimeProvider Clock { get; } = new();
    public CosmosClient Client { get; private set; } = null!;
    public CosmosCaseStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("CASEAGENTS_COSMOS_TESTS") != "1")
            return;
        Client = CosmosClientFactory.Create(Options);
        await CosmosBootstrapper.EnsureCreatedAsync(Client, Options);
        Store = new CosmosCaseStore(Client, Options, Clock);
    }

    public async Task DisposeAsync()
    {
        if (Client is null)
            return;
        await Client.GetDatabase(Options.DatabaseName).DeleteAsync();
        Client.Dispose();
    }

    /// <summary>
    /// CASEAGENTS_COSMOS_CONNECTION_STRING wins; otherwise the emulator on localhost, port COSMOS_EMULATOR_PORT (default 8081).
    /// </summary>
    private static string ConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("CASEAGENTS_COSMOS_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var port = Environment.GetEnvironmentVariable("COSMOS_EMULATOR_PORT");
        if (string.IsNullOrWhiteSpace(port))
            port = "8081";
        return $"AccountEndpoint=https://localhost:{port}/;AccountKey={EmulatorKey}";
    }
}

[Trait("Category", "CosmosIntegration")]
public sealed class CosmosIntegrationTests : IAsyncLifetime
{
    private readonly CosmosTestDatabase db = new();

    public Task InitializeAsync() => db.InitializeAsync();

    public Task DisposeAsync() => db.DisposeAsync();

    [CosmosFact]
    public Task Create_sets_draft_audit_seq_1_and_etag() => StoreScenarios.Create_sets_draft_audit_seq_1_and_etag(db.Store, db.Clock);

    [CosmosFact]
    public Task Create_with_existing_id_throws_conflict() => StoreScenarios.Create_with_existing_id_throws_conflict(db.Store, db.Clock);

    [CosmosFact]
    public Task Every_write_changes_the_case_etag_and_appends_one_audit_entry() =>
        StoreScenarios.Every_write_changes_the_case_etag_and_appends_one_audit_entry(db.Store, db.Clock);

    [CosmosFact]
    public Task Write_with_stale_etag_throws_conflict_and_writes_nothing() =>
        StoreScenarios.Write_with_stale_etag_throws_conflict_and_writes_nothing(db.Store, db.Clock);

    [CosmosFact]
    public Task Update_copies_only_status_and_lock_fields() => StoreScenarios.Update_copies_only_status_and_lock_fields(db.Store, db.Clock);

    [CosmosFact]
    public Task Proposal_upsert_replaces_previous_proposal_of_the_question() =>
        StoreScenarios.Proposal_upsert_replaces_previous_proposal_of_the_question(db.Store, db.Clock);

    [CosmosFact]
    public Task Duplicate_idempotency_key_writes_nothing_and_reports_duplicate() =>
        StoreScenarios.Duplicate_idempotency_key_writes_nothing_and_reports_duplicate(db.Store, db.Clock);

    [CosmosFact]
    public Task Duplicate_key_wins_over_stale_etag() => StoreScenarios.Duplicate_key_wins_over_stale_etag(db.Store, db.Clock);

    [CosmosFact]
    public Task Decision_writes_answer_and_proposal_atomically() =>
        StoreScenarios.Decision_writes_answer_and_proposal_atomically(db.Store, db.Clock);

    [CosmosFact]
    public Task Audit_entries_have_sequential_ids_and_store_clock_time() =>
        StoreScenarios.Audit_entries_have_sequential_ids_and_store_clock_time(db.Store, db.Clock);

    [CosmosFact]
    public Task Add_document_with_existing_document_id_throws_conflict() =>
        StoreScenarios.Add_document_with_existing_document_id_throws_conflict(db.Store, db.Clock);

    [CosmosFact]
    public Task List_cases_filters_by_submitter_newest_first() =>
        StoreScenarios.List_cases_filters_by_submitter_newest_first(db.Store, db.Clock);

    [CosmosFact]
    public Task Readers_order_documents_by_added_at_answers_by_id_and_records_oldest_first() =>
        StoreScenarios.Readers_order_documents_by_added_at_answers_by_id_and_records_oldest_first(db.Store, db.Clock);

    [CosmosFact]
    public Task Write_with_tampered_copy_keeps_the_stored_lock_and_title() =>
        StoreScenarios.Write_with_tampered_copy_keeps_the_stored_lock_and_title(db.Store, db.Clock);

    [CosmosFact]
    public Task Ids_with_characters_cosmos_rejects_throw_argument_exception() =>
        StoreScenarios.Ids_with_characters_cosmos_rejects_throw_argument_exception(db.Store, db.Clock);

    [CosmosFact]
    public async Task Missing_container_fails_loudly_instead_of_reporting_a_conflict()
    {
        var wrong = new CosmosOptions { DatabaseName = db.Options.DatabaseName, ContainerName = "missing-container" };
        var store = new CosmosCaseStore(db.Client, wrong, db.Clock);
        var caseId = ItemIds.NewCaseId();
        var someCase = StoreScenarios.NewDraft(caseId) with { ETag = "\"v1\"" };

        var write = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateCaseAsync(someCase, someCase with { Status = CaseStatus.Ready }, StoreScenarios.Audit(AuditActions.CaseReady)));
        Assert.Contains("missing-container", write.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetCaseAsync(caseId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAuditAsync(caseId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.IsMessageProcessedAsync(caseId, "k1"));
    }

    [CosmosFact]
    public async Task Batch_is_all_or_nothing_when_the_audit_item_already_exists()
    {
        var caseId = ItemIds.NewCaseId();
        var created = await db.Store.CreateCaseAsync(StoreScenarios.NewDraft(caseId), StoreScenarios.Audit(AuditActions.CaseCreated));

        // An audit item written out of band at seq 2: the case ETag is unchanged, so only the audit create can fail.
        var squatter = new AuditEntry
        {
            Id = ItemIds.Audit(caseId, 2),
            CaseId = caseId,
            Seq = 2,
            Actor = new AuditActor(ActorTypes.System, "test"),
            Action = AuditActions.CaseReady,
        };
        await db.Store.Container.CreateItemAsync(squatter, new PartitionKey(caseId));

        await Assert.ThrowsAsync<CaseConflictException>(() =>
            db.Store.UpdateCaseAsync(created, created with { Status = CaseStatus.Ready }, StoreScenarios.Audit(AuditActions.CaseReady)));

        var read = (await db.Store.GetCaseAsync(caseId))!;
        Assert.Equal(CaseStatus.Draft, read.Status);
        Assert.Equal(created.ETag, read.ETag);
        Assert.Equal(1, read.AuditSeq);
    }

    [CosmosFact]
    public async Task Items_are_stored_with_type_discriminator_in_the_case_partition()
    {
        var caseId = ItemIds.NewCaseId();
        var v1 = await db.Store.CreateCaseAsync(StoreScenarios.NewDraft(caseId), StoreScenarios.Audit(AuditActions.CaseCreated));
        var v2 = await db.Store.AddDocumentAsync(v1, StoreScenarios.BankLetter(caseId), StoreScenarios.Audit(AuditActions.DocumentAdded));
        await db.Store.WriteProposalAsync(v2, StoreScenarios.NewProposal(caseId, "Q4", YesNo.Yes),
            StoreScenarios.Audit(AuditActions.ProposalCreated, "Q4"), "k1");

        var query = new QueryDefinition("SELECT VALUE c.type FROM c");
        using var iterator = db.Store.Container.GetItemQueryIterator<string>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(caseId) });
        var types = new List<string>();
        while (iterator.HasMoreResults)
            types.AddRange(await iterator.ReadNextAsync());

        Assert.Equal(
            new[] { ItemTypes.Audit, ItemTypes.Audit, ItemTypes.Audit, ItemTypes.Case, ItemTypes.Document, ItemTypes.Message, ItemTypes.Proposal },
            types.Order());
    }
}
