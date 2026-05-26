namespace AgentHandoff.Engine.Approvals.EventGrid;

/// <summary>
/// Bound from <c>Approval:EventGrid</c>. When <see cref="Enabled"/> is false the system uses
/// the default <c>NullApprovalPublisher</c> and no listener is started.
/// </summary>
public sealed class EventGridApprovalOptions
{
    public const string SectionName = "Approval:EventGrid";

    public bool Enabled { get; set; }

    /// <summary>e.g. "https://my-ns.eastus-1.eventgrid.azure.net".</summary>
    public string? NamespaceEndpoint { get; set; }

    /// <summary>Single namespace-level access key used for both topics.</summary>
    public string? AccessKey { get; set; }

    /// <summary>Outbound topic the app publishes <c>ApprovalRequested</c> events to.</summary>
    public string OutboundTopic { get; set; } = "dagentin";

    /// <summary>Inbound topic the app subscribes to for <c>ApprovalDecided</c> events.</summary>
    public string InboundTopic { get; set; } = "dagentout";

    /// <summary>Subscription name on the inbound topic.</summary>
    public string InboundSubscription { get; set; } = "agenthandoff-api";

    public int MaxEventsPerReceive { get; set; } = 10;

    public int ReceiveLockSeconds { get; set; } = 60;

    /// <summary>
    /// Max times an event for an unknown / already-resolved approval will be released back
    /// to the topic before being acknowledged (dropped). Prevents a tight redelivery loop
    /// when a decision arrives for an approval this replica doesn't own (e.g. it was already
    /// resolved via the HTTP path, or belongs to a session on another replica that no longer exists).
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>
    /// Delay (seconds) applied when releasing an unknown approval event so it isn't redelivered
    /// immediately. Maps to <c>ReleaseDelay</c>; allowed values are 0, 10, 60, 600, 3600.
    /// </summary>
    public int ReleaseDelaySeconds { get; set; } = 10;
}
