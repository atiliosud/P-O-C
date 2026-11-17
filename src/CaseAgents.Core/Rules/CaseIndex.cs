using CaseAgents.Core.Agents;
using CaseAgents.Core.Models;

namespace CaseAgents.Core.Rules;

/// <summary>Keyword index over the pages of ONE case. Built from that case's documents only.</summary>
public sealed class CaseIndex
{
    private readonly IReadOnlyList<Passage> _pages;

    private CaseIndex(IReadOnlyList<Passage> pages) => _pages = pages;

    public static CaseIndex Build(IReadOnlyList<CaseDocument> docs) =>
        new(docs.SelectMany(d => d.Pages.OrderBy(p => p.N).Select(p => new Passage(d.DocumentId, d.Name, p.N, p.Text))).ToList());

    /// <summary>Pages containing any keyword (ignoring case), in document order then page order.</summary>
    public IReadOnlyList<Passage> FindPassages(IEnumerable<string> keywords, int max = 3)
    {
        var words = keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        return _pages
            .Where(p => words.Any(k => p.Text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Take(max)
            .ToList();
    }
}
