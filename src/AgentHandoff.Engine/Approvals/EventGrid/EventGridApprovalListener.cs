using System.Text.Json;
using Azure;
using Azure.Messaging.EventGrid.Namespaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine.Approvals.EventGrid;

/// <summary>
/// Long-running pull receiver against the inbound Event Grid namespace topic
/// (<c>dagentout</c> by convention). Each event is dispatched to the in-process
/// <see cref="IApprovalDispatcher"/>; lock disposition follows the result:
///   - resolved here  → Acknowledge
///   - unknown id     → Release (let another consumer / replica try)
///   - bad payload    → Reject (drop)
/// </summary>
public sealed class EventGridApprovalListener : BackgroundService
{
    private readonly EventGridApprovalOptions _options;
    private readonly IApprovalDispatcher _dispatcher;
    private readonly ILogger<EventGridApprovalListener>? _log;
    private readonly EventGridReceiverClient _receiver;

    public EventGridApprovalListener(
        EventGridApprovalOptions options,
        IApprovalDispatcher dispatcher,
        ILogger<EventGridApprovalListener>? log = null)
    {
        _options = options;
        _dispatcher = dispatcher;
        _log = log;

        if (string.IsNullOrWhiteSpace(options.NamespaceEndpoint) ||
            string.IsNullOrWhiteSpace(options.AccessKey)        ||
            string.IsNullOrWhiteSpace(options.InboundTopic)     ||
            string.IsNullOrWhiteSpace(options.InboundSubscription))
        {
            throw new InvalidOperationException(
                "Approval:EventGrid requires NamespaceEndpoint, AccessKey, InboundTopic, InboundSubscription.");
        }

        _receiver = new EventGridReceiverClient(
            new Uri(options.NamespaceEndpoint),
            options.InboundTopic,
            options.InboundSubscription,
            new AzureKeyCredential(options.AccessKey));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log?.LogInformation("[EventGrid] Listener starting on topic {Topic}/{Sub}.",
            _options.InboundTopic, _options.InboundSubscription);

        var maxWait = TimeSpan.FromSeconds(_options.ReceiveLockSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await _receiver.ReceiveAsync(
                    maxEvents: _options.MaxEventsPerReceive,
                    maxWaitTime: maxWait,
                    cancellationToken: stoppingToken).ConfigureAwait(false);

                if (batch?.Value is null || batch.Value.Details.Count == 0) continue;

                foreach (var detail in batch.Value.Details)
                {
                    var lockToken = detail.BrokerProperties.LockToken;
                    var cloudEvent = detail.Event;

                    ApprovalDecisionEnvelope? decision = null;
                    try
                    {
                        decision = cloudEvent.Data?.ToObjectFromJson<ApprovalDecisionEnvelope>(
                            new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    }
                    catch (Exception ex)
                    {
                        _log?.LogWarning(ex, "[EventGrid] Bad payload on {EventId}; rejecting.", cloudEvent.Id);
                    }

                    if (decision is null || string.IsNullOrWhiteSpace(decision.ApprovalId))
                    {
                        await _receiver.RejectAsync(new[] { lockToken }, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    var resolved = _dispatcher.Resolve(
                        decision.ApprovalId, decision.Approved, decision.DecidedBy, decision.Reason);

                    if (resolved)
                    {
                        await _receiver.AcknowledgeAsync(new[] { lockToken }, stoppingToken).ConfigureAwait(false);
                        _log?.LogInformation("[EventGrid] Resolved approval {ApprovalId} approved={Approved}.",
                            decision.ApprovalId, decision.Approved);
                    }
                    else
                    {
                        // Unknown on this host (already resolved via HTTP path, stale, or owned by another replica).
                        // Release a few times to give other consumers a chance, then acknowledge to break the
                        // tight redelivery loop that would otherwise spam this consumer indefinitely.
                        var attempts = detail.BrokerProperties.DeliveryCount;
                        if (attempts >= _options.MaxDeliveryAttempts)
                        {
                            await _receiver.AcknowledgeAsync(new[] { lockToken }, stoppingToken).ConfigureAwait(false);
                            _log?.LogWarning(
                                "[EventGrid] Unknown approval {ApprovalId} after {Attempts} deliveries; acknowledging to drop.",
                                decision.ApprovalId, attempts);
                        }
                        else
                        {
                            var delay = MapReleaseDelay(_options.ReleaseDelaySeconds);
                            await _receiver.ReleaseAsync(new[] { lockToken }, delay, stoppingToken).ConfigureAwait(false);
                            _log?.LogDebug(
                                "[EventGrid] Unknown approval {ApprovalId} on this host (attempt {Attempts}); released with delay {Delay}.",
                                decision.ApprovalId, attempts, delay);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log?.LogError(ex, "[EventGrid] Receive loop error; backing off 5s.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        _log?.LogInformation("[EventGrid] Listener stopped.");
    }

    private static ReleaseDelay MapReleaseDelay(int seconds) => seconds switch
    {
        <= 0    => ReleaseDelay.NoDelay,
        <= 10   => ReleaseDelay.TenSeconds,
        <= 60   => ReleaseDelay.OneMinute,
        <= 600  => ReleaseDelay.TenMinutes,
        _       => ReleaseDelay.OneHour,
    };
}
