using System.ComponentModel;

namespace AgentHandoff.Engine.Tools;

/// <summary>
/// Tools used by the Accounts &amp; Cards agent (formerly "OrderShipping").
/// File name kept for minimal churn — the class itself is renamed.
/// In production these would call the bank's core / card-management systems.
/// </summary>
public sealed class AccountTools
{
    private static readonly Dictionary<string, (string Status, string ETA, string Channel, string Reference)> _txns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["TXN-2026-0042"] = ("Pending",   "2026-05-10", "Wire (intl)", "MTREF-9F2A1"),
            ["TXN-2026-0099"] = ("Posted",    "2026-05-05", "Card",        "AUTH-7C113B"),
            ["TXN-2026-0123"] = ("Pending",   "2026-05-12", "ACH",         "ACHID-44218"),
        };

    [Description(
        "Look up the current status of a customer transaction (wire, ACH, card). " +
        "Returns the channel, expected settlement date, and reference id.")]
    public string GetTransactionStatus(
        [Description("The transaction id, e.g. TXN-2026-0042.")] string transactionId)
    {
        if (_txns.TryGetValue(transactionId, out var info))
        {
            return $"Transaction {transactionId}: status={info.Status}, expected={info.ETA}, " +
                   $"channel={info.Channel}, reference={info.Reference}.";
        }
        return $"Transaction {transactionId} not found.";
    }

    [Description(
        "Initiate a replacement card for a customer. Returns a tracking case id and an expected " +
        "delivery window. Use when the customer reports a lost, stolen, or damaged card.")]
    public string RequestCardReplacement(
        [Description("Last 4 digits of the card to replace, e.g. 4242.")] string cardLast4,
        [Description("Reason code: lost, stolen, damaged, fraud, expired.")] string reasonCode)
    {
        var caseId = $"CASE-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        return $"Card ending {cardLast4} flagged as {reasonCode}; replacement card mailed. " +
               $"Case id: {caseId}. Expected delivery: 5-7 business days. " +
               $"The old card has been deactivated.";
    }
}
