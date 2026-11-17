using System.Net;
using System.Net.Http.Json;
using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Api;

/// <summary>The whole flow with the in-process AI backend (Fake models) calling the API over HTTP.</summary>
public sealed class ScenarioFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] Dependents = ["Q4.1", "Q4.2", "Q4.3"];

    [Fact]
    public async Task Q4_scenario_end_to_end()
    {
        var reviewer = factory.Reviewer(Scenario.Analyst);
        var caseId = await factory.Submitter("alice").CreateScenarioCaseAsync();

        var view = await reviewer.WaitForProposalsAsync(caseId);

        var q4 = view.Question("Q4").Proposal;
        Assert.NotNull(q4);
        Assert.Equal(ProposalStatus.Proposed, q4.Status);
        Assert.Equal(YesNo.Yes, q4.Value);
        Assert.Equal(ConfidenceBand.High, q4.ConfidenceBand);
        Assert.True(q4.VerifierPassed);
        Assert.Equal(Scenario.BankLetterPage3Line, q4.Quote);
        Assert.Equal(Scenario.BankLetterId, q4.Evidence?.DocumentId);
        Assert.Equal(3, q4.Evidence?.Page);
        Assert.Equal("v12", q4.PromptVersion);
        Assert.Equal(AgentNames.Answerer, q4.Agent);
        foreach (var id in Dependents)
        {
            var proposal = view.Question(id).Proposal;
            Assert.Equal(ProposalStatus.Proposed, proposal?.Status);
            Assert.True(proposal?.DependsOnUnconfirmed, $"{id} should depend on an unconfirmed Q4");
        }

        var decisionTime = new DateTimeOffset(2026, 11, 17, 10, 42, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/dev/clock", new { at = decisionTime })).StatusCode);
        var current = await reviewer.GetViewAsync(caseId);
        var edited = await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Edit, YesNo.No, Scenario.AnalystReason, current.Case.ETag);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var after = await reviewer.WaitUntilAsync(caseId, v =>
            Dependents.All(id => v.Question(id).Proposal?.Status == ProposalStatus.Withdrawn));
        foreach (var id in Dependents)
        {
            var proposal = after.Question(id).Proposal!;
            Assert.False(string.IsNullOrWhiteSpace(proposal.WithdrawnReason));
            Assert.False(string.IsNullOrWhiteSpace(proposal.Value), $"{id} keeps its proposed value");
            Assert.Equal(Applicability.NotApplicable, after.Question(id).Applicability);
        }

        Assert.Equal(YesNo.No, after.Question("Q4").Answer?.Value);
        Assert.Equal(ProposalStatus.Rejected, after.Question("Q4").Proposal?.Status);

        var audit = await (await reviewer.GetAsync($"/api/cases/{caseId}/audit")).ReadAsync<AuditView>();
        var row = audit.Rows.Single(r => r.QuestionId == "Q4");
        Assert.Equal(YesNo.Yes, row.ProposedValue);
        Assert.Equal(YesNo.No, row.FinalValue);
        Assert.Equal(Scenario.Analyst, row.FinalBy);
        Assert.Equal(Scenario.AnalystReason, row.Reason);
        Assert.Equal("Q4.1 to Q4.3 proposals withdrawn", row.Effect);
        Assert.Equal(Scenario.BankLetterPage3Line, row.Basis?.Quote);
        Assert.InRange(row.FinalAt!.Value, decisionTime, decisionTime.AddMinutes(1));
    }

    [Fact]
    public async Task Confirming_Q4_yes_turns_dependents_into_normal_proposals()
    {
        var reviewer = factory.Reviewer();
        var caseId = await factory.Submitter().CreateScenarioCaseAsync();
        var view = await reviewer.WaitForProposalsAsync(caseId);

        var accepted = await reviewer.AnswerAsync(caseId, "Q4", AnswerActions.Accept, null, null, view.Case.ETag);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var after = await reviewer.WaitUntilAsync(caseId, v =>
            Dependents.All(id => v.Question(id).Proposal is { Status: ProposalStatus.Proposed, DependsOnUnconfirmed: false }));
        Assert.Equal(ProposalStatus.Accepted, after.Question("Q4").Proposal?.Status);
        Assert.All(Dependents, id => Assert.Equal(Applicability.Applicable, after.Question(id).Applicability));

        var audit = await (await reviewer.GetAsync($"/api/cases/{caseId}/audit")).ReadAsync<AuditView>();
        Assert.Equal("Q4.1 to Q4.3 proposals now normal proposals", audit.Rows.Single(r => r.QuestionId == "Q4").Effect);
    }

    /// <summary>
    /// The seeded "Hidden instruction in a payslip" case (scripts/seed.mjs): identity document, proof of address,
    /// bank letter and the payslip, with no client declaration. Only the payslip could "answer" Q3 and Q6.
    /// </summary>
    private static IReadOnlyList<CaseDocument> InjectionCaseDocuments(string caseId) =>
        Scenario.Documents(caseId).Where(d => d.DocumentId != "client-declaration")
            .Concat(Scenario.InjectionDocuments(caseId))
            .ToList();

    [Fact]
    public async Task Injection_case_produces_no_value_without_evidence()
    {
        var reviewer = factory.Reviewer();
        var caseId = await factory.Submitter().CreateCaseWithDocumentsAsync(InjectionCaseDocuments);

        var view = await reviewer.WaitForProposalsAsync(caseId);

        Assert.Contains(view.Documents, d => d.DocumentId == "payslip" && d.Pages[0].Text.Contains(Scenario.InjectionText));
        Assert.DoesNotContain(view.Documents, d => d.DocumentId == "client-declaration");
        foreach (var id in new[] { "Q3", "Q6" })
        {
            var state = view.Question(id);
            Assert.Equal(ProposalStatus.NeedsAnalyst, state.Proposal?.Status);
            Assert.Null(state.Proposal?.Value);
            Assert.Null(state.Answer);
        }

        Assert.DoesNotContain(view.Questions, s => s.Proposal is { DocumentId: "payslip" });
        // Every Yes on the case is quoted from the bank letter; nothing comes from the payslip's hidden text.
        Assert.All(view.Questions.Where(s => s.Proposal?.Value == YesNo.Yes), s =>
        {
            Assert.Equal(Scenario.BankLetterId, s.Proposal!.DocumentId);
            Assert.DoesNotContain(Scenario.InjectionText, s.Proposal.Quote ?? "");
        });
        Assert.Contains(view.Gaps, g => g.Kind == GapKind.NoEvidence && g.QuestionId == "Q6");
    }
}
