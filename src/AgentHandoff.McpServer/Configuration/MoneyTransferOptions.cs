namespace AgentHandoff.McpServer.Configuration;

/// <summary>
/// Configuration for the money-transfer extraction tools (Document Intelligence + Azure OpenAI).
/// Bound from configuration sections: <c>DocumentIntelligence</c> and <c>AzureOpenAI</c>.
/// </summary>
public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";

    /// <summary>e.g. https://my-docintel.cognitiveservices.azure.com/</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API key. Empty → DefaultAzureCredential.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Document Intelligence model id; default is the OCR-only prebuilt-read.</summary>
    public string ModelId { get; set; } = "prebuilt-read";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Azure OpenAI configuration used by the money-transfer extractor. Reuses the
/// <c>AzureOpenAI</c> section already populated by the rest of the system.
/// </summary>
public sealed class TransferOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    public string  Endpoint       { get; set; } = string.Empty;
    public string? ApiKey         { get; set; }
    public string  DeploymentName { get; set; } = "gpt-4o-mini";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(DeploymentName);
}
