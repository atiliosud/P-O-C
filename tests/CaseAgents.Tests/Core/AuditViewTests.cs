using CaseAgents.Core.Models;
using CaseAgents.Core.Rules;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Core;

public class AuditViewTests
{
    private static readonly Questionnaire Q = Scenario.Questionnaire();
    private static readonly AuditActor Agent = new(ActorTypes.Agent, "answerer");
    private static readonly AuditActor Analyst = new(ActorTypes.Person, Scenario.Analyst);
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 10, 30, 0, TimeSpan.Zero);

    private long _seq;

    private AuditEntry Entry(string action, string question, AuditActor actor, DateTimeOffset at, Action<Builder>? set = null)
    {
        var b = new Builder();
        set?.Invoke(b);
        return new AuditEntry
        {
            Id = ItemIds.Audit("c1", ++_seq),
            CaseId = "c1",
            Seq = _seq,
            At = at,
            Actor = actor,
            Action = action,
            QuestionId = question,
            Before = b.Before,
            After = b.After,
            Basis = b.Basis,
            Reason = b.Reason,
            CausedBy = b.CausedBy,
            Proposal = b.Proposal,
        };
    }

    private sealed class Builder
    {
        public string? Before, After, Reason, CausedBy;
        public AuditBasis? Basis;
        public ProposalSnapshot? Proposal;
    }

    private static ProposalSnapshot Snapshot(string? value, double? confidence, ConfidenceBand? band, ProposalStatus status, bool unconfirmed = false) =>
        new(value, confidence, band, "Answerer", "v12", "fake-answerer-1", VerifierPassed: value is not null, status, unconfirmed);

    private IReadOnlyList<AuditEntry> Scenario6()
    {
        var evidence = EvidenceLocator.Locate(Scenario.Documents("c1"), Scenario.BankLetterId, 3, Scenario.BankLetterPage3Line)!;
        var basis = new AuditBasis(Scenario.BankLetterId, 3, evidence.Offset, evidence.Length, evidence.Hash, Scenario.BankLetterPage3Line);
        var decidedAt = new DateTimeOffset(2026, 11, 17, 10, 42, 0, TimeSpan.Zero);

        var list = new List<AuditEntry>
        {
            Entry(AuditActions.ProposalCreated, "Q4", Agent, T0, b =>
            {
                b.After = "Yes";
                b.Basis = basis;
                b.Proposal = Snapshot("Yes", 0.93, ConfidenceBand.High, ProposalStatus.Proposed);
            }),
        };
        foreach (var id in new[] { "Q4.1", "Q4.2", "Q4.3" })
            list.Add(Entry(AuditActions.ProposalCreated, id, Agent, T0, b => b.Proposal = Snapshot("x", 0.9, ConfidenceBand.High, ProposalStatus.Proposed, unconfirmed: true)));

        list.Add(Entry(AuditActions.AnswerConfirmed, "Q4", Analyst, decidedAt, b =>
        {
            b.Before = "Yes";
            b.After = "No";
            b.Reason = Scenario.AnalystReason;
            b.Basis = basis;
        }));
        foreach (var id in new[] { "Q4.1", "Q4.2", "Q4.3" })
        {
            list.Add(Entry(AuditActions.ProposalWithdrawn, id, Agent, decidedAt, b =>
            {
                b.CausedBy = "Q4";
                b.Reason = $"Q4 confirmed as No; {id} applies only if Q4 = Yes";
                b.Proposal = Snapshot("x", 0.9, ConfidenceBand.High, ProposalStatus.Withdrawn, unconfirmed: true);
            }));
        }

        return list;
    }

    [Fact]
    public void AuditView_Q4_row_shows_proposed_yes_final_no_reason_and_effect()
    {
        var view = AuditViewBuilder.Build(Q, Scenario6());

        Assert.Equal(new[] { "Q4", "Q4.1", "Q4.2", "Q4.3" }, view.Rows.Select(r => r.QuestionId));
        var row = view.Rows[0];
        Assert.Equal("Yes", row.ProposedValue);
        Assert.Equal("Answerer agent", row.ProposedBy);
        Assert.Equal("v12", row.PromptVersion);
        Assert.Equal(3, row.Basis!.Page);
        Assert.Equal(Scenario.BankLetterPage3Line, row.Basis.Quote);
        Assert.Equal(ConfidenceBand.High, row.Band);
        Assert.Equal(0.93, row.Confidence);
        Assert.True(row.VerifierPassed);
        Assert.Equal("No", row.FinalValue);
        Assert.Equal("A. Silva", row.FinalBy);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 10, 42, 0, TimeSpan.Zero), row.FinalAt);
        Assert.Equal(Scenario.AnalystReason, row.Reason);
        Assert.Equal(ProposalStatus.Rejected, row.ProposalStatus);
        Assert.Equal("Q4.1 to Q4.3 proposals withdrawn", row.Effect);
        Assert.All(view.Rows.Skip(1), r => Assert.Equal(ProposalStatus.Withdrawn, r.ProposalStatus));
        Assert.Equal("Q4 confirmed as No; Q4.1 applies only if Q4 = Yes", view.Rows[1].Reason);
        Assert.Equal(8, view.Entries.Count);
        Assert.Equal(view.Entries.OrderBy(e => e.Seq).Select(e => e.Seq), view.Entries.Select(e => e.Seq));
    }

    [Fact]
    public void AuditView_marks_accepted_when_final_equals_proposed_value()
    {
        var entries = new[]
        {
            Entry(AuditActions.ProposalCreated, "Q3", Agent, T0, b => b.Proposal = Snapshot("No", 0.9, ConfidenceBand.High, ProposalStatus.Proposed)),
            Entry(AuditActions.AnswerConfirmed, "Q3", Analyst, T0, b => { b.Before = "No"; b.After = "No"; }),
        };

        var row = Assert.Single(AuditViewBuilder.Build(Q, entries).Rows);

        Assert.Equal(ProposalStatus.Accepted, row.ProposalStatus);
        Assert.Equal("No", row.FinalValue);
    }

    [Fact]
    public void AuditView_effect_names_two_one_and_updated_dependents()
    {
        var entries = new[]
        {
            Entry(AuditActions.ProposalWithdrawn, "Q4.1", Agent, T0, b => b.CausedBy = "Q4"),
            Entry(AuditActions.ProposalWithdrawn, "Q4.2", Agent, T0, b => b.CausedBy = "Q4"),
            Entry(AuditActions.ProposalUpdated, "Q4.3", Agent, T0, b => { b.CausedBy = "Q4"; b.Proposal = Snapshot("Yes", 0.9, ConfidenceBand.High, ProposalStatus.Proposed); }),
            Entry(AuditActions.AnswerConfirmed, "Q4", Analyst, T0, b => b.After = "Yes"),
        };

        var q4 = AuditViewBuilder.Build(Q, entries).Rows.Single(r => r.QuestionId == "Q4");

        Assert.Equal("Q4.1 and Q4.2 proposals withdrawn; Q4.3 proposal now a normal proposal", q4.Effect);
        Assert.Null(q4.ProposalStatus);
    }

    [Fact]
    public void AuditView_effect_counts_a_confirmed_needs_analyst_dependent()
    {
        var entries = new[]
        {
            Entry(AuditActions.ProposalUpdated, "Q4.1", Agent, T0, b => { b.CausedBy = "Q4"; b.Proposal = Snapshot("Switzerland", 0.9, ConfidenceBand.High, ProposalStatus.Proposed); }),
            Entry(AuditActions.ProposalNeedsAnalyst, "Q4.2", Agent, T0, b => { b.CausedBy = "Q4"; b.Proposal = Snapshot(null, null, null, ProposalStatus.NeedsAnalyst); }),
            Entry(AuditActions.ProposalUpdated, "Q4.3", Agent, T0, b => { b.CausedBy = "Q4"; b.Proposal = Snapshot("Yes", 0.9, ConfidenceBand.High, ProposalStatus.Proposed); }),
            Entry(AuditActions.AnswerConfirmed, "Q4", Analyst, T0, b => b.After = "Yes"),
        };

        var q4 = AuditViewBuilder.Build(Q, entries).Rows.Single(r => r.QuestionId == "Q4");

        Assert.Equal("Q4.1 to Q4.3 proposals now normal proposals", q4.Effect);
    }

    [Fact]
    public void AuditView_ignores_entries_without_proposal_or_decision()
    {
        var entries = new[] { Entry(AuditActions.CaseTakenOver, "Q1", Analyst, T0) };

        var view = AuditViewBuilder.Build(Q, entries);

        Assert.Empty(view.Rows);
        Assert.Single(view.Entries);
    }
}
