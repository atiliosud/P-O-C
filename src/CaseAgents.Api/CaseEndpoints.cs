using System.Security.Claims;
using System.Text.RegularExpressions;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Core.Security;
using CaseAgents.Infra.Auth;

namespace CaseAgents.Api;

/// <summary>
/// Cases: questionnaire, list, create, documents, ready, read, take over / release, audit and eval records.
/// Roles are checked by the endpoint policies; ownership is checked again in code. The caller's id always comes
/// from the token. Every write needs If-Match with the case ETag (428 without it, 409 version_changed when stale).
/// </summary>
public static partial class CaseEndpoints
{
    public const int MaxTitleLength = 200;
    public const int MaxDocumentNameLength = 200;
    public const int MaxKindLength = 64;
    public const int MaxPages = 200;
    public const int MaxPageTextLength = 20_000;

    /// <summary>Whole-document cap: at most about 1.5 MB in UTF-8, under the 2 MB Cosmos item and batch limit.</summary>
    public const int MaxDocumentTextLength = 500_000;

    public static IEndpointRouteBuilder MapCaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/questionnaire", (Questionnaire q) => Results.Ok(q)).RequireAuthorization(AuthPolicies.AnyRole);

        app.MapGet("/api/cases", ListCasesAsync).RequireAuthorization(AuthPolicies.SubmitOrReview);
        app.MapPost("/api/cases", CreateCaseAsync).RequireAuthorization(AuthPolicies.Submit);
        app.MapPost("/api/cases/{caseId}/documents", AddDocumentAsync).RequireAuthorization(AuthPolicies.Submit);
        app.MapPost("/api/cases/{caseId}/ready", MarkReadyAsync).RequireAuthorization(AuthPolicies.Submit);
        app.MapGet("/api/cases/{caseId}", GetCaseAsync).RequireAuthorization(AuthPolicies.AnyRole);
        app.MapPost("/api/cases/{caseId}/takeover", TakeOverAsync).RequireAuthorization(AuthPolicies.Review);
        app.MapPost("/api/cases/{caseId}/release", ReleaseAsync).RequireAuthorization(AuthPolicies.Review);
        app.MapGet("/api/cases/{caseId}/audit", GetAuditAsync).RequireAuthorization(AuthPolicies.Review);

        app.MapGet("/api/eval/records", async (ICaseReader reader, CancellationToken ct) =>
                Results.Ok(await reader.ListCaseRecordsAsync(ct)))
            .RequireAuthorization(AuthPolicies.Review);

        return app;
    }

    /// <summary>Returns an error message, or null when the title is valid (1 to 200 characters after trimming).</summary>
    public static string? ValidateTitle(string? title)
    {
        var trimmed = title?.Trim() ?? "";
        return trimmed.Length is 0 or > MaxTitleLength ? $"title is required (1 to {MaxTitleLength} characters)." : null;
    }

    /// <summary>Returns an error message, or null when the document is valid.</summary>
    public static string? ValidateDocument(AddDocumentRequest? request)
    {
        if (request is null)
            return "A document body is required.";
        if (request.DocumentId is null || !DocumentIdPattern().IsMatch(request.DocumentId))
            return "documentId is required (1 to 64 letters, digits, '.', '_' or '-').";
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > MaxDocumentNameLength)
            return $"name is required (1 to {MaxDocumentNameLength} characters).";
        if (string.IsNullOrWhiteSpace(request.Kind) || request.Kind.Trim().Length > MaxKindLength)
            return $"kind is required (1 to {MaxKindLength} characters).";
        if (request.Pages is null || request.Pages.Count is 0 or > MaxPages)
            return $"pages is required (1 to {MaxPages} pages).";
        if (request.Pages.Any(p => p is null || p.N < 1 || p.Text is null || p.Text.Length > MaxPageTextLength))
            return $"Every page needs n >= 1 and a text of at most {MaxPageTextLength} characters.";
        if (request.Pages.Select(p => p.N).Distinct().Count() != request.Pages.Count)
            return "Page numbers must be unique.";
        if (request.Pages.Sum(p => (long)p.Text.Length) > MaxDocumentTextLength)
            return $"The document text is too large (at most {MaxDocumentTextLength} characters in total).";
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex DocumentIdPattern();

    private static async Task<IResult> ListCasesAsync(ClaimsPrincipal principal, ICaseReader reader, Questionnaire q, CancellationToken ct)
    {
        var user = principal.ToUserContext();
        var cases = await reader.ListCasesAsync(user.IsReviewer ? null : user.UserId, ct);

        var summaries = new List<CaseSummary>();
        foreach (var item in cases)
        {
            var record = await reader.GetCaseRecordAsync(item.CaseId, ct);
            if (record is not null)
                summaries.Add(CaseViewBuilder.Summarize(q, record));
        }

        return Results.Ok(summaries);
    }

    private static async Task<IResult> CreateCaseAsync(
        CreateCaseRequest? request, ClaimsPrincipal principal, ICaseStore store, TimeProvider time, CancellationToken ct)
    {
        var error = ValidateTitle(request?.Title);
        if (error is not null)
            return ApiResults.BadRequest(error);

        var user = principal.ToUserContext();
        var caseId = ItemIds.NewCaseId();
        var item = new CaseItem
        {
            Id = caseId,
            CaseId = caseId,
            Title = request!.Title!.Trim(),
            SubmittedBy = user.UserId,
            Status = CaseStatus.Draft,
            CreatedAt = time.GetUtcNow(),
        };

        var stored = await store.CreateCaseAsync(item, AuditDrafts.Simple(AuditDrafts.Person(user), AuditActions.CaseCreated, item.Title), ct);
        return Results.Created($"/api/cases/{stored.CaseId}", stored);
    }

    private static async Task<IResult> AddDocumentAsync(
        string caseId,
        AddDocumentRequest? request,
        HttpRequest http,
        ClaimsPrincipal principal,
        ICaseStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        var user = principal.ToUserContext();
        var record = await store.GetCaseRecordAsync(caseId, ct);
        if (record is null || !IsOwner(user, record.Case))
            return Results.NotFound();

        var ifMatch = http.IfMatch();
        if (ifMatch is null)
            return ApiResults.PreconditionRequired();

        var error = ValidateDocument(request);
        if (error is not null)
            return ApiResults.BadRequest(error);

        if (record.Case.Status == CaseStatus.Closed)
            return ApiResults.Conflict(ConflictCodes.Conflict, "The case is closed.");
        if (ifMatch != record.Case.ETag)
            return ApiResults.VersionChanged();
        if (record.Documents.Any(d => d.DocumentId == request!.DocumentId))
            return ApiResults.Conflict(ConflictCodes.Conflict, $"The case already has a document '{request!.DocumentId}'.");

        var document = new CaseDocument
        {
            Id = ItemIds.Document(caseId, request!.DocumentId!),
            CaseId = caseId,
            DocumentId = request.DocumentId!,
            Name = request.Name!.Trim(),
            Kind = request.Kind!.Trim(),
            Pages = request.Pages!.OrderBy(p => p.N).ToList(),
            AddedAt = time.GetUtcNow(),
        };

        try
        {
            var updated = await store.AddDocumentAsync(
                record.Case with { ETag = ifMatch },
                document,
                AuditDrafts.Simple(AuditDrafts.Person(user), AuditActions.DocumentAdded, document.DocumentId),
                ct);
            return Results.Created($"/api/cases/{caseId}", updated);
        }
        catch (CaseConflictException)
        {
            return ApiResults.VersionChanged();
        }
        catch (ArgumentException ex)
        {
            // The store refuses empty ids or ids with '/', '\', '?' or '#'.
            return ApiResults.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> MarkReadyAsync(
        string caseId, HttpRequest http, ClaimsPrincipal principal, ICaseStore store, IEventBus bus, CancellationToken ct)
    {
        var user = principal.ToUserContext();
        var item = await store.GetCaseAsync(caseId, ct);
        if (item is null || !IsOwner(user, item))
            return Results.NotFound();

        var ifMatch = http.IfMatch();
        if (ifMatch is null)
            return ApiResults.PreconditionRequired();
        if (item.Status != CaseStatus.Draft)
            return ApiResults.Conflict(ConflictCodes.Conflict, $"Only a Draft case can be marked ready (current: {item.Status}).");

        return await UpdateAndPublishAsync(
            store, bus, item, ifMatch, item with { Status = CaseStatus.Ready }, AuditDrafts.Person(user), AuditActions.CaseReady, CaseEventTypes.CaseReady, ct);
    }

    private static async Task<IResult> GetCaseAsync(
        string caseId, ClaimsPrincipal principal, HttpResponse response, ICaseReader reader, CaseViews views, CancellationToken ct)
    {
        var user = principal.ToUserContext();
        var record = await reader.GetCaseRecordAsync(caseId, ct);

        // A case the caller may not read is reported as missing, so case ids cannot be probed.
        if (record is null || !CaseAccess.CanRead(user, record.Case))
            return Results.NotFound();

        response.SetETag(record.Case.ETag);
        return Results.Ok(views.Build(record, user));
    }

    private static async Task<IResult> TakeOverAsync(
        string caseId, HttpRequest http, ClaimsPrincipal principal, ICaseStore store, IEventBus bus, CancellationToken ct)
    {
        var user = principal.ToUserContext();
        var item = await store.GetCaseAsync(caseId, ct);
        if (item is null)
            return Results.NotFound();

        var ifMatch = http.IfMatch();
        if (ifMatch is null)
            return ApiResults.PreconditionRequired();
        if (item.AiLocked)
            return ApiResults.Conflict(ConflictCodes.AiLocked, $"The case is already taken over by {item.LockedBy}.");

        // One write sets the lock AND changes the version, so an agent write already in flight fails its If-Match.
        return await UpdateAndPublishAsync(
            store, bus, item, ifMatch, item with { AiLocked = true, LockedBy = user.UserId }, AuditDrafts.Person(user), AuditActions.CaseTakenOver, CaseEventTypes.CaseTakenOver, ct);
    }

    private static async Task<IResult> ReleaseAsync(
        string caseId, HttpRequest http, ClaimsPrincipal principal, ICaseStore store, IEventBus bus, CancellationToken ct)
    {
        var user = principal.ToUserContext();
        var item = await store.GetCaseAsync(caseId, ct);
        if (item is null)
            return Results.NotFound();

        var ifMatch = http.IfMatch();
        if (ifMatch is null)
            return ApiResults.PreconditionRequired();
        if (!item.AiLocked)
            return ApiResults.Conflict(ConflictCodes.Conflict, "The case is not taken over.");

        return await UpdateAndPublishAsync(
            store, bus, item, ifMatch, item with { AiLocked = false, LockedBy = null }, AuditDrafts.Person(user), AuditActions.CaseReleased, CaseEventTypes.CaseReleased, ct);
    }

    private static async Task<IResult> GetAuditAsync(string caseId, ICaseReader reader, Questionnaire q, CancellationToken ct)
    {
        if (await reader.GetCaseAsync(caseId, ct) is null)
            return Results.NotFound();

        return Results.Ok(AuditViewBuilder.Build(q, await reader.GetAuditAsync(caseId, ct)));
    }

    /// <summary>Version check, case update with one audit entry, then the event (published only after the write).</summary>
    private static async Task<IResult> UpdateAndPublishAsync(
        ICaseStore store,
        IEventBus bus,
        CaseItem current,
        string ifMatch,
        CaseItem updated,
        AuditActor actor,
        string auditAction,
        string eventType,
        CancellationToken ct)
    {
        if (ifMatch != current.ETag)
            return ApiResults.VersionChanged();

        CaseItem stored;
        try
        {
            stored = await store.UpdateCaseAsync(current with { ETag = ifMatch }, updated, AuditDrafts.Simple(actor, auditAction), ct);
        }
        catch (CaseConflictException)
        {
            return ApiResults.VersionChanged();
        }
        catch (ArgumentException ex)
        {
            // The store refuses empty ids or ids with '/', '\', '?' or '#'.
            return ApiResults.BadRequest(ex.Message);
        }

        await bus.PublishAsync(stored.CaseId, eventType, ct: ct);
        return Results.Ok(stored);
    }

    private static bool IsOwner(UserContext user, CaseItem item) =>
        string.Equals(item.SubmittedBy, user.UserId, StringComparison.Ordinal);
}
