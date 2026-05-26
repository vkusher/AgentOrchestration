namespace AgentHandoff.Api.Models;

public sealed record ChatAttachment(string Filename, string? ContentType, string Base64);

public sealed record ChatRequest(
    string Message,
    string? SessionId = null,
    string? Mode = null,
    IReadOnlyList<ChatAttachment>? Attachments = null);

public sealed record ApprovalDecisionRequest(string SessionId, string ApprovalId, bool Approved);

public sealed record ApprovalDecisionBody(bool Approved, string? DecidedBy = null, string? Reason = null);

public sealed record AgentInfo(string Id, string DisplayName, string Role, string Description);

public sealed record AgentsResponse(IReadOnlyList<AgentInfo> Agents);
