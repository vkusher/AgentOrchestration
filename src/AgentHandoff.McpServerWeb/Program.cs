using System.Text.Json;
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
using Microsoft.Extensions.Logging;

// ----------------------------------------------------------------------------------------------
// AgentHandoff.McpServerWeb
//
// ASP.NET Core web app hosting the Model Context Protocol server exposing knowledge-base
// tools to agents via HTTP. This web app wraps the MCP server logic and exposes HTTP
// endpoints to retrieve tools and execute them. It uses the same Azure AI Search backend
// as the original AgentHandoff.McpServer.
//
// Configuration (appsettings.json or environment variables):
//   - AzureSearch: endpoint, API key, index name (same as original server)
//   - Indexer: optional blob-based seeding configuration
//
// HTTP Endpoints:
//   - GET  /health            – health check
//   - GET  /mcp/tools         – list available MCP tools
//   - POST /mcp/execute       – execute a tool
// 
// Note: This is a simplified HTTP wrapper around the MCP server logic.
// The actual MCP protocol handling happens server-side; the HTTP layer exposes the tools.
// ----------------------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// File logger — diagnostic output
var logPath = Path.Combine(AppContext.BaseDirectory, "mcp-server-web.log");
FileLoggerProvider.Append(logPath, "");
FileLoggerProvider.Append(logPath, $"=== {DateTimeOffset.Now:O} startup (pid={Environment.ProcessId}) ===");
FileLoggerProvider.Append(logPath, $"baseDir = {AppContext.BaseDirectory}");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
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

builder.Services.AddHealthChecks();
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

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

var app = builder.Build();

app.UseCors();

// Health check endpoint
app.MapHealthChecks("/health");

// MCP tools listing endpoint
app.MapGet("/mcp/tools", (KnowledgeBaseSearchService? searchService) =>
{
    // List available tools by returning tool metadata
    // This matches the tools that would be discovered from the MCP server
    var tools = new List<object>
    {
        new
        {
            Name = "SearchKnowledgeBase",
            Description = "Search the knowledge base for relevant information using semantic search"
        },
        new
        {
            Name = "ListTopics",
            Description = "List available knowledge-base topic facets"
        },
        new
        {
            Name = "ExtractTransferRequest",
            Description = "Extract a money-transfer request (EN/HE) from text or a PDF/image blob URI; returns strict JSON with per-field confidence."
        },
        new
        {
            Name = "OcrDocument",
            Description = "Generic OCR for an inline base64 PDF/image; returns raw text + page count."
        },
        new
        {
            Name = "ResolveBank",
            Description = "Resolve a bank name (EN/HE alias) to its canonical id; defaults to Discount when unknown/empty."
        },
        new
        {
            Name = "ValidateAccount",
            Description = "Validate an Israeli account number ('bb-bbb-aaaaaaa') for the given bank."
        },
        new
        {
            Name = "IngestMortgageBundle",
            Description = "Register an incoming mortgage document bundle. Pass applicationId and a JSON array of {filename, summary}. Returns {bundleId, applicationId, docs:[{documentId, filename}]}."
        },
        new
        {
            Name = "ComputeRequiredDocuments",
            Description = "Compute required documents for a mortgage application based on customer profile and loan params. Returns a JSON array of {docType, mandatory, reason}."
        },
        new
        {
            Name = "ClassifyDocument",
            Description = "Classify each submitted document by its actual type. Pass bundleId and JSON array of {documentId, filename, summary}. Returns array of {documentId, filename, declaredType, detectedType, match, confidence}."
        },
        new
        {
            Name = "AuthenticateDocument",
            Description = "Run authenticity checks (tamper, signature, issuer) per document. Returns array of {documentId, filename, tamperScore, signatureValid, issuerVerified, anomalies, genuine}."
        },
        new
        {
            Name = "EmitValidationReport",
            Description = "Merge requirements, classifications, authentications into the final per-document validation report. Returns array of {documentType, submitted, validType, genuine, validity, remarks}."
        }
    };
    return Results.Json(new { Tools = tools });
});

// MCP tool execution endpoint
app.MapPost("/mcp/execute", async (HttpContext ctx, KnowledgeBaseSearchService? searchService) =>
{
    var request = await ctx.Request.ReadFromJsonAsync<ExecuteToolRequest>(cancellationToken: ctx.RequestAborted);
    var toolName = request?.ToolName ?? request?.Name;

    if (string.IsNullOrWhiteSpace(toolName))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "toolName is required" }, ctx.RequestAborted);
        return;
    }

    if (searchService is not null)
        KnowledgeBaseTools.SearchService = searchService;

    var requiresKb = toolName.Equals("SearchKnowledgeBase", StringComparison.OrdinalIgnoreCase) ||
                     toolName.Equals("ListTopics",          StringComparison.OrdinalIgnoreCase);

    if (requiresKb && searchService is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsJsonAsync(new { error = "Knowledge base not configured" }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("SearchKnowledgeBase", StringComparison.OrdinalIgnoreCase))
    {
        var query = GetStringArgument(request?.Arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "SearchKnowledgeBase requires a non-empty 'query' argument" }, ctx.RequestAborted);
            return;
        }

        var result = await KnowledgeBaseTools.SearchKnowledgeBase(query, ctx.RequestAborted);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("ListTopics", StringComparison.OrdinalIgnoreCase))
    {
        var result = await KnowledgeBaseTools.ListTopics(ctx.RequestAborted);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("ExtractTransferRequest", StringComparison.OrdinalIgnoreCase))
    {
        var t  = GetStringArgument(request?.Arguments, "text");
        var b  = GetStringArgument(request?.Arguments, "blobUri");
        var b6 = GetStringArgument(request?.Arguments, "base64");
        var result = await MoneyTransferTools.ExtractTransferRequest(t, b, b6, ctx.RequestAborted);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("OcrDocument", StringComparison.OrdinalIgnoreCase))
    {
        var b64 = GetStringArgument(request?.Arguments, "base64") ?? string.Empty;
        var result = await MoneyTransferTools.OcrDocument(b64, ctx.RequestAborted);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("ResolveBank", StringComparison.OrdinalIgnoreCase))
    {
        var q = GetStringArgument(request?.Arguments, "query");
        var result = MoneyTransferTools.ResolveBank(q);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("ValidateAccount", StringComparison.OrdinalIgnoreCase))
    {
        var account = GetStringArgument(request?.Arguments, "account") ?? string.Empty;
        var bank    = GetStringArgument(request?.Arguments, "bank")    ?? string.Empty;
        var result  = MoneyTransferTools.ValidateAccount(account, bank);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("IngestMortgageBundle", StringComparison.OrdinalIgnoreCase))
    {
        var appId = GetStringArgument(request?.Arguments, "applicationId") ?? string.Empty;
        var docs  = GetStringArgument(request?.Arguments, "documentsJson") ?? "[]";
        var result = MortgageDocumentTools.IngestMortgageBundle(appId, docs);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("ComputeRequiredDocuments", StringComparison.OrdinalIgnoreCase))
    {
        var appId = GetStringArgument(request?.Arguments, "applicationId") ?? string.Empty;
        var mv    = GetDecimalArgument(request?.Arguments, "mortgageValue") ?? 0m;
        var prof  = GetStringArgument(request?.Arguments, "profession") ?? "salaried";
        var band  = GetStringArgument(request?.Arguments, "incomeBand") ?? "mid";
        var ptype = GetStringArgument(request?.Arguments, "propertyType") ?? "apartment";
        var ftb   = GetBoolArgument(request?.Arguments, "firstTimeBuyer") ?? false;
        var result = MortgageDocumentTools.ComputeRequiredDocuments(appId, mv, prof, band, ptype, ftb);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("ClassifyDocument", StringComparison.OrdinalIgnoreCase))
    {
        var bid  = GetStringArgument(request?.Arguments, "bundleId") ?? string.Empty;
        var docs = GetStringArgument(request?.Arguments, "documentsJson") ?? "[]";
        var result = MortgageDocumentTools.ClassifyDocument(bid, docs);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("AuthenticateDocument", StringComparison.OrdinalIgnoreCase))
    {
        var bid  = GetStringArgument(request?.Arguments, "bundleId") ?? string.Empty;
        var docs = GetStringArgument(request?.Arguments, "documentsJson") ?? "[]";
        var result = MortgageDocumentTools.AuthenticateDocument(bid, docs);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    if (toolName.Equals("EmitValidationReport", StringComparison.OrdinalIgnoreCase))
    {
        var appId = GetStringArgument(request?.Arguments, "applicationId") ?? string.Empty;
        var rq = GetStringArgument(request?.Arguments, "requirementsJson") ?? "{}";
        var cl = GetStringArgument(request?.Arguments, "classificationsJson") ?? "{}";
        var au = GetStringArgument(request?.Arguments, "authenticationsJson") ?? "{}";
        var result = MortgageDocumentTools.EmitValidationReport(appId, rq, cl, au);
        await ctx.Response.WriteAsJsonAsync(new { result }, ctx.RequestAborted);
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    await ctx.Response.WriteAsJsonAsync(new { error = $"Unknown tool '{toolName}'" }, ctx.RequestAborted);
});

app.Run();

static string? GetStringArgument(JsonElement? args, string key)
{
    if (!args.HasValue)
        return null;

    var element = args.Value;

    if (element.ValueKind is JsonValueKind.String)
        return element.GetString();

    if (element.ValueKind is not JsonValueKind.Object)
        return null;

    foreach (var prop in element.EnumerateObject())
    {
        if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            continue;

        return prop.Value.ValueKind switch
        {
            JsonValueKind.String => prop.Value.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => prop.Value.GetRawText(),
        };
    }

    return null;
}

static decimal? GetDecimalArgument(JsonElement? args, string key)
{
    if (!args.HasValue || args.Value.ValueKind is not JsonValueKind.Object)
        return null;
    foreach (var prop in args.Value.EnumerateObject())
    {
        if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            continue;
        return prop.Value.ValueKind switch
        {
            JsonValueKind.Number => prop.Value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(prop.Value.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => (decimal?)null,
        };
    }
    return null;
}

static bool? GetBoolArgument(JsonElement? args, string key)
{
    if (!args.HasValue || args.Value.ValueKind is not JsonValueKind.Object)
        return null;
    foreach (var prop in args.Value.EnumerateObject())
    {
        if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            continue;
        return prop.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(prop.Value.GetString(), out var b) => b,
            _ => (bool?)null,
        };
    }
    return null;
}

internal sealed class ExecuteToolRequest
{
    public string? ToolName { get; init; }
    public string? Name { get; init; }
    public JsonElement? Arguments { get; init; }
}
