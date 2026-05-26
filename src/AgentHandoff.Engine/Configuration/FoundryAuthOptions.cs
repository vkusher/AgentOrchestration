namespace AgentHandoff.Engine.Configuration;

/// <summary>
/// Optional authentication configuration for Microsoft AI Foundry persistent agents
/// (used by agents whose Runtime.Transport == "foundry"). When all three fields are
/// populated the engine authenticates with a service-principal client secret;
/// otherwise it falls back to DefaultAzureCredential (Azure CLI / managed identity / etc.).
///
/// Store ClientSecret outside source control — user-secrets for dev, Key Vault / env vars
/// (Foundry__ClientSecret) in production.
/// </summary>
public sealed class FoundryAuthOptions
{
    public const string SectionName = "Foundry";

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public bool HasClientSecret =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
