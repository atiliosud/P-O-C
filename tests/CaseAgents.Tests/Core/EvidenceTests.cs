using CaseAgents.Core.Rules;
using CaseAgents.Tests.TestSupport;

namespace CaseAgents.Tests.Core;

public class EvidenceTests
{
    private static readonly IReadOnlyList<CaseAgents.Core.Models.CaseDocument> Docs = Scenario.Documents("c1");

    [Fact]
    public void Normalize_collapses_whitespace_and_newlines()
    {
        Assert.Equal("a b c d", TextNormalizer.Normalize("  a \n\n b\t\tc\r\nd  "));
    }

    [Fact]
    public void Locate_finds_bank_letter_line_on_page_3_with_offset_and_sha256_hash()
    {
        var evidence = EvidenceLocator.Locate(Docs, Scenario.BankLetterId, 3, Scenario.BankLetterPage3Line);

        Assert.NotNull(evidence);
        // Normalised page 3 starts with "Account status " (15 characters).
        Assert.Equal(15, evidence.Offset);
        Assert.Equal(Scenario.BankLetterPage3Line.Length, evidence.Length);
        Assert.Equal(3, evidence.Page);
        Assert.StartsWith("sha256:", evidence.Hash);
        Assert.Matches("^sha256:[0-9a-f]{64}$", evidence.Hash);
        Assert.Equal(EvidenceLocator.Hash(Scenario.BankLetterPage3Line), evidence.Hash);
    }

    [Fact]
    public void Locate_returns_null_when_quote_is_on_another_page()
    {
        Assert.Null(EvidenceLocator.Locate(Docs, Scenario.BankLetterId, 1, Scenario.BankLetterPage3Line));
    }

    [Fact]
    public void Locate_tolerates_different_whitespace()
    {
        var quote = "The account   held with Example Bank (Zurich)\nwas closed on 14 March 2024.";

        Assert.NotNull(EvidenceLocator.Locate(Docs, Scenario.BankLetterId, 3, quote));
    }

    [Fact]
    public void Locate_is_case_sensitive()
    {
        Assert.Null(EvidenceLocator.Locate(Docs, Scenario.BankLetterId, 3, Scenario.BankLetterPage3Line.ToUpperInvariant()));
    }

    [Fact]
    public void Locate_returns_null_for_missing_document_page_or_empty_quote()
    {
        Assert.Null(EvidenceLocator.Locate(Docs, "nope", 3, Scenario.BankLetterPage3Line));
        Assert.Null(EvidenceLocator.Locate(Docs, Scenario.BankLetterId, 9, Scenario.BankLetterPage3Line));
        Assert.Null(EvidenceLocator.Locate(Docs, Scenario.BankLetterId, 3, "   "));
    }
}
