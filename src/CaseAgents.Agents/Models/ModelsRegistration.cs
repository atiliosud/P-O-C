using Azure.Core;
using Azure.Identity;
using CaseAgents.Core.Agents;
using CaseAgents.Infra;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CaseAgents.Agents.Models;

public static class ModelsRegistration
{
    public const string SectionName = "Models";
    public const string Fake = "Fake";
    public const string AzureOpenAi = "AzureOpenAI";

    /// <summary>Registers IAnswerer and IVerifier. Models:Mode = Fake (default) or AzureOpenAI; anything else throws.</summary>
    public static IServiceCollection AddAgentModels(this IServiceCollection services, IConfiguration config)
    {
        var mode = config[$"{SectionName}:Mode"];
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals(Fake, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAnswerer, FakeAnswerer>();
            services.AddSingleton<IVerifier, FakeVerifier>();
            return services;
        }

        if (!mode.Equals(AzureOpenAi, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown Models:Mode '{mode}'. Use '{Fake}' or '{AzureOpenAi}'.");

        var options = config.GetSection(AzureOpenAiOptions.SectionName).Get<AzureOpenAiOptions>() ?? new AzureOpenAiOptions();
        services.AddSingleton(options);
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton(sp => new AzureOpenAiChatClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            sp.GetRequiredService<TokenCredential>(),
            sp.GetRequiredService<AzureOpenAiOptions>()));
        services.AddSingleton<IAnswerer, AzureOpenAiAnswerer>();
        services.AddSingleton<IVerifier, AzureOpenAiVerifier>();
        services.AddSingleton<IHostedService, ModelsStartupCheck>();
        return services;
    }
}
