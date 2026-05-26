using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.McpServer.Search;

/// <summary>
/// Uploads each <see cref="DefaultArticles.All"/> article as a JSON blob into a container.
/// One JSON file per article — the Azure Search indexer (parsingMode: json) will pull
/// each blob in as a single index document, mapping JSON keys → index fields by name.
/// Idempotent: skips uploads whose blob already exists.
/// </summary>
public sealed class BlobSeeder
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobSeeder>? _log;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = false,
        // Naming policy doesn't matter — KnowledgeBaseDocument has [JsonPropertyName] on every field.
    };

    public BlobSeeder(string connectionString, string containerName, ILogger<BlobSeeder>? log = null)
    {
        _container = new BlobContainerClient(connectionString, containerName);
        _log = log;
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var b in _container.GetBlobsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            existing.Add(b.Name);
        }

        var uploaded = 0;
        foreach (var doc in DefaultArticles.All)
        {
            var blobName = $"{doc.Id}.json";
            if (existing.Contains(blobName)) continue;

            var json = JsonSerializer.Serialize(doc, s_jsonOpts);
            await _container.UploadBlobAsync(blobName, BinaryData.FromString(json).ToStream(), cancellationToken)
                            .ConfigureAwait(false);
            uploaded++;
        }

        _log?.LogInformation(
            "Blob container '{Container}': {Existing} existing, {Uploaded} newly uploaded ({Total} default articles).",
            _container.Name, existing.Count, uploaded, DefaultArticles.All.Length);
    }
}
