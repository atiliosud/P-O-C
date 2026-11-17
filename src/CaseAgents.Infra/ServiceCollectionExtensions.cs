using CaseAgents.Core.Models;
using CaseAgents.Core.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaseAgents.Infra;

public static class StorageModes
{
    public const string InMemory = "InMemory";
    public const string Cosmos = "Cosmos";
}

/// <summary>Configuration section "Storage".</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Mode { get; set; } = StorageModes.InMemory;
    public CosmosOptions Cosmos { get; set; } = new();
}

public sealed class CosmosOptions
{
    /// <summary>Account endpoint for Azure (auth with DefaultAzureCredential). Used when ConnectionString is empty.</summary>
    public string? AccountEndpoint { get; set; }

    /// <summary>Only for the local emulator; set via user-secrets or environment, never in the repo.</summary>
    public string? ConnectionString { get; set; }

    public string DatabaseName { get; set; } = "caseagents";
    public string ContainerName { get; set; } = "cases";

    /// <summary>Create the database and container at startup if missing (local emulator convenience).</summary>
    public bool EnsureCreated { get; set; }

    /// <summary>Accept the emulator's self-signed certificate (gateway mode). Never enable against Azure.</summary>
    public bool EmulatorMode { get; set; }
}

/// <summary>Configuration section "AzureOpenAI". No keys: authentication uses DefaultAzureCredential.</summary>
public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; set; } = "";

    /// <summary>Deployment used by the Answerer.</summary>
    public string Deployment { get; set; } = "";

    /// <summary>Deployment used by the Verifier (the second model). Falls back to Deployment when empty.</summary>
    public string VerifierDeployment { get; set; } = "";

    public string ApiVersion { get; set; } = "2024-10-21";
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Questionnaire (Questionnaire:Path, default questionnaire.json next to the app), the simulated FaultState and
    /// the clock. Every host calls this.
    /// </summary>
    public static IServiceCollection AddCaseAgentsCore(this IServiceCollection services, IConfiguration config)
    {
        var path = config["Questionnaire:Path"];
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(AppContext.BaseDirectory, "questionnaire.json");

        services.AddSingleton(_ => QuestionnaireLoader.Load(path));
        services.AddSingleton<FaultState>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }

    public static StorageOptions GetStorageOptions(this IConfiguration config) =>
        config.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

    public static bool IsInMemoryStorage(this IConfiguration config) =>
        string.Equals(config.GetStorageOptions().Mode, StorageModes.InMemory, StringComparison.OrdinalIgnoreCase);
}
