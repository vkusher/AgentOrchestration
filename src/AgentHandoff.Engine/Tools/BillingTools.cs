using System.ComponentModel;
using AgentHandoff.Engine.Orchestration;

namespace AgentHandoff.Engine.Tools;

/// <summary>
/// Tools used by the Fees &amp; Refunds agent. Banking flavour: account balances and
/// fee refunds (refunds at or above ApprovalThreshold are gated by HITL approval).
/// </summary>
public sealed class BillingTools
{
    private readonly string _eventAgentId;

    public BillingTools(string eventAgentId = "billing")
    {
        _eventAgentId = string.IsNullOrWhiteSpace(eventAgentId) ? "billing" : eventAgentId;
    }

    /// <summary>Refunds at or above this amount must be approved by a human supervisor.</summary>
    public const decimal ApprovalThreshold = 50m;

    private static readonly Dictionary<string, (string Type, decimal Available)> _accounts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ACCT-1001"] = ("Savings",    4_250.99m),
            ["ACCT-1002"] = ("Checking",      312.07m),
            ["ACCT-1003"] = ("Certificate", 25_000.00m),
        };

    [Description("Look up the available balance and account type for a given customer account.")]
    public string LookupBalance(
        [Description("The customer account id, e.g. ACCT-1001.")] string accountId)
    {
        if (_accounts.TryGetValue(accountId, out var info))
        {
            return $"Account {accountId} ({info.Type}): available balance ${info.Available:F2}.";
        }
        return $"No account found for id {accountId}.";
    }

    [Description(
        "Issue a refund of a previously charged fee (overdraft, wire, ATM, foreign-exchange) " +
        "back to the customer's account. Returns a confirmation code. " +
        "IMPORTANT: refunds at or above $50 require supervisor approval — the call will pause " +
        "while a human operator reviews it. If denied, the function returns a denial message " +
        "and you should offer the customer an alternative (waive next fee, partial credit).")]
    public async Task<string> IssueRefund(
        [Description("The transaction id of the fee being refunded, e.g. TXN-2026-0099.")] string transactionId,
        [Description("The refund amount in USD.")] decimal amount,
        [Description("Reason for the refund (e.g. 'overdraft fee waiver', 'wire fee dispute', 'goodwill credit').")] string reason,
        CancellationToken cancellationToken = default)
    {
        // ── Diagnostic: confirm the gate is active ────────────────────────────
        TurnEventBus.Publish(new GuardrailEvent(
            AgentId:   _eventAgentId,
            Stage:     "approval-check",
            Verdict:   amount >= ApprovalThreshold ? "required" : "skipped",
            Reason:    $"amount=${amount:F2} threshold=${ApprovalThreshold:F2} " +
                       $"gate={(ApprovalGate.Current is null ? "DETACHED" : "attached")}",
            Timestamp: DateTimeOffset.UtcNow));

        if (amount >= ApprovalThreshold)
        {
            var approved = await ApprovalGate.RequestAsync(
                toolName: "IssueRefund",
                arguments: new Dictionary<string, object?>
                {
                    ["transactionId"] = transactionId,
                    ["amount"]        = amount,
                    ["reason"]        = reason,
                },
                ct: cancellationToken).ConfigureAwait(false);

            if (!approved)
            {
                return $"Refund of ${amount:F2} for {transactionId} was DENIED by the supervisor. " +
                       $"Reason given to customer: '{reason}'. Please offer an alternative " +
                       $"(waive the next fee, smaller goodwill credit, or escalate).";
            }
        }

        var confirmation = $"REF-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        return $"Refund of ${amount:F2} credited to the account associated with {transactionId} " +
               $"(reason: {reason}). Confirmation: {confirmation}. Funds typically post within " +
               $"one business day.";
    }
}
