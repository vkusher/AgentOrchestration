using Azure;
using Azure.AI.ContentSafety;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine.Guardrails;

/// <summary>
/// Outcome of a single Content Safety check.
/// </summary>
public enum SafetyOutcome
{
    Skipped,
    Passed,
    Blocked,
    Errored,
}

public sealed record ContentSafetyVerdict(SafetyOutcome Outcome, string Reason)
{
    public static readonly ContentSafetyVerdict Skipped = new(SafetyOutcome.Skipped, "no text to analyze");
    public static readonly ContentSafetyVerdict Passed  = new(SafetyOutcome.Passed,  "ok");

    public static ContentSafetyVerdict Blocked(string reason) => new(SafetyOutcome.Blocked, reason);
    public static ContentSafetyVerdict Errored(string reason) => new(SafetyOutcome.Errored, reason);

    public bool IsBlocked => Outcome == SafetyOutcome.Blocked;
}

/// <summary>
/// Wraps <see cref="ContentSafetyClient"/> for use as a guardrail. Analyses text across
/// the four standard harm categories (Hate, Self-Harm, Sexual, Violence) and blocks if
/// any category meets or exceeds the configured severity threshold.
///
/// Severity is an int returned by the service (0/2/4/6 in the 4-level model the public
/// portal exposes; integer otherwise). A threshold of 2 = "block anything flagged".
///
/// Reference: https://learn.microsoft.com/azure/ai-services/content-safety/quickstart-text
/// </summary>
public sealed class ContentSafetyAnalyzer
{
    private readonly ContentSafetyClient _client;
    private readonly int _blockSeverity;
    private readonly ILogger<ContentSafetyAnalyzer>? _log;

    private ContentSafetyAnalyzer(ContentSafetyClient client, int blockSeverity, ILogger<ContentSafetyAnalyzer>? log)
    {
        _client = client;
        _blockSeverity = blockSeverity;
        _log = log;
    }

    /// <summary>
    /// Returns null when not configured (so the GuardrailMiddleware falls back to the local blocklist).
    /// </summary>
    public static ContentSafetyAnalyzer? TryCreate(
        string? endpoint,
        string? apiKey,
        int blockSeverity,
        ILoggerFactory? loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            return null;

        var client = new ContentSafetyClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        var log = loggerFactory?.CreateLogger<ContentSafetyAnalyzer>();
        log?.LogInformation("Azure Content Safety enabled at {Endpoint} (severity ≥ {Threshold})", endpoint, blockSeverity);
        return new ContentSafetyAnalyzer(client, blockSeverity, log);
    }

    public async Task<ContentSafetyVerdict> AnalyzeAsync(string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ContentSafetyVerdict.Skipped;

        // Service hard-limits text to 10k characters per call.
        var snippet = text.Length > 10_000 ? text[..10_000] : text;

        try
        {
            var options = new AnalyzeTextOptions(snippet);
            // Default behaviour analyses Hate, SelfHarm, Sexual, Violence.

            Response<AnalyzeTextResult> response =
                await _client.AnalyzeTextAsync(options, ct).ConfigureAwait(false);

            var triggered = response.Value.CategoriesAnalysis
                .Where(c => (c.Severity ?? 0) >= _blockSeverity)
                .Select(c => $"{c.Category}={c.Severity ?? 0}")
                .ToArray();

            if (triggered.Length == 0)
                return ContentSafetyVerdict.Passed;

            var reason = $"Azure Content Safety blocked: {string.Join(", ", triggered)}";
            _log?.LogWarning("Content Safety BLOCK — {Reason}", reason);
            return ContentSafetyVerdict.Blocked(reason);
        }
        catch (Exception ex)
        {
            // Fail open (allow) — production might prefer fail-closed depending on threat model.
            _log?.LogError(ex, "Content Safety call failed; allowing request through.");
            return ContentSafetyVerdict.Errored(ex.Message);
        }
    }
}
