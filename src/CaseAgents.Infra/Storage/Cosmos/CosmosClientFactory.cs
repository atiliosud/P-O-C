using Azure.Identity;
using CaseAgents.Core.Models;
using Microsoft.Azure.Cosmos;

namespace CaseAgents.Infra.Storage.Cosmos;

public static class CosmosClientFactory
{
    /// <summary>
    /// ConnectionString (local emulator) or AccountEndpoint + DefaultAzureCredential (Azure).
    /// Items are (de)serialized with System.Text.Json and <see cref="CaseAgentsJson"/> options.
    /// </summary>
    public static CosmosClient Create(CosmosOptions options)
    {
        var clientOptions = new CosmosClientOptions
        {
            ApplicationName = "caseagents",
            UseSystemTextJsonSerializerWithOptions = CaseAgentsJson.Create(),
        };

        if (options.EmulatorMode)
        {
            // The emulator uses a self-signed certificate and advertises its own address:
            // gateway mode limited to the configured endpoint works from the host and from containers.
            clientOptions.ConnectionMode = ConnectionMode.Gateway;
            clientOptions.LimitToEndpoint = true;
            clientOptions.HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
            return new CosmosClient(options.ConnectionString, clientOptions);

        if (string.IsNullOrWhiteSpace(options.AccountEndpoint))
        {
            throw new InvalidOperationException(
                "Cosmos storage needs Storage:Cosmos:AccountEndpoint (Azure, DefaultAzureCredential) " +
                "or Storage:Cosmos:ConnectionString (local emulator, via user-secrets or environment).");
        }

        return new CosmosClient(options.AccountEndpoint, new DefaultAzureCredential(), clientOptions);
    }
}
