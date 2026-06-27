using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Configuration;

public sealed record ManagerConfig
{
    public const int CurrentSchemaVersion = 3;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string? RealClaudePath { get; init; }
    public bool ManagedCommandEnabled { get; init; }
    public string Language { get; init; } = "en";
    public List<ProviderRecord> Providers { get; init; } = ProviderPresets.Defaults();
    public List<ApiKeyRecord> Keys { get; init; } = [];
    public List<ModelRecord> Models { get; init; } = [];
    public List<QuotaPolicyRecord> QuotaPolicies { get; init; } = QuotaPolicyRecord.Defaults();
    public List<ErrorPatternSetRecord> ErrorPatternSets { get; init; } = ErrorPatternSetRecord.Defaults();
    public GatewaySettings Gateway { get; init; } = new();
    public List<RoutingChain> RoutingChains { get; init; } =
    [
        new()
        {
            Id = "chain-default",
            Name = "Default Chain",
            Steps = [new RoutingChainStep { Order = 1, ProviderIds = [] }]
        }
    ];
    public List<ModelPricing> ModelPricing { get; init; } = [];
    public List<BudgetPolicy> BudgetPolicies { get; init; } = [];
    public List<LaunchProfile> LaunchProfiles { get; init; } =
    [
        new()
        {
            Id = "default",
            Name = "Default",
            ProviderIds = [],
            RoutingChainId = "chain-default",
            IsDefault = true
        }
    ];
}

public sealed record ManagerState
{
    public Guid? CurrentKeyId { get; init; }
    public string? CurrentProfileId { get; init; }
    public string? CurrentProviderId { get; init; }
    public string? CurrentModel { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public Guid? NotificationId { get; init; }
    public string? NotificationTitle { get; init; }
    public string? NotificationMessage { get; init; }
    public int? GatewayProcessId { get; init; }
    public int? GatewayPort { get; init; }
    public DateTimeOffset? GatewayStartedAt { get; init; }
    public string? GatewayError { get; init; }
}
