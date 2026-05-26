namespace AgentHandoff.VoiceGateway.Realtime;

public sealed record RealtimeOptions(
    string Endpoint,
    string Deployment,
    string ApiVersion,
    string ApiKey);
