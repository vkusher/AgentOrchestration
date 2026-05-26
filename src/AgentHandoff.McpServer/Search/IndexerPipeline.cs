using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.McpServer.Search;

/// <summary>
/// Configures the pull-model pipeline in Azure AI Search:
///   Blob container → Data Source → Indexer → Index
/// Each blob (one JSON article) is parsed by the indexer using parsingMode=json
/// and lands as a single document in <c>indexName</c>.
/// </summary>
public sealed class IndexerPipeline
{
    private readonly SearchIndexerClient _client;
    private readonly ILogger<IndexerPipeline>? _log;

    public IndexerPipeline(Uri searchEndpoint, AzureKeyCredential credential, ILogger<IndexerPipeline>? log = null)
    {
        _client = new SearchIndexerClient(searchEndpoint, credential);
        _log = log;
    }

    /// <summary>
    /// Clears the indexer's high-watermark so that the next run re-indexes everything.
    /// Call this after the target index has been dropped+recreated, otherwise the indexer
    /// still thinks the (now-deleted) blobs are already indexed and skips them.
    /// </summary>
    public async Task ResetIndexerAsync(string indexerName, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.ResetIndexerAsync(indexerName, cancellationToken).ConfigureAwait(false);
            _log?.LogInformation("Indexer '{Name}' state reset (high-watermark cleared).", indexerName);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Indexer doesn't exist yet — fine, EnsureAndRunAsync will create it.
        }
        catch (RequestFailedException ex)
        {
            _log?.LogWarning(ex, "ResetIndexerAsync for '{Name}' failed (status={Status})",
                indexerName, ex.Status);
        }
    }

    /// <summary>
    /// Idempotent: creates or updates the data source + indexer, then runs the indexer once.
    /// </summary>
    public async Task EnsureAndRunAsync(
        string blobConnectionString,
        string containerName,
        string dataSourceName,
        string indexerName,
        string indexName,
        CancellationToken cancellationToken = default)
    {
        // 1. Data source — points at the blob container.
        var dataSource = new SearchIndexerDataSourceConnection(
            name:           dataSourceName,
            type:           SearchIndexerDataSourceType.AzureBlob,
            connectionString: blobConnectionString,
            container:      new SearchIndexerDataContainer(containerName))
        {
            Description = "JSON articles for the bank-support knowledge base.",
        };

        try
        {
            await _client.CreateOrUpdateDataSourceConnectionAsync(dataSource, cancellationToken: cancellationToken).ConfigureAwait(false);
            _log?.LogInformation("Data source '{Name}' ready (container='{Container}').",
                dataSourceName, containerName);
        }
        catch (RequestFailedException ex)
        {
            _log?.LogError(ex,
                "Create/update data source '{Name}' failed (status={Status}). " +
                "Check that the search admin key has permission and the blob connection string is valid.",
                dataSourceName, ex.Status);
            throw;
        }

        // 2. Indexer — pulls JSON files from the data source into the index.
        //    parsingMode=json: each blob is a single JSON object, fields map by name.
        //    Our index has 'id' as IsKey and the JSON has 'id' → no field mapping needed.
        var indexer = new SearchIndexer(
            name:           indexerName,
            dataSourceName: dataSourceName,
            targetIndexName: indexName)
        {
            Description = "Pulls JSON articles from blob storage into the bank KB index.",
            Parameters  = new IndexingParameters
            {
                Configuration =
                {
                    ["parsingMode"]                  = "json",
                    ["failOnUnsupportedContentType"] = false,
                    ["failOnUnprocessableDocument"]  = false,
                },
            },
            // No schedule — we run on-demand from the McpServer startup.
        };

        try
        {
            await _client.CreateOrUpdateIndexerAsync(indexer, cancellationToken: cancellationToken).ConfigureAwait(false);
            _log?.LogInformation("Indexer '{Name}' ready (target='{Index}').", indexerName, indexName);
        }
        catch (RequestFailedException ex)
        {
            _log?.LogError(ex,
                "Create/update indexer '{Name}' failed (status={Status}).", indexerName, ex.Status);
            throw;
        }

        // 3. Run the indexer once. Subsequent startups also run it (idempotent — only changed
        //    blobs are re-indexed thanks to Azure's high-water-mark change tracking).
        try
        {
            await _client.RunIndexerAsync(indexerName, cancellationToken).ConfigureAwait(false);
            _log?.LogInformation("Indexer '{Name}' run requested. Status will appear in the Azure portal " +
                "→ Search service → Indexers → {Name} → Execution history.", indexerName, indexerName);

            // Wait a moment, then peek at the latest status for the log.
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            try
            {
                var status = await _client.GetIndexerStatusAsync(indexerName, cancellationToken).ConfigureAwait(false);
                var last = status.Value.LastResult;
                if (last is not null)
                {
                    _log?.LogInformation(
                        "Indexer '{Name}' lastResult: status={Status}, succeeded={Items}, failed={Failed}.",
                        indexerName, last.Status, last.ItemCount, last.FailedItemCount);
                    foreach (var err in last.Errors)
                    {
                        _log?.LogError("Indexer error: {Key} — {Msg}", err.Key, err.ErrorMessage);
                    }
                }
            }
            catch (RequestFailedException) { /* status not yet available — fine */ }
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _log?.LogInformation("Indexer '{Name}' is already running — skipping duplicate run.", indexerName);
        }
        catch (RequestFailedException ex)
        {
            _log?.LogError(ex, "Run indexer '{Name}' failed (status={Status}).", indexerName, ex.Status);
            throw;
        }
    }
}
