using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Messaging;
using CaseAgents.Core.Operations;
using CaseAgents.Infra.Messaging;
using CaseAgents.Infra.Storage;
using CaseAgents.Infra.Storage.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaseAgents.Infra;

public static class StorageRegistration
{
    /// <summary>
    /// Storage:Mode = InMemory (default) or Cosmos. ICaseStore and ICaseReader resolve to the same singleton.
    /// In Cosmos mode the database and container are created at startup when Storage:Cosmos:EnsureCreated is true.
    /// </summary>
    public static IServiceCollection AddCaseAgentsStorage(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetStorageOptions();
        services.TryAddSingleton(TimeProvider.System);

        if (string.Equals(options.Mode, StorageModes.Cosmos, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(options.Cosmos);
            services.AddSingleton(_ => CosmosClientFactory.Create(options.Cosmos));
            services.AddSingleton<CosmosCaseStore>();
            services.AddSingleton<ICaseStore>(sp => sp.GetRequiredService<CosmosCaseStore>());
            services.AddSingleton<ICaseReader>(sp => sp.GetRequiredService<CosmosCaseStore>());
            if (options.Cosmos.EnsureCreated)
                services.AddHostedService<CosmosBootstrapper>();
        }
        else if (string.Equals(options.Mode, StorageModes.InMemory, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemoryCaseStore>();
            services.AddSingleton<ICaseStore>(sp => sp.GetRequiredService<InMemoryCaseStore>());
            services.AddSingleton<ICaseReader>(sp => sp.GetRequiredService<InMemoryCaseStore>());
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown Storage:Mode '{options.Mode}'. Use '{StorageModes.InMemory}' or '{StorageModes.Cosmos}'.");
        }

        return services;
    }

    /// <summary>
    /// The in-memory event bus (options from section "Bus") registered as IEventBus, and the pump that delivers its events.
    /// Event handlers (ICaseEventHandler) are registered by the AI backend.
    /// </summary>
    public static IServiceCollection AddCaseAgentsMessaging(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(config.GetSection(BusOptions.SectionName).Get<BusOptions>() ?? new BusOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<FaultState>();
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddHostedService<EventBusPump>();
        return services;
    }
}
