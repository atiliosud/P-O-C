using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;

namespace CaseAgents.Infra.Storage.Cosmos;

/// <summary>
/// Creates the database and the "cases" container (partition key /caseId)
/// when Storage:Cosmos:EnsureCreated=true. Local emulator convenience; in Azure they come from Bicep.
/// </summary>
public sealed class CosmosBootstrapper(CosmosClient client, CosmosOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => EnsureCreatedAsync(client, options, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static async Task EnsureCreatedAsync(CosmosClient client, CosmosOptions options, CancellationToken ct = default)
    {
        Database database = await client.CreateDatabaseIfNotExistsAsync(options.DatabaseName, cancellationToken: ct);
        await database.CreateContainerIfNotExistsAsync(new ContainerProperties(options.ContainerName, "/caseId"), cancellationToken: ct);
    }
}
