using System.Text.RegularExpressions;

namespace CaseAgents.Core.Rules;

public static partial class TextNormalizer
{
    /// <summary>Collapses every whitespace run (including newlines) to one space and trims.</summary>
    public static string Normalize(string text) => WhitespaceRun().Replace(text ?? "", " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
