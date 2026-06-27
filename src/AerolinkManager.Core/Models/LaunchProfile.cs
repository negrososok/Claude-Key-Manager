namespace AerolinkManager.Core.Models;

public enum SelectionStrategy
{
    LeastRecentlyUsed,
    PriorityOrder,
    PriorityThenLru,
    Random,
    ManualKey,
    ProviderFallback,
    LeastUsed,
    RoundRobin,
    FillFirst,
    CostOptimized,
    ResetAware,
    Manual
}

public sealed record LaunchProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public List<string> ProviderIds { get; init; } = [];
    public List<Guid> AllowedKeyIds { get; init; } = [];
    public string? ModelOverride { get; init; }
    public SelectionStrategy Strategy { get; init; } = SelectionStrategy.PriorityThenLru;
    public string? FallbackProfileId { get; init; }
    public bool IsDefault { get; init; }
    public Guid? ManualKeyId { get; init; }
    public string? RoutingChainId { get; init; }
    public ModelMode ModelMode { get; init; } = ModelMode.RespectUser;
    public bool SessionAffinity { get; init; } = true;
    public int MaxRetries { get; init; } = 3;
    public string? BudgetPolicyId { get; init; }
    public int WaitForCooldownIfNearestUnderMinutes { get; init; }
}
