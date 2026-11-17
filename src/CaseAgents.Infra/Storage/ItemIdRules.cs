namespace CaseAgents.Infra.Storage;

/// <summary>
/// Caller-supplied parts of item ids (case id, document id, question id, Idempotency-Key). Cosmos DB rejects ids that
/// contain / \ ? or #, so both stores refuse them the same way (ArgumentException, which the API turns into 400).
/// </summary>
internal static class ItemIdRules
{
    private static readonly char[] Forbidden = ['/', '\\', '?', '#'];

    public static bool IsValid(string? part) => !string.IsNullOrEmpty(part) && part.IndexOfAny(Forbidden) < 0;

    public static void Validate(string? part, string name)
    {
        if (!IsValid(part))
            throw new ArgumentException($"The {name} '{part}' is empty or contains a character that is not allowed in ids (/ \\ ? #).", name);
    }
}
