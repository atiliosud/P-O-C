using System.Net;
using System.Net.Http.Json;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Models;
using CaseAgents.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace CaseAgents.Tests.Api;

public sealed class CaseLifecycleTests(NoAiBackendApi api) : IClassFixture<NoAiBackendApi>
{
    private ApiFactory Factory => api.Factory;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_case_requires_a_title(string? title)
    {
        var response = await Factory.Submitter().PostAsJsonAsync("/api/cases", new { title });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_case_rejects_a_title_over_200_characters()
    {
        var response = await Factory.Submitter().PostAsJsonAsync("/api/cases", new { title = new string('x', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_case_returns_201_draft_owned_by_the_caller_with_an_audit_entry()
    {
        var response = await Factory.Submitter("erin").PostAsJsonAsync("/api/cases", new { title = "  Accounts outside the EU  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = await response.ReadAsync<CaseItem>();
        Assert.Equal("Accounts outside the EU", item.Title);
        Assert.Equal("erin", item.SubmittedBy);
        Assert.Equal(CaseStatus.Draft, item.Status);
        Assert.False(string.IsNullOrEmpty(item.ETag));

        var audit = await Factory.Services.GetRequiredService<ICaseReader>().GetAuditAsync(item.CaseId);
        var entry = Assert.Single(audit);
        Assert.Equal(AuditActions.CaseCreated, entry.Action);
        Assert.Equal(new AuditActor(ActorTypes.Person, "erin"), entry.Actor);
    }

    [Fact]
    public async Task Add_document_requires_if_match()
    {
        var submitter = Factory.Submitter();
        var item = await submitter.CreateCaseAsync();

        var response = await submitter.SendAsync(
            HttpMethod.Post, $"/api/cases/{item.CaseId}/documents", Request(Scenario.Documents(item.CaseId)[0]));

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Fact]
    public async Task Add_document_rejects_an_invalid_document()
    {
        var submitter = Factory.Submitter();
        var item = await submitter.CreateCaseAsync();

        var noPages = new AddDocumentRequest("doc-1", "Bank letter", "bank_letter", []);
        var badId = new AddDocumentRequest("bad:id", "Bank letter", "bank_letter", [new DocumentPage(1, "text")]);
        var samePageTwice = new AddDocumentRequest("doc-1", "Bank letter", "bank_letter", [new DocumentPage(1, "a"), new DocumentPage(1, "b")]);

        foreach (var request in new[] { noPages, badId, samePageTwice })
        {
            var response = await submitter.SendAsync(HttpMethod.Post, $"/api/cases/{item.CaseId}/documents", request, item.ETag);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public void Document_text_over_the_total_cap_is_rejected_even_when_every_page_is_valid()
    {
        var page = new string('x', 20_000);
        var tooLarge = Enumerable.Range(1, 26).Select(n => new DocumentPage(n, page)).ToList();   // 520,000 characters
        var atCap = Enumerable.Range(1, 25).Select(n => new DocumentPage(n, page)).ToList();      // 500,000 characters

        Assert.NotNull(CaseAgents.Api.CaseEndpoints.ValidateDocument(new AddDocumentRequest("doc-1", "Bank letter", "bank_letter", tooLarge)));
        Assert.Null(CaseAgents.Api.CaseEndpoints.ValidateDocument(new AddDocumentRequest("doc-1", "Bank letter", "bank_letter", atCap)));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    public async Task Add_document_with_a_reserved_character_in_the_id_returns_400(string documentId)
    {
        var submitter = Factory.Submitter();
        var item = await submitter.CreateCaseAsync();
        var request = new AddDocumentRequest(documentId, "Bank letter", "bank_letter", [new DocumentPage(1, "text")]);

        var response = await submitter.SendAsync(HttpMethod.Post, $"/api/cases/{item.CaseId}/documents", request, item.ETag);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await submitter.GetViewAsync(item.CaseId)).Documents);
    }

    [Fact]
    public async Task Add_document_returns_201_with_a_new_version_and_stamps_the_time()
    {
        var submitter = Factory.Submitter();
        var item = await submitter.CreateCaseAsync();
        var document = Scenario.Documents(item.CaseId)[2];

        var response = await submitter.AddDocumentAsync(item.CaseId, document, item.ETag!);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var updated = await response.ReadAsync<CaseItem>();
        Assert.NotEqual(item.ETag, updated.ETag);
        var view = await submitter.GetViewAsync(item.CaseId);
        var stored = Assert.Single(view.Documents);
        Assert.Equal(Scenario.BankLetterId, stored.DocumentId);
        Assert.Equal(3, stored.Pages.Count);
        Assert.True(stored.AddedAt >= item.CreatedAt);
    }

    [Fact]
    public async Task Add_document_with_a_stale_version_or_an_existing_id_returns_409()
    {
        var submitter = Factory.Submitter();
        var item = await submitter.CreateCaseAsync();
        var documents = Scenario.Documents(item.CaseId);
        var added = await (await submitter.AddDocumentAsync(item.CaseId, documents[0], item.ETag!)).ReadAsync<CaseItem>();

        var stale = await submitter.AddDocumentAsync(item.CaseId, documents[1], item.ETag!);
        var duplicate = await submitter.AddDocumentAsync(item.CaseId, documents[0], added.ETag!);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version_changed", await stale.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("conflict", await duplicate.ErrorCodeAsync());
    }

    [Fact]
    public async Task Add_document_to_another_submitters_case_returns_404()
    {
        var item = await Factory.Submitter("owner").CreateCaseAsync();

        var response = await Factory.Submitter("intruder").AddDocumentAsync(item.CaseId, Scenario.Documents(item.CaseId)[0], item.ETag!);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Ready_marks_the_case_ready_and_publishes_case_ready()
    {
        var submitter = Factory.Submitter();
        var caseId = await submitter.CreateCaseWithDocumentsAsync(Scenario.Documents, ready: false);
        var etag = (await submitter.GetViewAsync(caseId)).Case.ETag;

        var response = await submitter.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/ready", etag: etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await response.ReadAsync<CaseItem>();
        Assert.Equal(CaseStatus.Ready, item.Status);
        await api.Events.WaitForAsync(e => e.CaseId == caseId && e.EventType == CaseEventTypes.CaseReady);
    }

    [Fact]
    public async Task Ready_twice_returns_409_and_ready_without_if_match_returns_428()
    {
        var submitter = Factory.Submitter();
        var caseId = await submitter.CreateScenarioCaseAsync();
        var etag = (await submitter.GetViewAsync(caseId)).Case.ETag;

        var again = await submitter.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/ready", etag: etag);
        var noIfMatch = await submitter.SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/ready");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal((HttpStatusCode)428, noIfMatch.StatusCode);
    }

    [Fact]
    public async Task Get_case_returns_the_view_and_the_etag_header()
    {
        var caseId = await Factory.Submitter().CreateScenarioCaseAsync();

        var response = await Factory.Reviewer().GetAsync($"/api/cases/{caseId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.ReadAsync<CaseView>();
        Assert.Equal(view.Case.ETag, response.Headers.ETag?.Tag);
        Assert.Equal(9, view.Questions.Count);
        Assert.Equal("v12", view.PromptVersion);
        Assert.Contains(view.Gaps, g => g.Kind == GapKind.MissingDocument && g.DocumentKind == "source_of_funds");
    }

    [Fact]
    public async Task Get_unknown_case_returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Factory.Reviewer().GetAsync("/api/cases/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Factory.Reviewer().GetAsync("/api/cases/nope/audit")).StatusCode);
    }

    private static AddDocumentRequest Request(CaseDocument d) => new(d.DocumentId, d.Name, d.Kind, d.Pages);
}
