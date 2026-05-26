using System.ComponentModel;
using System.Text.Json;
using AgentHandoff.Engine.Orchestration;

namespace AgentHandoff.Engine.Tools;

/// <summary>
/// Local executor tool for the transfer flow. Gated by the existing approval pipeline:
/// any transfer at or above <see cref="ApprovalThreshold"/> pauses for HITL review and
/// raises an Event Grid 'agenthandoff.approval.requested' event when the broker is configured.
/// </summary>
public sealed class MoneyTransferTools
{
    public const decimal ApprovalThreshold = 1_000m;

    private readonly string _eventAgentId;

    public MoneyTransferTools(string eventAgentId = "transfer_executor")
    {
        _eventAgentId = string.IsNullOrWhiteSpace(eventAgentId) ? "transfer_executor" : eventAgentId;
    }

    [Description(
        "Submit a customer money-transfer for execution. The 'payload' MUST be the JSON object " +
        "returned by ExtractTransferRequest (after validation). Transfers at or above the " +
        "approval threshold (1000 of any currency) require human supervisor approval.")]
    public async Task<string> SubmitTransfer(
        [Description("JSON payload from ExtractTransferRequest, validated by ValidateAccount.")] string payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "Refusing to submit: payload is empty.";

        decimal amount = 0m;
        string currency = "ILS";
        string? fromAcc = null, toAcc = null, toBank = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("amount", out var a))
            {
                if (a.TryGetProperty("value",    out var v) && v.ValueKind is JsonValueKind.Number) amount   = v.GetDecimal();
                if (a.TryGetProperty("currency", out var c) && c.ValueKind is JsonValueKind.String) currency = c.GetString() ?? "ILS";
            }
            fromAcc = root.TryGetProperty("fromAccount", out var f) && f.TryGetProperty("value", out var fv) ? fv.GetString() : null;
            toAcc   = root.TryGetProperty("toAccount",   out var t) && t.TryGetProperty("value", out var tv) ? tv.GetString() : null;
            toBank  = root.TryGetProperty("toBank",      out var b) && b.TryGetProperty("value", out var bv) ? bv.GetString() : null;
        }
        catch (JsonException ex)
        {
            return $"Refusing to submit: payload is not valid JSON ({ex.Message}).";
        }

        if (amount <= 0)        return "Refusing to submit: amount missing or non-positive.";
        if (string.IsNullOrWhiteSpace(fromAcc)) return "Refusing to submit: fromAccount missing.";
        if (string.IsNullOrWhiteSpace(toAcc))   return "Refusing to submit: toAccount missing.";
        if (string.IsNullOrWhiteSpace(toBank))  return "Refusing to submit: toBank missing.";

        TurnEventBus.Publish(new GuardrailEvent(
            AgentId:   _eventAgentId,
            Stage:     "approval-check",
            Verdict:   amount >= ApprovalThreshold ? "required" : "skipped",
            Reason:    $"amount={amount:F2} {currency} threshold={ApprovalThreshold:F2} " +
                       $"gate={(ApprovalGate.Current is null ? "DETACHED" : "attached")}",
            Timestamp: DateTimeOffset.UtcNow));

        if (amount >= ApprovalThreshold)
        {
            var approved = await ApprovalGate.RequestAsync(
                toolName: "SubmitTransfer",
                arguments: new Dictionary<string, object?>
                {
                    ["fromAccount"] = fromAcc,
                    ["toAccount"]   = toAcc,
                    ["toBank"]      = toBank,
                    ["amount"]      = amount,
                    ["currency"]    = currency,
                },
                ct: cancellationToken).ConfigureAwait(false);

            if (!approved)
            {
                return $"Transfer of {amount:F2} {currency} from {fromAcc} to {toAcc} ({toBank}) " +
                       $"was DENIED by the supervisor. No funds moved.";
            }
        }

        var confirmation = $"TRX-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";
        return $"Transfer of {amount:F2} {currency} from {fromAcc} to {toAcc} ({toBank}) submitted. " +
               $"Confirmation: {confirmation}. Funds typically settle within one business day.";
    }
}
