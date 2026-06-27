namespace AerolinkManager.Core.Storage;

public sealed record RequestUsageRecord
{
    public required string RequestId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? SessionId { get; init; }
    public string? AgentId { get; init; }
    public string? ParentAgentId { get; init; }
    public string? ProfileId { get; init; }
    public string? ProviderId { get; init; }
    public Guid? KeyId { get; init; }
    public string? Model { get; init; }
    public bool Streaming { get; init; }
    public int StatusCode { get; init; }
    public string? ErrorType { get; init; }
    public long DurationMs { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheCreationInputTokens { get; init; }
    public long CacheReadInputTokens { get; init; }
    public long ServerToolUse { get; init; }
    public long? EstimatedCostMicros { get; init; }
    public string? Currency { get; init; }
}

public sealed record SessionUpsert
{
    public required string SessionId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? ProfileId { get; init; }
    public string? ProviderId { get; init; }
    public Guid? KeyId { get; init; }
    public string? Model { get; init; }
    public long TokensDelta { get; init; }
    public long? CostMicrosDelta { get; init; }
    public bool KeySwitched { get; init; }
    public required string Status { get; init; }
}

public sealed record UsageEventRecord
{
    public required string RequestId { get; init; }
    public required string EventType { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheCreationInputTokens { get; init; }
    public long CacheReadInputTokens { get; init; }
    public long ServerToolUse { get; init; }
}

public sealed record LimitEventRecord
{
    public string? RequestId { get; init; }
    public string? ProviderId { get; init; }
    public Guid? KeyId { get; init; }
    public string? Model { get; init; }
    public required string LimitType { get; init; }
    public DateTimeOffset? ResetAtUtc { get; init; }
    public bool Estimated { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RouteDecisionRecord
{
    public required string RequestId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? SessionId { get; init; }
    public string? ProfileId { get; init; }
    public string? ChainId { get; init; }
    public string? SelectedProviderId { get; init; }
    public Guid? SelectedKeyId { get; init; }
    public string? SelectedModel { get; init; }
    public required string DecisionReason { get; init; }
    public required string TraceJson { get; init; }
    public required string Story { get; init; }
}

public sealed record RouteDecisionQuery
{
    public string? SessionId { get; init; }
    public string? ProfileId { get; init; }
    public string? ProviderId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int Limit { get; init; } = 20;
}

public sealed record KeySwitchRecord
{
    public string? SessionId { get; init; }
    public Guid? FromKeyId { get; init; }
    public required Guid ToKeyId { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RequestQuery
{
    public string? SessionId { get; init; }
    public string? ProviderId { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int? StatusCode { get; init; }
}

// ── Phase C DTOs ──

public enum UsageGrouping { Period, Provider, Key, Model, Profile, Session }

public sealed record UsageQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public UsageGrouping GroupBy { get; init; } = UsageGrouping.Period;
    public string? PeriodBucket { get; init; } // "hour", "day", "month"
    public string? ProviderId { get; init; }
    public string? ProfileId { get; init; }
    public string? Model { get; init; }
    public string? SessionId { get; init; }
    public Guid? KeyId { get; init; }
}

public sealed record UsageGroupRow
{
    public required string GroupKey { get; init; }
    public long RequestCount { get; init; }
    public long TotalInputTokens { get; init; }
    public long TotalOutputTokens { get; init; }
    public long TotalCacheCreationInputTokens { get; init; }
    public long TotalCacheReadInputTokens { get; init; }
    public long TotalServerToolUse { get; init; }
    public long? TotalCostMicros { get; init; }
    public string? Currency { get; init; }
    public double SuccessRate { get; init; }
    public long P50DurationMs { get; init; }
    public long P95DurationMs { get; init; }
    public DateTimeOffset FirstTimestamp { get; init; }
    public DateTimeOffset LastTimestamp { get; init; }
}

public sealed record SessionSummaryRow
{
    public required string SessionId { get; init; }
    public long RequestCount { get; init; }
    public long TotalInputTokens { get; init; }
    public long TotalOutputTokens { get; init; }
    public long TotalCacheCreationInputTokens { get; init; }
    public long TotalCacheReadInputTokens { get; init; }
    public long? TotalCostMicros { get; init; }
    public string? Currency { get; init; }
    public long SuccessCount { get; init; }
    public long ErrorCount { get; init; }
    public long PartialCount { get; init; }
    public DateTimeOffset? FirstTimestamp { get; init; }
    public DateTimeOffset? LastTimestamp { get; init; }
    public IReadOnlyList<SessionProviderBreakdown> ProviderBreakdowns { get; init; } = [];
}

public sealed record SessionProviderBreakdown
{
    public required string ProviderId { get; init; }
    public string? Model { get; init; }
    public long RequestCount { get; init; }
    public long TotalInputTokens { get; init; }
    public long TotalOutputTokens { get; init; }
    public long? TotalCostMicros { get; init; }
}

public sealed record ProviderHealthRow
{
    public required string ProviderId { get; init; }
    public string CircuitState { get; init; } = "closed";
    public long? P50LatencyMs { get; init; }
    public long? P95LatencyMs { get; init; }
    public double? SuccessRate { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public long TotalRequests { get; init; }
    public long ErrorCount { get; init; }
}

public interface IUsageStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task AddRequestAsync(RequestUsageRecord request, CancellationToken cancellationToken = default);
    Task<long> CountRequestsAsync(CancellationToken cancellationToken = default);
    Task UpsertSessionAsync(SessionUpsert session, CancellationToken cancellationToken = default);
    Task AddUsageEventAsync(UsageEventRecord usageEvent, CancellationToken cancellationToken = default);
    Task AddLimitEventAsync(LimitEventRecord limitEvent, CancellationToken cancellationToken = default);
    Task AddRouteDecisionAsync(RouteDecisionRecord routeDecision, CancellationToken cancellationToken = default);
    Task AddKeySwitchAsync(KeySwitchRecord keySwitch, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RequestUsageRecord>> QueryRequestsAsync(RequestQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RouteDecisionRecord>> QueryRouteDecisionsAsync(RouteDecisionQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UsageGroupRow>> QueryGroupedUsageAsync(UsageQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionSummaryRow>> QuerySessionSummariesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderHealthRow>> GetProviderHealthAsync(CancellationToken cancellationToken = default);
    Task RecomputeProviderHealthAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
