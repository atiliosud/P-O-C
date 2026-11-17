using Microsoft.Extensions.Hosting;

namespace CaseAgents.Agents.Models;

/// <summary>Fails the host at startup if an Azure OpenAI deployment is missing or does not return logprobs.</summary>
public sealed class ModelsStartupCheck(AzureOpenAiChatClient client) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = client.Options;
        var deployments = new[] { options.Deployment, AzureOpenAiVerifier.Deployment(options) }
            .Distinct(StringComparer.Ordinal);

        foreach (var deployment in deployments)
            await client.CompleteAsync(deployment, "Reply with Yes.", "Startup check", null, 1, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
