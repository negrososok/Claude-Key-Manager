using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Routing;

/// <summary>
/// Per-key runtime state needed for routing decisions that is not part of the
/// persisted <see cref="ApiKeyRecord"/>. Cooldown, recent usage counts and the
/// latest rate-limit headroom are produced by the gateway/usage layer and fed
/// into the planner through an immutable snapshot.
/// </summary>
public sealed record KeyRuntime
{
    public required ApiKeyRecord Key { get; init; }

    /// <summary>Key-scoped cooldown (429/rate/manual). The key is skipped while this is in the future.</summary>
    public DateTimeOffset? CooldownUntil { get; init; }

    /// <summary>Requests attributed to this key in the active usage window (least_used / round_robin / fill_first).</summary>
    public long WindowRequestCount { get; init; }

    /// <summary>Tokens attributed to this key in the active usage window.</summary>
    public long WindowTokenCount { get; init; }

    /// <summary>Remaining tokens reported by upstream rate-limit headers, when known (reset_aware).</summary>
    public long? RateLimitTokensRemaining { get; init; }

    /// <summary>Upstream rate-limit reset instant, when known.</summary>
    public DateTimeOffset? RateLimitResetAt { get; init; }
}

/// <summary>Current consumption for a single <see cref="BudgetPolicy"/> within its active window.</summary>
public sealed record BudgetUsage
{
    public required string PolicyId { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public long UsedTokens { get; init; }
    public decimal UsedCost { get; init; }
    public long UsedRequests { get; init; }
}

/// <summary>Sticky route remembered for a Claude Code session when affinity is enabled.</summary>
public sealed record SessionAffinityRecord
{
    public required string SessionId { get; init; }
    public string? ProviderId { get; init; }
    public Guid? KeyId { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
}

/// <summary>
/// Immutable input to <c>RoutePlanner</c>. The live gateway and the dry-run
/// simulator both build a snapshot and must receive identical plans from
/// identical snapshots — the planner performs no I/O and reads nothing else.
/// </summary>
public sealed record RoutingSnapshot
{
    public required DateTimeOffset Now { get; init; }
    public required IReadOnlyList<ProviderRecord> Providers { get; init; }
    public required IReadOnlyList<KeyRuntime> Keys { get; init; }
    public required IReadOnlyList<RoutingChain> Chains { get; init; }
    public required IReadOnlyList<LaunchProfile> Profiles { get; init; }
    public IReadOnlyList<ModelRecord> Models { get; init; } = [];
    public IReadOnlyList<ModelPricing> Pricing { get; init; } = [];
    public IReadOnlyList<BudgetPolicy> BudgetPolicies { get; init; } = [];
    public IReadOnlyList<BudgetUsage> BudgetUsages { get; init; } = [];
    public IReadOnlyList<ProviderCircuitRecord> Circuits { get; init; } = [];
    public IReadOnlyList<ModelLockoutRecord> ModelLockouts { get; init; } = [];
    public IReadOnlyList<SessionAffinityRecord> Sessions { get; init; } = [];

    /// <summary>round_robin cursor per scope key (<c>chainId:stepOrder</c>); absent scopes start at 0.</summary>
    public IReadOnlyDictionary<string, int> RoundRobinCursors { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}
