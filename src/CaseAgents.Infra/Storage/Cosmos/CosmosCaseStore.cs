using System.Net;
using System.Text.Json;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;
using Microsoft.Azure.Cosmos;

namespace CaseAgents.Infra.Storage.Cosmos;

/// <summary>
/// One container ("cases", partition key /caseId) holds every item of a case, discriminated by <c>type</c>.
/// Each write reads the stored CASE item (the caller's copy is used only for its ETag), then runs one TransactionalBatch
/// in the case partition: the CASE item is replaced with If-Match on that ETag (AuditSeq + 1), the write's items follow,
/// and exactly one audit entry is created.
/// </summary>
public sealed class CosmosCaseStore(CosmosClient client, CosmosOptions options, TimeProvider time) : ICaseStore
{
    private static readonly string[] RecordTypes = [ItemTypes.Case, ItemTypes.Document, ItemTypes.Answer, ItemTypes.Proposal];

    public Container Container { get; } = client.GetContainer(options.DatabaseName, options.ContainerName);

    public async Task<CaseItem> CreateCaseAsync(CaseItem item, AuditDraft audit, CancellationToken ct = default)
    {
        ItemIdRules.Validate(item.CaseId, "case id");
        var toStore = item with { Id = item.CaseId, Type = ItemTypes.Case, AuditSeq = 1, ETag = null };

        var batch = Container.CreateTransactionalBatch(new PartitionKey(item.CaseId))
            .CreateItem(toStore)
            .CreateItem(NewEntry(toStore, audit));

        using var response = await ExecuteAsync(batch, item.CaseId, ct);
        return toStore with { ETag = response[0].ETag };
    }

    public async Task<CaseItem> AddDocumentAsync(CaseItem current, CaseDocument document, AuditDraft audit, CancellationToken ct = default)
    {
        ItemIdRules.Validate(current.CaseId, "case id");
        ItemIdRules.Validate(document.DocumentId, "document id");

        var stored = await LoadForUpdateAsync(current, ct);
        var next = Bump(stored);
        var toStore = document with
        {
            Id = ItemIds.Document(current.CaseId, document.DocumentId),
            CaseId = current.CaseId,
            Type = ItemTypes.Document,
        };

        // CreateItem fails with 409 when the document id exists: the whole batch is rolled back.
        var batch = Container.CreateTransactionalBatch(new PartitionKey(current.CaseId))
            .ReplaceItem(current.CaseId, next, IfMatch(stored))
            .CreateItem(toStore)
            .CreateItem(NewEntry(next, audit));

        return await CommitAsync(batch, next, ct);
    }

    public async Task<CaseItem> UpdateCaseAsync(CaseItem current, CaseItem updated, AuditDraft audit, CancellationToken ct = default)
    {
        ItemIdRules.Validate(current.CaseId, "case id");

        var stored = await LoadForUpdateAsync(current, ct);
        var next = Bump(stored) with { Status = updated.Status, AiLocked = updated.AiLocked, LockedBy = updated.LockedBy };

        var batch = Container.CreateTransactionalBatch(new PartitionKey(current.CaseId))
            .ReplaceItem(current.CaseId, next, IfMatch(stored))
            .CreateItem(NewEntry(next, audit));

        return await CommitAsync(batch, next, ct);
    }

    public async Task<WriteResult> WriteProposalAsync(CaseItem current, Proposal proposal, AuditDraft audit, string? idempotencyKey, CancellationToken ct = default)
    {
        ItemIdRules.Validate(current.CaseId, "case id");
        ItemIdRules.Validate(proposal.QuestionId, "question id");
        if (idempotencyKey is not null)
            ItemIdRules.Validate(idempotencyKey, "idempotency key");

        if (idempotencyKey is not null && await IsMessageProcessedAsync(current.CaseId, idempotencyKey, ct))
            return new WriteResult(await GetExistingCaseAsync(current.CaseId, ct), true);

        var stored = await LoadForUpdateAsync(current, ct);
        var next = Bump(stored);
        var toStore = proposal with
        {
            Id = ItemIds.Proposal(current.CaseId, proposal.QuestionId),
            CaseId = current.CaseId,
            Type = ItemTypes.Proposal,
        };

        var batch = Container.CreateTransactionalBatch(new PartitionKey(current.CaseId))
            .ReplaceItem(current.CaseId, next, IfMatch(stored))
            .UpsertItem(toStore);
        if (idempotencyKey is not null)
            batch = batch.CreateItem(new MessageMarker(ItemIds.Message(current.CaseId, idempotencyKey), current.CaseId, idempotencyKey, time.GetUtcNow()));
        batch = batch.CreateItem(NewEntry(next, audit));

        try
        {
            return new WriteResult(await CommitAsync(batch, next, ct), false);
        }
        catch (CaseConflictException) when (idempotencyKey is not null)
        {
            // Lost the race against the same message: it is a duplicate, not a conflict.
            if (await IsMessageProcessedAsync(current.CaseId, idempotencyKey, ct))
                return new WriteResult(await GetExistingCaseAsync(current.CaseId, ct), true);
            throw;
        }
    }

    public async Task<CaseItem> WriteDecisionAsync(CaseItem current, FinalAnswer? answer, Proposal? updatedProposal, AuditDraft audit, CancellationToken ct = default)
    {
        if (answer is null && updatedProposal is null)
            throw new ArgumentException("A decision needs a final answer, an updated proposal, or both.");
        ItemIdRules.Validate(current.CaseId, "case id");
        if (answer is not null)
            ItemIdRules.Validate(answer.QuestionId, "question id");
        if (updatedProposal is not null)
            ItemIdRules.Validate(updatedProposal.QuestionId, "question id");

        var stored = await LoadForUpdateAsync(current, ct);
        var next = Bump(stored);
        var batch = Container.CreateTransactionalBatch(new PartitionKey(current.CaseId))
            .ReplaceItem(current.CaseId, next, IfMatch(stored));

        if (answer is not null)
        {
            batch = batch.UpsertItem(answer with
            {
                Id = ItemIds.Answer(current.CaseId, answer.QuestionId),
                CaseId = current.CaseId,
                Type = ItemTypes.Answer,
            });
        }

        if (updatedProposal is not null)
        {
            batch = batch.UpsertItem(updatedProposal with
            {
                Id = ItemIds.Proposal(current.CaseId, updatedProposal.QuestionId),
                CaseId = current.CaseId,
                Type = ItemTypes.Proposal,
            });
        }

        batch = batch.CreateItem(NewEntry(next, audit));
        return await CommitAsync(batch, next, ct);
    }

    public async Task<CaseItem?> GetCaseAsync(string caseId, CancellationToken ct = default)
    {
        if (!ItemIdRules.IsValid(caseId))
            return null;

        try
        {
            var response = await Container.ReadItemAsync<CaseItem>(caseId, new PartitionKey(caseId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            await ThrowIfContainerMissingAsync(ex, ct);
            return null;
        }
    }

    public async Task<CaseRecord?> GetCaseRecordAsync(string caseId, CancellationToken ct = default)
    {
        if (!ItemIdRules.IsValid(caseId))
            return null;

        var query = new QueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(@types, c.type)")
            .WithParameter("@types", RecordTypes);

        var items = await QueryAsync<JsonElement>(query, new QueryRequestOptions { PartitionKey = new PartitionKey(caseId) }, ct);
        return BuildRecords(items).SingleOrDefault();
    }

    public async Task<IReadOnlyList<CaseItem>> ListCasesAsync(string? submittedBy = null, CancellationToken ct = default)
    {
        var query = submittedBy is null
            ? new QueryDefinition("SELECT * FROM c WHERE c.type = @type")
                .WithParameter("@type", ItemTypes.Case)
            : new QueryDefinition("SELECT * FROM c WHERE c.type = @type AND c.submittedBy = @submittedBy")
                .WithParameter("@type", ItemTypes.Case)
                .WithParameter("@submittedBy", submittedBy);

        var cases = await QueryAsync<CaseItem>(query, null, ct);
        return cases.OrderByDescending(c => c.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<CaseRecord>> ListCaseRecordsAsync(CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(@types, c.type)")
            .WithParameter("@types", RecordTypes);

        var items = await QueryAsync<JsonElement>(query, null, ct);
        return BuildRecords(items).OrderBy(r => r.Case.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string caseId, CancellationToken ct = default)
    {
        if (!ItemIdRules.IsValid(caseId))
            return [];

        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = @type")
            .WithParameter("@type", ItemTypes.Audit);

        var entries = await QueryAsync<AuditEntry>(query, new QueryRequestOptions { PartitionKey = new PartitionKey(caseId) }, ct);
        return entries.OrderBy(e => e.Seq).ToList();
    }

    public async Task<bool> IsMessageProcessedAsync(string caseId, string idempotencyKey, CancellationToken ct = default)
    {
        if (!ItemIdRules.IsValid(caseId) || !ItemIdRules.IsValid(idempotencyKey))
            return false;

        using var response = await Container.ReadItemStreamAsync(
            ItemIds.Message(caseId, idempotencyKey), new PartitionKey(caseId), cancellationToken: ct);
        if (response.IsSuccessStatusCode)
            return true;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await ThrowIfContainerMissingAsync(null, ct);
            return false;
        }

        throw new InvalidOperationException(
            $"Reading the idempotency marker of case '{caseId}' failed with {(int)response.StatusCode} {response.StatusCode}: {response.ErrorMessage}");
    }

    /// <summary>
    /// Reads the stored CASE item and checks the caller's ETag against it. The new case item is built from the stored one,
    /// so fields the caller changed in its copy are never written.
    /// </summary>
    private async Task<CaseItem> LoadForUpdateAsync(CaseItem current, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(current.ETag))
            throw new CaseConflictException($"Case '{current.CaseId}' must be the stored version (with its ETag).");

        var stored = await GetCaseAsync(current.CaseId, ct)
            ?? throw new CaseConflictException($"Case '{current.CaseId}' does not exist.");
        if (!string.Equals(stored.ETag, current.ETag, StringComparison.Ordinal))
            throw new CaseConflictException($"Case '{current.CaseId}' was modified by someone else (ETag mismatch).");
        return stored;
    }

    private async Task<CaseItem> GetExistingCaseAsync(string caseId, CancellationToken ct) =>
        await GetCaseAsync(caseId, ct) ?? throw new CaseConflictException($"Case '{caseId}' does not exist.");

    private async Task<CaseItem> CommitAsync(TransactionalBatch batch, CaseItem next, CancellationToken ct)
    {
        using var response = await ExecuteAsync(batch, next.CaseId, ct);
        return next with { ETag = response[0].ETag };
    }

    /// <summary>The replaced CASE item: the stored one with the next AuditSeq (Cosmos assigns the new ETag).</summary>
    private static CaseItem Bump(CaseItem stored) => stored with
    {
        Id = stored.CaseId,
        Type = ItemTypes.Case,
        AuditSeq = stored.AuditSeq + 1,
        ETag = null,
    };

    private AuditEntry NewEntry(CaseItem caseItem, AuditDraft draft) => new()
    {
        Id = ItemIds.Audit(caseItem.CaseId, caseItem.AuditSeq),
        CaseId = caseItem.CaseId,
        Seq = caseItem.AuditSeq,
        At = time.GetUtcNow(),
        Actor = draft.Actor,
        Action = draft.Action,
        QuestionId = draft.QuestionId,
        Before = draft.Before,
        After = draft.After,
        Basis = draft.Basis,
        Reason = draft.Reason,
        CausedBy = draft.CausedBy,
        Proposal = draft.Proposal,
    };

    /// <summary>Groups raw items by caseId and reads each one according to its <c>type</c>. Groups without a CASE item are dropped.</summary>
    private static List<CaseRecord> BuildRecords(IEnumerable<JsonElement> items)
    {
        var result = new List<CaseRecord>();
        foreach (var group in items.GroupBy(i => i.GetProperty("caseId").GetString() ?? ""))
        {
            CaseItem? caseItem = null;
            var documents = new List<CaseDocument>();
            var answers = new List<FinalAnswer>();
            var proposals = new List<Proposal>();

            foreach (var item in group)
            {
                var type = item.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
                switch (type)
                {
                    case ItemTypes.Case: caseItem = Deserialize<CaseItem>(item); break;
                    case ItemTypes.Document: documents.Add(Deserialize<CaseDocument>(item)); break;
                    case ItemTypes.Answer: answers.Add(Deserialize<FinalAnswer>(item)); break;
                    case ItemTypes.Proposal: proposals.Add(Deserialize<Proposal>(item)); break;
                }
            }

            if (caseItem is not null)
            {
                result.Add(new CaseRecord(
                    caseItem,
                    documents.OrderBy(d => d.AddedAt).ThenBy(d => d.Id, StringComparer.Ordinal).ToList(),
                    answers.OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
                    proposals.OrderBy(p => p.Id, StringComparer.Ordinal).ToList()));
            }
        }

        return result;
    }

    private static T Deserialize<T>(JsonElement item) =>
        item.Deserialize<T>(CaseAgentsJson.Options)
        ?? throw new InvalidOperationException($"Item could not be read as {typeof(T).Name}.");

    /// <summary>Runs a query to the end. A 404 on a query means the database or container is missing.</summary>
    private async Task<List<T>> QueryAsync<T>(QueryDefinition query, QueryRequestOptions? requestOptions, CancellationToken ct)
    {
        var results = new List<T>();
        try
        {
            using var iterator = Container.GetItemQueryIterator<T>(query, requestOptions: requestOptions);
            while (iterator.HasMoreResults)
                results.AddRange(await iterator.ReadNextAsync(ct));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw MissingContainer(ex);
        }

        return results;
    }

    /// <summary>
    /// Runs the batch (all or nothing). A lost race becomes <see cref="CaseConflictException"/>:
    /// 409 an item already exists, 412 the case ETag changed, 404 the case is missing.
    /// A 404 because the database or container is missing becomes <see cref="InvalidOperationException"/>.
    /// </summary>
    private async Task<TransactionalBatchResponse> ExecuteAsync(TransactionalBatch batch, string caseId, CancellationToken ct)
    {
        TransactionalBatchResponse response;
        try
        {
            response = await batch.ExecuteAsync(ct);
        }
        catch (CosmosException ex) when (IsLostRace(ex.StatusCode))
        {
            if (ex.StatusCode == HttpStatusCode.NotFound)
                await ThrowIfContainerMissingAsync(ex, ct);
            throw new CaseConflictException($"Case '{caseId}': write rejected with {(int)ex.StatusCode}.", ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = response.StatusCode;
        var error = response.ErrorMessage;
        response.Dispose();

        if (IsLostRace(status))
        {
            if (status == HttpStatusCode.NotFound)
                await ThrowIfContainerMissingAsync(null, ct);

            var reason = status switch
            {
                HttpStatusCode.PreconditionFailed => "was modified by someone else (ETag mismatch)",
                HttpStatusCode.NotFound => "does not exist",
                _ => "already has this item",
            };
            throw new CaseConflictException($"Case '{caseId}' {reason}.");
        }

        throw new InvalidOperationException($"Cosmos batch for case '{caseId}' failed with {(int)status} {status}: {error}");
    }

    private static bool IsLostRace(HttpStatusCode status) =>
        status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound;

    /// <summary>After a 404: if the container (or its database) does not exist, fail loudly instead of reporting a missing case.</summary>
    private async Task ThrowIfContainerMissingAsync(Exception? inner, CancellationToken ct)
    {
        using var response = await Container.ReadContainerStreamAsync(cancellationToken: ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw MissingContainer(inner);
    }

    private InvalidOperationException MissingContainer(Exception? inner) => new(
        $"Cosmos container '{options.ContainerName}' in database '{options.DatabaseName}' does not exist. " +
        "Create it (Bicep in Azure) or set Storage:Cosmos:EnsureCreated=true for the local emulator.",
        inner);

    private static TransactionalBatchItemRequestOptions IfMatch(CaseItem stored) =>
        new() { IfMatchEtag = stored.ETag };
}

/// <summary>MESSAGE item: marker of a processed Idempotency-Key (id = "{caseId}:msg:{key}"), written in the same batch as the proposal.</summary>
internal sealed record MessageMarker(string Id, string CaseId, string IdempotencyKey, DateTimeOffset At)
{
    public string Type { get; init; } = ItemTypes.Message;
}
