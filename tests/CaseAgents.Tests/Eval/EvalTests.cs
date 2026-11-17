using System.Net;
using System.Text;
using System.Text.Json;
using CaseAgents.Core.Models;
using CaseAgents.Eval;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Eval;

public class EvalTests
{
    // In config/questionnaire.json Q4 is yes_no and Q4.1 is free_text.
    private static readonly Questionnaire Q = Scenario.Questionnaire();
    private const string FreeTextId = "Q4.1";

    private static FinalAnswer Final(string caseId, string questionId, string value) => new()
    {
        Id = ItemIds.Answer(caseId, questionId),
        CaseId = caseId,
        QuestionId = questionId,
        Value = value,
        SetBy = Scenario.Analyst,
    };

    private static Proposal Prop(string caseId, string questionId, string? value, ProposalStatus status, bool shadow = false) => new()
    {
        Id = ItemIds.Proposal(caseId, questionId),
        CaseId = caseId,
        QuestionId = questionId,
        Value = value,
        Agent = "Answerer",
        PromptVersion = "v12",
        ModelVersion = "m",
        Status = status,
        Shadow = shadow,
    };

    private static CaseRecord Record(string caseId, FinalAnswer[] answers, Proposal[] proposals) =>
        new(Scenario.NewCase(caseId), [], answers, proposals);

    private static EvalRow Row(IReadOnlyList<EvalRow> rows, string type) => rows.Single(r => r.QuestionType == type);

    [Fact]
    public void Precision_per_question_type()
    {
        var records = new[]
        {
            Record("c1", [Final("c1", "Q4", "Yes"), Final("c1", FreeTextId, "Closed in 2024")],
                [Prop("c1", "Q4", " yes ", ProposalStatus.Accepted), Prop("c1", FreeTextId, "Something else", ProposalStatus.Rejected)]),
            Record("c2", [Final("c2", "Q4", "No")],
                [Prop("c2", "Q4", "Yes", ProposalStatus.Rejected)]),
        };

        var rows = EvalCalculator.Compute(Q, records);

        Assert.Equal(["yes_no", "free_text", "all"], rows.Select(r => r.QuestionType));
        var yesNo = Row(rows, "yes_no");
        Assert.Equal((2, 1), (yesNo.Proposals, yesNo.Correct));
        Assert.Equal(0.5, yesNo.Precision, 10);
        var freeText = Row(rows, "free_text");
        Assert.Equal((1, 0), (freeText.Proposals, freeText.Correct));
        Assert.Equal(0.0, freeText.Precision, 10);
        var all = Row(rows, "all");
        Assert.Equal((3, 1), (all.Proposals, all.Correct));
    }

    [Fact]
    public void Accepted_unchanged_rate_counts_only_decided_non_shadow()
    {
        var records = new[]
        {
            Record("c1", [Final("c1", "Q4", "Yes")], [Prop("c1", "Q4", "Yes", ProposalStatus.Accepted)]),
            Record("c2", [Final("c2", "Q4", "No")], [Prop("c2", "Q4", "Yes", ProposalStatus.Rejected)]),
            Record("c3", [], [Prop("c3", "Q4", "Yes", ProposalStatus.Proposed)]),
            Record("c4", [Final("c4", "Q4", "Yes")], [Prop("c4", "Q4", "Yes", ProposalStatus.Accepted, shadow: true)]),
        };

        var yesNo = Row(EvalCalculator.Compute(Q, records), "yes_no");

        Assert.Equal(2, yesNo.Decided);
        Assert.Equal(1, yesNo.AcceptedUnchanged);
        Assert.Equal(0.5, yesNo.AcceptedUnchangedRate, 10);
    }

    [Fact]
    public void Shadow_proposals_count_for_precision_only()
    {
        var records = new[]
        {
            Record("c1", [Final("c1", "Q4", "Yes")], [Prop("c1", "Q4", "Yes", ProposalStatus.Proposed, shadow: true)]),
        };

        var yesNo = Row(EvalCalculator.Compute(Q, records), "yes_no");

        Assert.Equal((1, 1), (yesNo.Proposals, yesNo.Correct));
        Assert.Equal(1.0, yesNo.Precision, 10);
        Assert.Equal(0, yesNo.Decided);
        Assert.True(double.IsNaN(yesNo.AcceptedUnchangedRate));
    }

    [Fact]
    public void Withdrawn_proposals_are_ignored()
    {
        var records = new[]
        {
            Record("c1", [Final("c1", "Q4", "Yes")], [Prop("c1", "Q4", "Yes", ProposalStatus.Withdrawn)]),
            Record("c2", [Final("c2", "Q4", "Yes")], [Prop("c2", "Q4", null, ProposalStatus.NeedsAnalyst)]),
        };

        var all = Row(EvalCalculator.Compute(Q, records), "all");

        Assert.Equal(0, all.Proposals);
        Assert.True(double.IsNaN(all.Precision));
    }

    [Fact]
    public void Report_contains_rows_and_risk_signal_line()
    {
        var records = new[]
        {
            Record("c1", [Final("c1", "Q4", "Yes")], [Prop("c1", "Q4", "Yes", ProposalStatus.Accepted)]),
        };

        var text = EvalReport.Render(EvalCalculator.Compute(Q, records));

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains(lines, l => l.StartsWith("yes_no", StringComparison.Ordinal) && l.Contains("100.0%"));
        Assert.Contains(lines, l => l.StartsWith("free_text", StringComparison.Ordinal) && l.Contains("n/a"));
        Assert.Contains(lines, l => l.StartsWith("all", StringComparison.Ordinal));
        Assert.Contains("Accepted unchanged is a risk signal (possible over-trust), not a success metric.", lines);
    }

    [Fact]
    public async Task Api_loader_gets_token_then_records_with_bearer()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test") };

        var records = await new ApiRecordLoader(http).LoadAsync();

        Assert.Equal(["POST /dev/token", "GET /api/eval/records"], handler.Requests);
        Assert.Equal("Bearer tok-123", handler.RecordsAuthorization);
        Assert.Contains("\"userId\":\"eval\"", handler.TokenBody);
        Assert.Contains("Case.Review", handler.TokenBody);
        Assert.Equal("c1", Assert.Single(records).Case.CaseId);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? RecordsAuthorization { get; private set; }
        public string TokenBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");

            if (request.RequestUri.AbsolutePath == "/dev/token")
            {
                TokenBody = await request.Content!.ReadAsStringAsync(ct);
                return Json("""{"accessToken":"tok-123","expiresAt":"2030-01-01T00:00:00Z"}""");
            }

            RecordsAuthorization = request.Headers.Authorization?.ToString();
            return Json(JsonSerializer.Serialize(new[] { Record("c1", [], []) }, CaseAgentsJson.Options));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
