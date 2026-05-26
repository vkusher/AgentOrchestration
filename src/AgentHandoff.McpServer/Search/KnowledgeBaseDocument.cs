using System.Text.Json.Serialization;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace AgentHandoff.McpServer.Search;

/// <summary>
/// Schema of a knowledge-base article in the Azure AI Search index.
///
/// Why the explicit JSON property names: the Azure.Search.Documents SDK has changed its
/// default JSON naming policy across versions. Without these, FieldBuilder may create the
/// index with PascalCase field names while document upload serializes camelCase — every
/// doc lands with empty fields and full-text search returns nothing.
///
/// Why the Hebrew Microsoft analyzer on Title/Content: the default standard.lucene analyzer
/// is Unicode-aware and tokenises Hebrew text by whitespace, so EXACT-word matching works
/// — but it doesn't apply Hebrew stemming or root extraction. Switching to he.microsoft on
/// the long-text fields gives proper Hebrew lemmatisation while still tokenising English
/// reasonably (so 'downtown branch hours' still matches the English article). The Topic
/// field stays on the default analyzer because its values are English keywords used by the
/// LLM for filtering.
/// </summary>
public sealed class KnowledgeBaseDocument
{
    [SimpleField(IsKey = true, IsFilterable = true)]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [SearchableField(IsFilterable = true, IsFacetable = true)]
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    //[SearchableField(AnalyzerName = "he.microsoft")]
    [SearchableField()]
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    //[SearchableField(AnalyzerName = "he.microsoft")]
    [SearchableField()]
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
