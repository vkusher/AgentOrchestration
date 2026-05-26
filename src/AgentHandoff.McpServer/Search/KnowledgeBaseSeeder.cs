using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.McpServer.Search;

/// <summary>
/// Idempotent index bootstrap for the knowledge base.
///
/// Two-step API:
///   <see cref="EnsureIndexAsync"/>  — schema only. Always run.
///                                     Returns true if the index had to be dropped and recreated
///                                     (e.g. analyzer change), so callers can reset the indexer
///                                     in pull mode to clear its high-water mark.
///   <see cref="SeedAsync"/>          — push-model: upload <see cref="DefaultArticles.All"/> directly.
///                                     Skip when using the pull-model pipeline.
/// </summary>
public sealed class KnowledgeBaseSeeder
{
    private readonly SearchIndexClient _indexClient;
    private readonly string _indexName;
    private readonly ILogger<KnowledgeBaseSeeder>? _log;

    public KnowledgeBaseSeeder(SearchIndexClient indexClient, string indexName, ILogger<KnowledgeBaseSeeder>? log = null)
    {
        _indexClient = indexClient;
        _indexName   = indexName;
        _log         = log;
    }

    /// <summary>Convenience: ensure index + push-seed in one call (push-model only).</summary>
    public async Task<bool> EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        var recreated = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        await SeedAsync(cancellationToken).ConfigureAwait(false);
        return recreated;
    }

    /// <returns>True if the index was dropped and recreated (schema-incompatible change).</returns>
    public async Task<bool> EnsureIndexAsync(CancellationToken cancellationToken = default)
    {
        var fieldBuilder = new FieldBuilder();
        var fields = fieldBuilder.Build(typeof(KnowledgeBaseDocument));

        _log?.LogInformation("KB index '{Index}' schema fields: {Fields}",
            _indexName, string.Join(", ",
                fields.Select(f => $"{f.Name}({f.Type}{(f.AnalyzerName is null ? "" : $",analyzer={f.AnalyzerName}")})")));

        var index = new SearchIndex(_indexName, fields);

        try
        {
            await _indexClient.CreateOrUpdateIndexAsync(
                index, allowIndexDowntime: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            _log?.LogInformation("KB index '{Index}' is ready (CreateOrUpdate succeeded).", _indexName);
            return false;
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            _log?.LogError(ex,
                "Cannot create/update KB index '{Index}' — 403 Forbidden. The configured " +
                "AzureSearch:ApiKey appears to be a QUERY key (read-only). Use an ADMIN key.",
                _indexName);
            throw;
        }
        catch (RequestFailedException ex) when (
            ex.Status == 400 &&
            (ex.Message.Contains("analyzer", StringComparison.OrdinalIgnoreCase)
              || ex.Message.Contains("incompatible", StringComparison.OrdinalIgnoreCase)
              || ex.Message.Contains("retryableFields", StringComparison.OrdinalIgnoreCase)))
        {
            // Azure rejects analyzer changes on existing fields — must drop and recreate.
            _log?.LogWarning(
                "CreateOrUpdate rejected (analyzer/schema change). Dropping and recreating index '{Index}'. " +
                "Reason: {Msg}", _indexName, ex.Message);

            await _indexClient.DeleteIndexAsync(_indexName, cancellationToken).ConfigureAwait(false);
            await _indexClient.CreateIndexAsync(index, cancellationToken).ConfigureAwait(false);

            _log?.LogInformation("KB index '{Index}' recreated.", _indexName);
            return true;
        }
        catch (RequestFailedException ex)
        {
            _log?.LogError(ex, "EnsureIndexAsync failed for '{Index}' (status={Status})",
                _indexName, ex.Status);
            throw;
        }
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var searchClient = _indexClient.GetSearchClient(_indexName);
        var existingCount = await CountDocumentsAsync(searchClient, cancellationToken).ConfigureAwait(false);

        if (existingCount >= DefaultArticles.All.Length)
        {
            _log?.LogInformation("KB index '{Index}' already has {Count} doc(s); push-seed skipped.",
                _indexName, existingCount);
            return;
        }

        _log?.LogInformation("KB index '{Index}' has {Existing} doc(s); pushing {Count} default article(s).",
            _indexName, existingCount, DefaultArticles.All.Length);

        var batch = IndexDocumentsBatch.Upload(DefaultArticles.All);
        try
        {
            var response = await searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken).ConfigureAwait(false);
            var failed = 0;
            foreach (var r in response.Value.Results)
            {
                if (!r.Succeeded)
                {
                    failed++;
                    _log?.LogError("Doc {Key} failed to index: status={Status} message={Msg}",
                        r.Key, r.Status, r.ErrorMessage);
                }
            }
            _log?.LogInformation("Push upload finished: {OK} succeeded, {Failed} failed.",
                response.Value.Results.Count - failed, failed);
        }
        catch (RequestFailedException ex)
        {
            _log?.LogError(ex,
                "Upload to KB index '{Index}' failed (status={Status}). " +
                "Check that the API key has WRITE permission (admin key, not query key).",
                _indexName, ex.Status);
            throw;
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        var afterCount = await CountDocumentsAsync(searchClient, cancellationToken).ConfigureAwait(false);
        _log?.LogInformation("KB index '{Index}' has {Count} doc(s) after push-seed.", _indexName, afterCount);
    }

    private async Task<long> CountDocumentsAsync(SearchClient client, CancellationToken ct)
    {
        try
        {
            var options = new SearchOptions { Size = 0, IncludeTotalCount = true };
            var resp = await client.SearchAsync<KnowledgeBaseDocument>("*", options, ct).ConfigureAwait(false);
            return resp.Value.TotalCount ?? 0;
        }
        catch (RequestFailedException ex)
        {
            _log?.LogWarning("Count query failed (status={Status}); assuming empty.", ex.Status);
            return 0;
        }
    }
}
