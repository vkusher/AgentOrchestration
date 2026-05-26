namespace AgentHandoff.Engine.Configuration;

/// <summary>
/// Selects which <see cref="Sessions.ISessionRegistry"/> backend to use.
/// </summary>
public sealed class SessionRegistryOptions
{
    public const string SectionName = "SessionRegistry";

    /// <summary>"InMemory" (default) or "Cosmos".</summary>
    public string Provider { get; set; } = "InMemory";

    public CosmosSessionRegistryOptions Cosmos { get; set; } = new();
}

/// <summary>Cosmos DB connection + container settings for the session registry.</summary>
public sealed class CosmosSessionRegistryOptions
{
    /// <summary>Account endpoint (e.g. https://my-account.documents.azure.com:443/). Required when Provider=Cosmos.</summary>
    public string? AccountEndpoint { get; set; }

    /// <summary>Account key. If null, AAD/DefaultAzureCredential is used against AccountEndpoint.</summary>
    public string? AccountKey { get; set; }

    public string DatabaseId  { get; set; } = "AgentHandoff";
    public string ContainerId { get; set; } = "Sessions";

    /// <summary>If true, create database/container on startup if missing. Default true.</summary>
    public bool CreateIfNotExists { get; set; } = true;

    /// <summary>Throughput (RU/s) used when creating the container. Default 400 (shared minimum).</summary>
    public int ProvisionedThroughput { get; set; } = 400;
}
