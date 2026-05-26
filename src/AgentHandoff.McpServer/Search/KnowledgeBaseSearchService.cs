using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.McpServer.Search;

/// <summary>
/// Wraps <see cref="SearchClient"/> with the small surface our MCP tool needs.
/// Logs every query and its hit count so failures are visible in the MCP server's stderr.
/// </summary>
public sealed class KnowledgeBaseSearchService
{
    private readonly SearchClient _client;
    private readonly int _defaultTopK;
    private readonly ILogger<KnowledgeBaseSearchService>? _log;

    public KnowledgeBaseSearchService(SearchClient client, int defaultTopK, ILogger<KnowledgeBaseSearchService>? log = null)
    {
        _client = client;
        _defaultTopK = defaultTopK;
        _log = log;
    }

    public async Task<IReadOnlyList<KnowledgeBaseDocument>> SearchAsync(
        string query,
        int? top = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<KnowledgeBaseDocument>();

        var options = new SearchOptions
        {
            Size = top ?? _defaultTopK,
            QueryType = SearchQueryType.Simple,
            SearchMode = SearchMode.Any,
            IncludeTotalCount = true,
        };

        try
        {
            Response<SearchResults<KnowledgeBaseDocument>> response =
                await _client.SearchAsync<KnowledgeBaseDocument>(query, options, cancellationToken).ConfigureAwait(false);

            var hits = new List<KnowledgeBaseDocument>();
            await foreach (var r in response.Value.GetResultsAsync().ConfigureAwait(false))
            {
                hits.Add(r.Document);
            }

            _log?.LogInformation(
                "KB search '{Query}' → {Hits} hit(s) (total matches={Total})",
                query, hits.Count, response.Value.TotalCount ?? 0);

            return hits;
        }
        catch (RequestFailedException ex)
        {
            _log?.LogError(ex,
                "Azure Search query failed for '{Query}' (status={Status}). " +
                "Common causes: wrong index name, wrong API key permissions, index not yet created.",
                query, ex.Status);
            return Array.Empty<KnowledgeBaseDocument>();
        }
    }
}
