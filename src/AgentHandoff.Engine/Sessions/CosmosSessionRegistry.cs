using System.Net;
using System.Text.Json.Serialization;
using AgentHandoff.Engine.Configuration;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine.Sessions;

/// <summary>
/// Cosmos DB-backed <see cref="ISessionRegistry"/>. Single container partitioned by
/// <c>/sessionId</c>, three document kinds discriminated by the <c>kind</c> property:
/// <list type="bullet">
///   <item><c>session</c> — id = sessionId</item>
///   <item><c>approval</c> — id = "approval:{approvalId}"</item>
///   <item><c>audit</c> — id = "audit:{guid}"</item>
/// </list>
/// Approval resolves use ETag optimistic concurrency to avoid double-decision races.
/// </summary>
public sealed class CosmosSessionRegistry : ISessionRegistry
{
    private readonly Container _container;
    private readonly ILogger<CosmosSessionRegistry>? _log;

    public CosmosSessionRegistry(Container container, ILogger<CosmosSessionRegistry>? log = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _log = log;
    }

    // ── Session lifecycle ────────────────────────────────────────────────────

    public void TouchSession(string sessionId, SessionStatus status, string? currentAgentId = null)
        => TouchSessionAsync(sessionId, status, currentAgentId).GetAwaiter().GetResult();

    private async Task TouchSessionAsync(string sessionId, SessionStatus status, string? currentAgentId)
    {
        var pk = new PartitionKey(sessionId);
        var now = DateTimeOffset.UtcNow;

        try
        {
            var existing = await _container.ReadItemAsync<SessionDoc>(sessionId, pk).ConfigureAwait(false);
            var doc = existing.Resource;
            doc.Status = status.ToString();
            if (currentAgentId is not null) doc.CurrentAgentId = currentAgentId;
            doc.LastActivityAt = now;
            await _container.ReplaceItemAsync(doc, sessionId, pk,
                new ItemRequestOptions { IfMatchEtag = existing.ETag }).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            var doc = new SessionDoc
            {
                Id = sessionId,
                SessionId = sessionId,
                Status = status.ToString(),
                CurrentAgentId = currentAgentId,
                CreatedAt = now,
                LastActivityAt = now,
                TurnCount = 0,
            };
            try { await _container.CreateItemAsync(doc, pk).ConfigureAwait(false); }
            catch (CosmosException dup) when (dup.StatusCode == HttpStatusCode.Conflict)
            {
                // Lost the race — retry once via replace path.
                await TouchSessionAsync(sessionId, status, currentAgentId).ConfigureAwait(false);
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Concurrent writer — best-effort retry.
            await TouchSessionAsync(sessionId, status, currentAgentId).ConfigureAwait(false);
        }
    }

    public void IncrementTurn(string sessionId)
        => IncrementTurnAsync(sessionId).GetAwaiter().GetResult();

    private async Task IncrementTurnAsync(string sessionId)
    {
        var pk = new PartitionKey(sessionId);
        try
        {
            var existing = await _container.ReadItemAsync<SessionDoc>(sessionId, pk).ConfigureAwait(false);
            var doc = existing.Resource;
            doc.TurnCount += 1;
            doc.LastActivityAt = DateTimeOffset.UtcNow;
            await _container.ReplaceItemAsync(doc, sessionId, pk,
                new ItemRequestOptions { IfMatchEtag = existing.ETag }).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Session not yet created; ignore.
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            await IncrementTurnAsync(sessionId).ConfigureAwait(false);
        }
    }

    public SessionSummary? GetSession(string sessionId)
    {
        try
        {
            var resp = _container.ReadItemAsync<SessionDoc>(sessionId, new PartitionKey(sessionId))
                                 .GetAwaiter().GetResult();
            return ToSummary(resp.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public IReadOnlyList<SessionSummary> ListSessions(SessionStatus? filter = null)
    {
        var sql = filter is null
            ? "SELECT * FROM c WHERE c.kind = 'session' ORDER BY c.lastActivityAt DESC"
            : $"SELECT * FROM c WHERE c.kind = 'session' AND c.status = '{filter.Value}' ORDER BY c.lastActivityAt DESC";

        return Query<SessionDoc>(sql).Select(ToSummary).ToList();
    }

    private SessionSummary ToSummary(SessionDoc s)
    {
        var pendingCount = Query<int>(
            $"SELECT VALUE COUNT(1) FROM c WHERE c.kind = 'approval' AND c.sessionId = '{s.SessionId}' AND c.status = 'Pending'")
            .FirstOrDefault();

        return new SessionSummary(
            SessionId: s.SessionId,
            Status: Enum.TryParse<SessionStatus>(s.Status, out var st) ? st : SessionStatus.Idle,
            CurrentAgentId: s.CurrentAgentId,
            CreatedAt: s.CreatedAt,
            LastActivityAt: s.LastActivityAt,
            PendingApprovalCount: pendingCount,
            TurnCount: s.TurnCount);
    }

    // ── Approval queue ───────────────────────────────────────────────────────

    public void EnqueueApproval(PendingApproval approval)
    {
        var doc = ApprovalDoc.From(approval);
        _container.UpsertItemAsync(doc, new PartitionKey(approval.SessionId)).GetAwaiter().GetResult();
        Append(new SessionAuditEntry(
            approval.SessionId,
            "approval.requested",
            $"{approval.ToolName} (id={approval.ApprovalId}, agent={approval.AgentId})",
            approval.CreatedAt));
    }

    public bool TryResolveApproval(
        string approvalId,
        ApprovalStatus outcome,
        string? decidedBy,
        string? reason,
        out PendingApproval resolved)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var docId = ApprovalDoc.MakeId(approvalId);
            ApprovalDoc current;
            string? etag;
            string sessionId;

            // Need to find the doc — partition is sessionId, which we don't know up front.
            var found = Query<ApprovalDoc>(
                $"SELECT * FROM c WHERE c.kind = 'approval' AND c.approvalId = '{approvalId}'").FirstOrDefault();
            if (found is null)
            {
                resolved = default!;
                return false;
            }
            sessionId = found.SessionId;

            try
            {
                var read = _container.ReadItemAsync<ApprovalDoc>(docId, new PartitionKey(sessionId))
                                     .GetAwaiter().GetResult();
                current = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                resolved = default!;
                return false;
            }

            if (!string.Equals(current.Status, ApprovalStatus.Pending.ToString(), StringComparison.Ordinal))
            {
                resolved = current.ToModel();
                return false;
            }

            current.Status = outcome.ToString();
            current.DecidedBy = decidedBy;
            current.DecisionReason = reason;
            current.DecidedAt = DateTimeOffset.UtcNow;

            try
            {
                _container.ReplaceItemAsync(current, docId, new PartitionKey(sessionId),
                    new ItemRequestOptions { IfMatchEtag = etag }).GetAwaiter().GetResult();
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                continue; // concurrent decision; retry
            }

            Append(new SessionAuditEntry(
                sessionId,
                $"approval.{outcome.ToString().ToLowerInvariant()}",
                $"{current.ToolName} (id={approvalId}) by {decidedBy ?? "unknown"}: {reason ?? "(no reason)"}",
                current.DecidedAt!.Value,
                Actor: decidedBy));

            resolved = current.ToModel();
            return true;
        }

        _log?.LogWarning("TryResolveApproval gave up after retries for approval {ApprovalId}", approvalId);
        resolved = default!;
        return false;
    }

    public PendingApproval? GetApproval(string approvalId)
    {
        var doc = Query<ApprovalDoc>(
            $"SELECT * FROM c WHERE c.kind = 'approval' AND c.approvalId = '{approvalId}'").FirstOrDefault();
        return doc?.ToModel();
    }

    public IReadOnlyList<PendingApproval> ListApprovals(ApprovalStatus? filter = null, string? sessionId = null)
    {
        var clauses = new List<string> { "c.kind = 'approval'" };
        if (filter is not null) clauses.Add($"c.status = '{filter.Value}'");
        if (sessionId is not null) clauses.Add($"c.sessionId = '{sessionId}'");
        var sql = $"SELECT * FROM c WHERE {string.Join(" AND ", clauses)} ORDER BY c.createdAt DESC";
        return Query<ApprovalDoc>(sql).Select(d => d.ToModel()).ToList();
    }

    public IReadOnlyList<PendingApproval> GetExpiredPending(DateTimeOffset asOf)
    {
        var iso = asOf.UtcDateTime.ToString("o");
        var sql = $"SELECT * FROM c WHERE c.kind = 'approval' AND c.status = 'Pending' AND c.expiresAt <= '{iso}'";
        return Query<ApprovalDoc>(sql).Select(d => d.ToModel()).ToList();
    }

    // ── Audit ────────────────────────────────────────────────────────────────

    public void Append(SessionAuditEntry entry)
    {
        var doc = new AuditDoc
        {
            Id = $"audit:{Guid.NewGuid():N}",
            SessionId = entry.SessionId,
            Kind2 = entry.Kind,
            Detail = entry.Detail,
            Timestamp = entry.Timestamp,
            Actor = entry.Actor,
        };
        try
        {
            _container.CreateItemAsync(doc, new PartitionKey(entry.SessionId)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to append audit entry for session {SessionId}", entry.SessionId);
        }
    }

    public IReadOnlyList<SessionAuditEntry> GetAudit(string sessionId)
    {
        var sql = $"SELECT * FROM c WHERE c.kind = 'audit' AND c.sessionId = '{sessionId}' ORDER BY c.timestamp ASC";
        return Query<AuditDoc>(sql, new PartitionKey(sessionId))
            .Select(d => new SessionAuditEntry(d.SessionId, d.Kind2, d.Detail, d.Timestamp, d.Actor))
            .ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IEnumerable<T> Query<T>(string sql, PartitionKey? partition = null)
    {
        var opts = partition is null
            ? new QueryRequestOptions()
            : new QueryRequestOptions { PartitionKey = partition.Value };
        using var iter = _container.GetItemQueryIterator<T>(new QueryDefinition(sql), requestOptions: opts);
        var results = new List<T>();
        while (iter.HasMoreResults)
        {
            var page = iter.ReadNextAsync().GetAwaiter().GetResult();
            results.AddRange(page);
        }
        return results;
    }

    /// <summary>
    /// Ensures the database and container exist with the expected partition key.
    /// Call this once at startup before constructing the registry.
    /// </summary>
    public static async Task<Container> EnsureContainerAsync(
        CosmosClient client,
        CosmosSessionRegistryOptions opts,
        CancellationToken cancellationToken = default)
    {
        if (opts.CreateIfNotExists)
        {
            var dbResp = await client.CreateDatabaseIfNotExistsAsync(opts.DatabaseId, cancellationToken: cancellationToken)
                                     .ConfigureAwait(false);
            var containerProps = new ContainerProperties(opts.ContainerId, partitionKeyPath: "/sessionId");
            await dbResp.Database.CreateContainerIfNotExistsAsync(containerProps, opts.ProvisionedThroughput,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return client.GetContainer(opts.DatabaseId, opts.ContainerId);
    }

    // ── Cosmos document shapes ───────────────────────────────────────────────

    private sealed class SessionDoc
    {
        [JsonPropertyName("id")]              public string Id { get; set; } = "";
        [JsonPropertyName("kind")]            public string Kind { get; set; } = "session";
        [JsonPropertyName("sessionId")]       public string SessionId { get; set; } = "";
        [JsonPropertyName("status")]          public string Status { get; set; } = "";
        [JsonPropertyName("currentAgentId")]  public string? CurrentAgentId { get; set; }
        [JsonPropertyName("createdAt")]       public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("lastActivityAt")]  public DateTimeOffset LastActivityAt { get; set; }
        [JsonPropertyName("turnCount")]       public int TurnCount { get; set; }
    }

    private sealed class ApprovalDoc
    {
        [JsonPropertyName("id")]              public string Id { get; set; } = "";
        [JsonPropertyName("kind")]            public string Kind { get; set; } = "approval";
        [JsonPropertyName("approvalId")]      public string ApprovalId { get; set; } = "";
        [JsonPropertyName("sessionId")]       public string SessionId { get; set; } = "";
        [JsonPropertyName("agentId")]         public string AgentId { get; set; } = "";
        [JsonPropertyName("toolName")]        public string ToolName { get; set; } = "";
        [JsonPropertyName("arguments")]       public Dictionary<string, object?> Arguments { get; set; } = new();
        [JsonPropertyName("createdAt")]       public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("expiresAt")]       public DateTimeOffset ExpiresAt { get; set; }
        [JsonPropertyName("status")]          public string Status { get; set; } = "Pending";
        [JsonPropertyName("decidedBy")]       public string? DecidedBy { get; set; }
        [JsonPropertyName("decisionReason")]  public string? DecisionReason { get; set; }
        [JsonPropertyName("decidedAt")]       public DateTimeOffset? DecidedAt { get; set; }

        public static string MakeId(string approvalId) => $"approval:{approvalId}";

        public static ApprovalDoc From(PendingApproval a) => new()
        {
            Id = MakeId(a.ApprovalId),
            ApprovalId = a.ApprovalId,
            SessionId = a.SessionId,
            AgentId = a.AgentId,
            ToolName = a.ToolName,
            Arguments = a.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
            CreatedAt = a.CreatedAt,
            ExpiresAt = a.ExpiresAt,
            Status = a.Status.ToString(),
            DecidedBy = a.DecidedBy,
            DecisionReason = a.DecisionReason,
            DecidedAt = a.DecidedAt,
        };

        public PendingApproval ToModel() => new(
            ApprovalId: ApprovalId,
            SessionId: SessionId,
            AgentId: AgentId,
            ToolName: ToolName,
            Arguments: Arguments,
            CreatedAt: CreatedAt,
            ExpiresAt: ExpiresAt,
            Status: Enum.TryParse<ApprovalStatus>(Status, out var s) ? s : ApprovalStatus.Pending,
            DecidedBy: DecidedBy,
            DecisionReason: DecisionReason,
            DecidedAt: DecidedAt);
    }

    private sealed class AuditDoc
    {
        [JsonPropertyName("id")]         public string Id { get; set; } = "";
        [JsonPropertyName("kind")]       public string Kind { get; set; } = "audit";
        [JsonPropertyName("sessionId")]  public string SessionId { get; set; } = "";
        // Avoid clobbering the discriminator with the entry's own kind label.
        [JsonPropertyName("auditKind")]  public string Kind2 { get; set; } = "";
        [JsonPropertyName("detail")]     public string Detail { get; set; } = "";
        [JsonPropertyName("timestamp")]  public DateTimeOffset Timestamp { get; set; }
        [JsonPropertyName("actor")]      public string? Actor { get; set; }
    }
}
