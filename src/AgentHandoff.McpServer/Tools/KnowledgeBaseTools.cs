using System.ComponentModel;
using System.Text;
using AgentHandoff.McpServer.Search;
using ModelContextProtocol.Server;

namespace AgentHandoff.McpServer.Tools;

/// <summary>
/// Knowledge-base lookups exposed to MCP clients. Backed by Azure AI Search when configured;
/// falls back to a small inline dictionary so the demo still works headless.
/// </summary>
[McpServerToolType]
public static class KnowledgeBaseTools
{
    /// <summary>Set by Program.cs at startup if Azure Search is configured.</summary>
    public static KnowledgeBaseSearchService? SearchService { get; set; }

    [McpServerTool, Description(
        "Retrieve articles from the bank's customer-support knowledge base using full-text search " +
        "(Azure AI Search). Pass the customer's question as the query. Returns the top matching " +
        "articles, each with a topic, title and body. " +
        "Topics seeded in the index include: branches (opening hours, locations, holiday schedule), " +
        "terms (APR/APY, FDIC, IBAN/SWIFT/routing, overdraft fees), " +
        "transfers (wire, ACH timing and fees), digital (online banking, mobile check deposit).")]
    public static async Task<string> SearchKnowledgeBase(
        [Description("Free-text query — the customer's question (e.g. 'downtown branch hours', 'what's APY', 'wire fee international').")] string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Provide a non-empty query (the customer's symptom or error message).";

        if (SearchService is not null)
        {
            var hits = await SearchService.SearchAsync(query, top: null, cancellationToken).ConfigureAwait(false);

            if (hits.Count == 0)
                return $"No KB articles matched '{query}'. Suggest the customer rephrase or escalate.";

            var sb = new StringBuilder();
            sb.AppendLine($"Top {hits.Count} KB article(s) for '{query}':");
            sb.AppendLine();

            for (var i = 0; i < hits.Count; i++)
            {
                var d = hits[i];
                sb.AppendLine($"[{i + 1}] [{d.Topic}] {d.Title}");
                sb.AppendLine(d.Content);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        // Fallback when Azure Search isn't configured — keep the demo runnable.
        return InlineFallback.Search(query);
    }

    [McpServerTool, Description("List the topic facets currently in the knowledge base.")]
    public static async Task<string> ListTopics(CancellationToken cancellationToken = default)
    {
        if (SearchService is not null)
        {
            // Cheap proxy: search for everything, dedupe topics.
            var hits = await SearchService.SearchAsync("*", top: 50, cancellationToken).ConfigureAwait(false);
            var topics = hits.Select(h => h.Topic)
                             .Where(t => !string.IsNullOrWhiteSpace(t))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                             .ToArray();

            return topics.Length == 0
                ? "Index is empty."
                : "Available topics: " + string.Join(", ", topics);
        }

        return "Available topics: " + string.Join(", ", InlineFallback.Topics);
    }

    /// <summary>Last-resort static knowledge base used only when Azure Search is unconfigured.</summary>
    private static class InlineFallback
    {
        private static readonly Dictionary<string, string> _kb = new(StringComparer.OrdinalIgnoreCase)
        {
            ["downtown"]  = "Downtown branch (123 Main St, Suite 100): Mon-Fri 9:00-17:00, Sat 10:00-14:00, closed Sunday.",
            ["westside"]  = "Westside branch (88 Park Ave): Mon-Fri 8:30-17:30, Sat 9:00-13:00, closed Sunday.",
            ["airport"]   = "Airport kiosk (Concourse C, T2): teller Mon-Sun 6:00-22:00, ATM 24/7.",
            ["holiday"]   = "All branches close on US federal banking holidays. Online banking & ATMs run 24/7.",
            ["apr"]       = "APR is the simple yearly cost of borrowing (no compounding); APY includes compounding. APY ≥ APR for the same nominal rate.",
            ["apy"]       = "APY = compounded yearly yield. Used on savings & CDs.",
            ["fdic"]      = "FDIC insures deposits up to $250,000 per depositor per ownership category.",
            ["iban"]      = "IBAN required for transfers to Europe/ME/parts of Asia. US has no IBAN — use routing + account.",
            ["swift"]     = "SWIFT/BIC: 8- or 11-char bank identifier required for international wires.",
            ["overdraft"] = "Overdraft fee $35 per item, capped at 4/day. Opt-out in online banking → overdraft preferences.",
            ["wire"]      = "Domestic wire $25, posts same business day if before 16:30 ET. International wire $45, 1-3 business days.",
            ["ach"]       = "Standard ACH 1-2 business days. Same-day ACH cutoff 13:45 ET. Daily limit $25,000.",
            ["mobile"]    = "Mobile check deposit limits: $5,000 per check, $10,000 per day. First $225 available immediately.",
            ["online"]    = "Enroll at online.example-bank.com with account number + SSN. Password reset via SMS/email OTP.",
        };

        public static IEnumerable<string> Topics => _kb.Keys;

        public static string Search(string query)
        {
            var match = _kb.Keys.FirstOrDefault(k => query.Contains(k, StringComparison.OrdinalIgnoreCase));
            return match is null
                ? $"No KB article found for '{query}'. Available topics: {string.Join(", ", _kb.Keys)}."
                : $"KB:{match}\n{_kb[match]}";
        }
    }
}
