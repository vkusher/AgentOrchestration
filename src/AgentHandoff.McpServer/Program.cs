using AgentHandoff.McpServer.Configuration;
using AgentHandoff.McpServer.Logging;
using AgentHandoff.McpServer.Search;
using AgentHandoff.McpServer.Tools;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// ----------------------------------------------------------------------------------------------
// AgentHandoff.McpServer
//
// Stdio Model Context Protocol server exposing knowledge-base tools to the BankingInfo agent.
// The KB is backed by Azure AI Search. Two population modes — chosen by config:
//
//   PUSH (default): on first start the seeder uploads DefaultArticles directly via
//                   SearchClient.IndexDocumentsAsync. Self-contained — no extra infra.
//
//   PULL  (set AzureSearch:Indexer:BlobConnectionString): articles are uploaded as JSON blobs
//         into a Storage container, then a Search Data Source + Indexer pulls them in.
//         Production-style RAG pipeline.
//
// Pattern from: https://learn.microsoft.com/agent-framework/agents/tools/local-mcp-tools
// ----------------------------------------------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);

// File logger — the parent process spawns this over stdio and discards stderr,
// so console logs are invisible. The file is the source of truth for diagnostics.
var logPath = Path.Combine(AppContext.BaseDirectory, "mcp-server.log");
FileLoggerProvider.Append(logPath, "");
FileLoggerProvider.Append(logPath, $"=== {DateTimeOffset.Now:O} startup (pid={Environment.ProcessId}) ===");
FileLoggerProvider.Append(logPath, $"baseDir = {AppContext.BaseDirectory}");

// Stdout is reserved for MCP JSON-RPC traffic — log to stderr only.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddProvider(new FileLoggerProvider(logPath));

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

var searchOptions = new AzureSearchOptions();
builder.Configuration.GetSection(AzureSearchOptions.SectionName).Bind(searchOptions);

FileLoggerProvider.Append(logPath,
    $"AzureSearch: configured={searchOptions.IsConfigured}  index='{searchOptions.IndexName}'  " +
    $"endpoint='{searchOptions.Endpoint}'  autoSeed={searchOptions.AutoSeed}");
FileLoggerProvider.Append(logPath,
    $"Indexer:     configured={searchOptions.Indexer.IsConfigured}  container='{searchOptions.Indexer.ContainerName}'  " +
    $"dataSource='{searchOptions.Indexer.DataSourceName}'  indexer='{searchOptions.Indexer.IndexerName}'");

if (searchOptions.IsConfigured)
{
    var endpoint   = new Uri(searchOptions.Endpoint);
    var credential = new AzureKeyCredential(searchOptions.ApiKey!);

    var indexClient  = new SearchIndexClient(endpoint, credential);
    var searchClient = indexClient.GetSearchClient(searchOptions.IndexName);

    builder.Services.AddSingleton(indexClient);
    builder.Services.AddSingleton(searchClient);
    builder.Services.AddSingleton(sp => new KnowledgeBaseSearchService(
        searchClient,
        searchOptions.TopK,
        sp.GetService<ILogger<KnowledgeBaseSearchService>>()));

    if (searchOptions.AutoSeed)
    {
        builder.Services.AddSingleton(sp => new KnowledgeBaseSeeder(
            indexClient,
            searchOptions.IndexName,
            sp.GetService<ILogger<KnowledgeBaseSeeder>>()));

        if (searchOptions.Indexer.IsConfigured)
        {
            builder.Services.AddSingleton(sp => new BlobSeeder(
                searchOptions.Indexer.BlobConnectionString!,
                searchOptions.Indexer.ContainerName,
                sp.GetService<ILogger<BlobSeeder>>()));

            builder.Services.AddSingleton(sp => new IndexerPipeline(
                endpoint, credential,
                sp.GetService<ILogger<IndexerPipeline>>()));
        }
    }
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

KnowledgeBaseTools.SearchService = host.Services.GetService<KnowledgeBaseSearchService>();

// ── Money-transfer extractor: Document Intelligence + Azure OpenAI ──────────
var docIntelOpts = new DocumentIntelligenceOptions();
builder.Configuration.GetSection(DocumentIntelligenceOptions.SectionName).Bind(docIntelOpts);
var transferAoaiOpts = new TransferOpenAIOptions();
builder.Configuration.GetSection(TransferOpenAIOptions.SectionName).Bind(transferAoaiOpts);

FileLoggerProvider.Append(logPath,
    $"DocumentIntelligence: configured={docIntelOpts.IsConfigured}  endpoint='{docIntelOpts.Endpoint}'");
FileLoggerProvider.Append(logPath,
    $"TransferOpenAI:       configured={transferAoaiOpts.IsConfigured}  endpoint='{transferAoaiOpts.Endpoint}'  deployment='{transferAoaiOpts.DeploymentName}'");

if (docIntelOpts.IsConfigured)
{
    MoneyTransferTools.DocIntelClient = new DocumentIntelligenceClient(
        new Uri(docIntelOpts.Endpoint),
        new AzureKeyCredential(docIntelOpts.ApiKey!));
}
if (transferAoaiOpts.IsConfigured)
{
    var aoai = new AzureOpenAIClient(
        new Uri(transferAoaiOpts.Endpoint),
        new AzureKeyCredential(transferAoaiOpts.ApiKey!));
    MoneyTransferTools.OpenAIChat = aoai.GetChatClient(transferAoaiOpts.DeploymentName);
}

if (searchOptions.IsConfigured && searchOptions.AutoSeed)
{
    var startupLog = host.Services.GetRequiredService<ILogger<Program>>();
    var seeder = host.Services.GetRequiredService<KnowledgeBaseSeeder>();

    try
    {
        // Schema is shared by both modes.
        // Returns true if the index had to be dropped + recreated (analyzer/schema change).
        var indexRecreated = await seeder.EnsureIndexAsync();

        if (searchOptions.Indexer.IsConfigured)
        {
            // ── PULL MODE ─────────────────────────────────────────────────────
            startupLog.LogInformation(
                "Pull-model pipeline enabled — using Blob Storage container '{Container}' as the data source.",
                searchOptions.Indexer.ContainerName);

            var blobSeeder = host.Services.GetRequiredService<BlobSeeder>();
            await blobSeeder.EnsureSeededAsync();

            var pipeline = host.Services.GetRequiredService<IndexerPipeline>();

            // If the index was just recreated, the indexer's high-watermark refers to the OLD
            // (now empty) index — reset it so the next run re-indexes every blob.
            if (indexRecreated)
            {
                await pipeline.ResetIndexerAsync(searchOptions.Indexer.IndexerName);
            }

            await pipeline.EnsureAndRunAsync(
                blobConnectionString: searchOptions.Indexer.BlobConnectionString!,
                containerName:        searchOptions.Indexer.ContainerName,
                dataSourceName:       searchOptions.Indexer.DataSourceName,
                indexerName:          searchOptions.Indexer.IndexerName,
                indexName:            searchOptions.IndexName);
        }
        else
        {
            // ── PUSH MODE ─────────────────────────────────────────────────────
            startupLog.LogInformation("Push-model seeding (no blob connection string configured).");
            await seeder.SeedAsync();
        }

        startupLog.LogInformation("Knowledge-base index '{Index}' is ready.", searchOptions.IndexName);
    }
    catch (Exception ex)
    {
        startupLog.LogError(ex,
            "Knowledge-base bootstrap failed; tools will fall back to inline dictionary.");
        KnowledgeBaseTools.SearchService = null;
    }
}
else
{
    var startupLog = host.Services.GetRequiredService<ILogger<Program>>();
    startupLog.LogWarning(
        "AzureSearch not configured — knowledge-base tool will use the inline dictionary fallback.");
}

await host.RunAsync();
