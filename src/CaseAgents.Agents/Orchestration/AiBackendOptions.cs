namespace CaseAgents.Agents;

/// <summary>Configuration section "AiBackend".</summary>
public sealed class AiBackendOptions
{
    public const string SectionName = "AiBackend";

    public bool Enabled { get; set; } = true;
    public string CaseApiBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>When true, every proposal is written as shadow (visible to agents and eval, hidden from analysts).</summary>
    public bool ShadowMode { get; set; }

    public string AgentUserId { get; set; } = "ai-backend";
    public int MaxConflictRetries { get; set; } = 5;
    public TimeSpan OutboxFlushInterval { get; set; } = TimeSpan.FromSeconds(2);
}
