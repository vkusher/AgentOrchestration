using System.Text.Json;
using Azure;
using Azure.Messaging;
using Azure.Messaging.EventGrid.Namespaces;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine.Approvals.EventGrid;

/// <summary>
/// Publishes <see cref="ApprovalRequestEnvelope"/>s to the outbound Event Grid namespace topic
/// (<c>dagentin</c> by convention). Best-effort: if the broker is unreachable the in-process
/// SSE / HTTP approval flow still works.
/// </summary>
public sealed class EventGridApprovalPublisher : IApprovalPublisher
{
    private readonly EventGridSenderClient _sender;
    private readonly ILogger<EventGridApprovalPublisher>? _log;

    public EventGridApprovalPublisher(EventGridApprovalOptions options, ILogger<EventGridApprovalPublisher>? log = null)
    {
        if (string.IsNullOrWhiteSpace(options.NamespaceEndpoint))
            throw new InvalidOperationException("Approval:EventGrid:NamespaceEndpoint is required when Enabled=true.");
        if (string.IsNullOrWhiteSpace(options.AccessKey))
            throw new InvalidOperationException("Approval:EventGrid:AccessKey is required when Enabled=true.");
        if (string.IsNullOrWhiteSpace(options.OutboundTopic))
            throw new InvalidOperationException("Approval:EventGrid:OutboundTopic is required when Enabled=true.");

        _sender = new EventGridSenderClient(
            new Uri(options.NamespaceEndpoint),
            options.OutboundTopic,
            new AzureKeyCredential(options.AccessKey));
        _log = log;
    }

    public async Task PublishRequestAsync(ApprovalRequestEnvelope envelope, CancellationToken ct = default)
    {
        var cloudEvent = new CloudEvent(
            source: "https://agenthandoff.local/api",
            type:   "agenthandoff.approval.requested",
            jsonSerializableData: envelope)
        {
            Id      = envelope.ApprovalId,
            Subject = $"session/{envelope.SessionId}/approval/{envelope.ApprovalId}",
            Time    = envelope.CreatedAt,
        };

        try
        {
            await _sender.SendAsync(cloudEvent, ct).ConfigureAwait(false);
            _log?.LogInformation("[EventGrid] Published approval.requested {ApprovalId} for session {SessionId}.",
                envelope.ApprovalId, envelope.SessionId);
        }
        catch (Exception ex)
        {
            // Don't fail the approval — local SSE/HTTP path remains the source of truth.
            _log?.LogWarning(ex, "[EventGrid] Failed to publish approval.requested {ApprovalId}.", envelope.ApprovalId);
        }
    }
}
