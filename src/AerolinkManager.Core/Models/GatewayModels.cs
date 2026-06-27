namespace AerolinkManager.Core.Models;

public enum RoutingMode { Launcher, LocalGateway }
public enum ProviderAuthScheme { XApiKey, Bearer, Custom }
public enum ModelMode { Default, RespectUser, PreferProfile, ForceProfile }
public enum CircuitState { Closed, Open, HalfOpen }
public enum BudgetScope { Key, Provider, Profile, Model }
public enum BudgetType { None, Tokens, Cost, Requests }
public enum BudgetWindow { Daily, Weekly, Monthly, Manual }

public sealed record GatewaySettings
{
    public RoutingMode RoutingMode { get; init; } = RoutingMode.LocalGateway;
    public int Port { get; init; } = 17844;
    public bool AutoStartWithApp { get; init; } = true;
    public bool StartWhenWrapperRuns { get; init; } = true;
    public bool EnableModelDiscovery { get; init; } = true;
    public bool LogRequestBodies { get; init; }
    public bool UsageTrackingEnabled { get; init; } = true;
    public bool CostTrackingEnabled { get; init; } = true;
    public bool DeveloperMode { get; init; }
    public int MaxRequestBodyMb { get; init; } = 64;
    public string? LocalAuthTokenEncrypted { get; init; }
}

public sealed record ProviderCapabilities
{
    public bool Messages { get; init; } = true;
    public bool CountTokens { get; init; } = true;
    public bool Models { get; init; }
    public bool Streaming { get; init; } = true;
    public bool UsageInStreaming { get; init; } = true;
    public bool RateLimitHeaders { get; init; }
}

public sealed record RoutingChain
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public List<RoutingChainStep> Steps { get; init; } = [];
    public int MaxFallbackSteps { get; init; } = 3;
}

public sealed record RoutingChainStep
{
    public int Order { get; init; }
    public List<string> ProviderIds { get; init; } = [];
    public List<Guid> AllowedKeyIds { get; init; } = [];
    public string? ModelOverride { get; init; }
    public SelectionStrategy Strategy { get; init; } = SelectionStrategy.PriorityThenLru;
    public string? BudgetPolicyId { get; init; }
    public bool StopIfAvailable { get; init; } = true;
}

public sealed record ModelPricing
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelValue { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal InputPerMillion { get; init; }
    public decimal OutputPerMillion { get; init; }
    public decimal CacheReadPerMillion { get; init; }
    public decimal CacheWritePerMillion { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record BudgetPolicy
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public BudgetScope Scope { get; init; }
    public string? ScopeId { get; init; }
    public BudgetType Type { get; init; }
    public BudgetWindow Window { get; init; } = BudgetWindow.Monthly;
    public decimal Limit { get; init; }
    public DateTimeOffset? ManualResetAt { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record ProviderCircuitRecord
{
    public required string ProviderId { get; init; }
    public CircuitState State { get; init; } = CircuitState.Closed;
    public DateTimeOffset? OpenedUntil { get; init; }
    public int FailureCount { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
}

public sealed record ModelLockoutRecord
{
    public required string ProviderId { get; init; }
    public required string ModelValue { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }
    public required string Reason { get; init; }
    public bool Estimated { get; init; }
}
