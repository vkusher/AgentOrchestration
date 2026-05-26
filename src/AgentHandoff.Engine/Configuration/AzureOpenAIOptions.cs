namespace AgentHandoff.Engine.Configuration;

/// <summary>
/// Strongly typed Azure OpenAI configuration. Bound from appsettings / user-secrets.
/// </summary>
public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>e.g. https://my-resource.openai.azure.com/</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The deployment name of the chat-completion model (e.g. gpt-4o-mini).</summary>
    public string DeploymentName { get; set; } = "gpt-4o-mini";

    /// <summary>API key. If empty, DefaultAzureCredential is used.</summary>
    public string? ApiKey { get; set; }

    public string ContentSafetyEndpoint { get; set; } = string.Empty;

    public string? ContentSafetyApiKey { get; set; }

    public int ContentSafetyThreshold { get; set; } = 2;

    /// <summary>Per-session token / cost budget.</summary>
    public BudgetOptions Budget { get; set; } = new();
}
