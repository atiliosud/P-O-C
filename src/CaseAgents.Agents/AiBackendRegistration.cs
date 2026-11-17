using CaseAgents.Agents.Models;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CaseAgents.Agents;

public static class AiBackendRegistration
{
    /// <summary>
    /// Registers the in-process AI backend: options (section "AiBackend"), the answerer/verifier models, the HTTP client
    /// to the Case API with a Dev agent token provider (needs DevTokenIssuer, i.e. Auth:Mode=Dev), the outbox,
    /// the orchestrator (also as the bus's ICaseEventHandler), the status (IAiBackendStatus) and the outbox flusher.
    /// </summary>
    public static IServiceCollection AddAiBackend(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AiBackendOptions.SectionName).Get<AiBackendOptions>() ?? new AiBackendOptions();
        services.AddSingleton(options);
        services.AddAgentModels(configuration);

        services.AddSingleton<CaseAppHttpClientProvider>();
        services.AddSingleton<IAgentTokenProvider, DevAgentTokenProvider>();
        services.AddSingleton<ICaseAppClient, HttpCaseAppClient>();
        services.AddSingleton<Outbox>();

        services.AddSingleton<CaseOrchestrator>();
        services.AddSingleton<ICaseEventHandler>(sp => sp.GetRequiredService<CaseOrchestrator>());
        services.AddSingleton<AiBackendStatus>();
        services.AddSingleton<IAiBackendStatus>(sp => sp.GetRequiredService<AiBackendStatus>());

        services.AddSingleton<OutboxFlusher>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxFlusher>());
        return services;
    }
}
