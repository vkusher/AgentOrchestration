using System.Text.RegularExpressions;
using AgentHandoff.Engine.Orchestration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine.Guardrails;

/// <summary>
/// Pre/post execution guardrails wired in via the Microsoft Agent Framework agent middleware.
///
/// Input pipeline (each step short-circuits and blocks on hit):
///   1. Local prompt-injection markers (cheap, pre-flight, before any external call)
///   2. <see cref="ContentSafetyAnalyzer"/> — Azure AI Content Safety (Hate/Self-Harm/Sexual/Violence)
///      If not configured, falls back to a small local blocklist for the demo.
///   3. PII regex redaction (credit cards, SSNs) — always runs, mutates the input rather than blocking.
///
/// Output pipeline:
///   - Length cap (truncates very long responses).
///
/// Inspired by https://learn.microsoft.com/agent-framework/agents/middleware/termination?pivots=programming-language-csharp
/// Type names match Microsoft.Agents.AI 1.0.0-preview.251002.1 (AgentThread / AgentRunResponse / AgentRunResponseUpdate).
/// </summary>
public sealed class GuardrailMiddleware
{
    private readonly ILogger<GuardrailMiddleware>? _log;
    private readonly Action<AgentEvent>? _onEvent;
    private readonly ContentSafetyAnalyzer? _safety;

    /// <summary>Used only when Content Safety is not configured.</summary>
    private static readonly string[] FallbackBlockedTerms =
    {
        "password", "social security", "ssn", "credit card", "bank account number",
    };

    private static readonly Regex CreditCardRegex = new(@"\b(?:\d[ -]*?){13,16}\b", RegexOptions.Compiled);
    private static readonly Regex SsnRegex        = new(@"\b\d{3}-\d{2}-\d{4}\b",   RegexOptions.Compiled);

    private static readonly string[] InjectionMarkers =
    {
        "ignore previous instructions",
        "disregard the system prompt",
        "you are now",
        "developer override",
    };

    private const int MaxOutputChars = 4000;

    public GuardrailMiddleware(
        ILogger<GuardrailMiddleware>? log = null,
        Action<AgentEvent>? onEvent = null,
        ContentSafetyAnalyzer? safety = null)
    {
        _log = log;
        _onEvent = onEvent;
        _safety = safety;
    }

    /// <summary>
    /// Non-streaming run-middleware delegate.
    /// </summary>
    public Func<IEnumerable<ChatMessage>, AgentThread?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentRunResponse>> Run =>
        async (messages, thread, options, innerAgent, ct) =>
        {
            var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            var agentId  = innerAgent.Name ?? innerAgent.Id ?? "unknown";

            var verdict = await InspectInputAsync(lastUser, ct).ConfigureAwait(false);
            if (verdict.IsBlocked)
            {
                _log?.LogWarning("[Guardrail] Blocked input for {Agent}: {Reason}", agentId, verdict.Reason);
                Emit(new GuardrailEvent(agentId, "input", "blocked", verdict.Reason, DateTimeOffset.UtcNow));
                return new AgentRunResponse(new ChatMessage(
                    ChatRole.Assistant,
                    $"I cannot help with that: {verdict.Reason}. Please rephrase without sensitive information."));
            }

            if (verdict.Sanitised is { Length: > 0 })
            {
                Emit(new GuardrailEvent(agentId, "input", "redacted", verdict.Reason, DateTimeOffset.UtcNow));
                messages = ReplaceLastUserMessage(messages, verdict.Sanitised);
            }
            else
            {
                Emit(new GuardrailEvent(agentId, "input", "passed", verdict.Reason, DateTimeOffset.UtcNow));
            }

            var response = await innerAgent.RunAsync(messages, thread, options, ct).ConfigureAwait(false);

            var responseText = response.Text ?? string.Empty;
            if (responseText.Length > MaxOutputChars)
            {
                _log?.LogInformation("[Guardrail] Truncating response from {Agent}: {Length} chars", agentId, responseText.Length);
                Emit(new GuardrailEvent(agentId, "output", "truncated", $"len={responseText.Length}", DateTimeOffset.UtcNow));
                var truncated = responseText[..MaxOutputChars] + "… [truncated]";
                return new AgentRunResponse(new ChatMessage(ChatRole.Assistant, truncated));
            }

            return response;
        };

    /// <summary>
    /// Streaming run-middleware delegate. Same semantics as <see cref="Run"/>.
    /// </summary>
    public Func<IEnumerable<ChatMessage>, AgentThread?, AgentRunOptions?, AIAgent, CancellationToken, IAsyncEnumerable<AgentRunResponseUpdate>> RunStreaming =>
        (messages, thread, options, innerAgent, ct) => RunStreamingCore(messages, thread, options, innerAgent, ct);

    private async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingCore(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var agentId  = innerAgent.Name ?? innerAgent.Id ?? "unknown";

        var verdict = await InspectInputAsync(lastUser, ct).ConfigureAwait(false);
        if (verdict.IsBlocked)
        {
            _log?.LogWarning("[Guardrail] Blocked input for {Agent}: {Reason}", agentId, verdict.Reason);
            Emit(new GuardrailEvent(agentId, "input", "blocked", verdict.Reason, DateTimeOffset.UtcNow));

            var blockText = $"I cannot help with that: {verdict.Reason}. Please rephrase without sensitive information.";
            yield return new AgentRunResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = new List<AIContent> { new TextContent(blockText) },
            };
            yield break;
        }

        if (verdict.Sanitised is { Length: > 0 })
        {
            Emit(new GuardrailEvent(agentId, "input", "redacted", verdict.Reason, DateTimeOffset.UtcNow));
            messages = ReplaceLastUserMessage(messages, verdict.Sanitised);
        }
        else
        {
            Emit(new GuardrailEvent(agentId, "input", "passed", verdict.Reason, DateTimeOffset.UtcNow));
        }

        var totalLen = 0;
        await foreach (var update in innerAgent.RunStreamingAsync(messages, thread, options, ct).ConfigureAwait(false))
        {
            totalLen += update.Text?.Length ?? 0;
            yield return update;
        }

        if (totalLen > MaxOutputChars)
        {
            _log?.LogInformation("[Guardrail] Long response from {Agent}: {Length} chars", agentId, totalLen);
            Emit(new GuardrailEvent(agentId, "output", "truncated", $"len={totalLen}", DateTimeOffset.UtcNow));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Input inspection — combines local detectors with Azure Content Safety
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<InputVerdict> InspectInputAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return InputVerdict.Passed("empty");

        var lower = text.ToLowerInvariant();

        // 1. Local prompt-injection markers (Azure has separate Prompt Shields for this — not yet wired)
        foreach (var marker in InjectionMarkers)
        {
            if (lower.Contains(marker))
                return InputVerdict.Blocked($"prompt-injection attempt detected ('{marker}')");
        }

        // 2. Azure Content Safety (preferred) or local blocklist (fallback)
        string passReason = "ok";
        if (_safety is not null)
        {
            var v = await _safety.AnalyzeAsync(text, ct).ConfigureAwait(false);
            if (v.IsBlocked)
                return InputVerdict.Blocked(v.Reason);

            passReason = v.Outcome switch
            {
                SafetyOutcome.Passed  => "Content Safety: clean",
                SafetyOutcome.Errored => $"Content Safety errored — fail-open ({v.Reason})",
                SafetyOutcome.Skipped => "Content Safety: skipped",
                _ => "ok",
            };
        }
        else
        {
            foreach (var blocked in FallbackBlockedTerms)
            {
                if (lower.Contains(blocked))
                    return InputVerdict.Blocked($"input mentions disallowed term '{blocked}' (local blocklist)");
            }
            passReason = "local blocklist: clean";
        }

        // 3. PII redaction (always)
        var sanitised = text;
        var redacted = false;
        if (CreditCardRegex.IsMatch(sanitised))
        {
            sanitised = CreditCardRegex.Replace(sanitised, "[REDACTED-CC]");
            redacted = true;
        }
        if (SsnRegex.IsMatch(sanitised))
        {
            sanitised = SsnRegex.Replace(sanitised, "[REDACTED-SSN]");
            redacted = true;
        }

        return redacted
            ? InputVerdict.Redacted("PII redacted before reaching the model", sanitised)
            : InputVerdict.Passed(passReason);
    }

    private static IEnumerable<ChatMessage> ReplaceLastUserMessage(IEnumerable<ChatMessage> messages, string newText)
    {
        var list = messages.ToList();
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Role == ChatRole.User)
            {
                list[i] = new ChatMessage(ChatRole.User, newText);
                break;
            }
        }
        return list;
    }

    private void Emit(AgentEvent evt)
    {
        _onEvent?.Invoke(evt);
        TurnEventBus.Publish(evt);
    }

    private readonly record struct InputVerdict(bool IsBlocked, string Reason, string? Sanitised)
    {
        public static InputVerdict Passed(string reason)               => new(false, reason, null);
        public static InputVerdict Blocked(string reason)              => new(true,  reason, null);
        public static InputVerdict Redacted(string reason, string txt) => new(false, reason, txt);
    }
}
