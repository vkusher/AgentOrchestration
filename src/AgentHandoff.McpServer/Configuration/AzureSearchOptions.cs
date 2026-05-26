namespace AgentHandoff.McpServer.Configuration;

/// <summary>
/// Optional Azure-Search-Indexer configuration. When <see cref="BlobConnectionString"/>
/// is set, the McpServer seeds article JSON to a blob container and registers an
/// Azure Search Data Source + Indexer that pulls from that container into the KB index.
/// When empty, the simpler push-model seeder uploads documents directly via the SDK.
/// </summary>
public sealed class AzureSearchIndexerOptions
{
    public string? BlobConnectionString { get; set; }
    public string  ContainerName        { get; set; } = "kb-articles";
    public string  DataSourceName       { get; set; } = "agent-handoff-bank-blob";
    public string  IndexerName          { get; set; } = "agent-handoff-bank-indexer";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BlobConnectionString);
}

/// <summary>
/// Azure AI Search configuration for the knowledge-base RAG tool.
/// </summary>
public sealed class AzureSearchOptions
{
    public const string SectionName = "AzureSearch";

    public string Endpoint    { get; set; } = string.Empty;
    public string? ApiKey     { get; set; }
    public string IndexName   { get; set; } = "agent-handoff-bank-kb";
    public int    TopK        { get; set; } = 3;

    /// <summary>
    /// If true, ensure the index exists and seed it with the default articles when empty.
    /// Idempotent — runs once per fresh service.
    /// </summary>
    public bool AutoSeed      { get; set; } = true;

    /// <summary>Optional pull-model pipeline (blob → indexer → index).</summary>
    public AzureSearchIndexerOptions Indexer { get; set; } = new();

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);
}
