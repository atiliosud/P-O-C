using System.Security.Cryptography;
using System.Text;
using CaseAgents.Core.Models;

namespace CaseAgents.Core.Rules;

/// <summary>Checks that a quote really sits on the cited page and records where.</summary>
public static class EvidenceLocator
{
    /// <summary>Null when the document or page is missing, the quote is empty, or it is not on that page (ordinal, case-sensitive).</summary>
    public static EvidenceRef? Locate(IReadOnlyList<CaseDocument> docs, string documentId, int page, string quote)
    {
        var document = docs.FirstOrDefault(d => d.DocumentId == documentId);
        var pageItem = document?.Pages.FirstOrDefault(p => p.N == page);
        if (pageItem is null)
            return null;

        var normalisedQuote = TextNormalizer.Normalize(quote);
        if (normalisedQuote.Length == 0)
            return null;

        var offset = TextNormalizer.Normalize(pageItem.Text).IndexOf(normalisedQuote, StringComparison.Ordinal);
        if (offset < 0)
            return null;

        return new EvidenceRef(documentId, page, offset, normalisedQuote.Length, Hash(normalisedQuote));
    }

    /// <summary>"sha256:" + lowercase hex SHA-256 of the UTF-8 bytes.</summary>
    public static string Hash(string normalisedQuote) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalisedQuote))).ToLowerInvariant();
}
