namespace AerolinkManager.Core.Models;

public enum QuotaPolicyType
{
    None,
    Manual,
    FixedOrDetectedResetWindow,
    Weekly,
    Monthly,
    Composite
}

public sealed record QuotaPolicyRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public QuotaPolicyType Type { get; init; }
    public TimeSpan? FallbackResetWindow { get; init; }
    public List<string> ChildPolicyIds { get; init; } = [];

    public static List<QuotaPolicyRecord> Defaults() =>
    [
        new() { Id = "none", Name = "No automatic quota", Type = QuotaPolicyType.None },
        new() { Id = "manual", Name = "Manual", Type = QuotaPolicyType.Manual },
        new() { Id = "aerolink-5h", Name = "Aerolink 5-hour", Type = QuotaPolicyType.FixedOrDetectedResetWindow, FallbackResetWindow = TimeSpan.FromHours(5) },
        new() { Id = "aerolink-weekly", Name = "Aerolink weekly", Type = QuotaPolicyType.Weekly },
        new() { Id = ProviderPresets.AerolinkQuotaPolicyId, Name = "Aerolink 5-hour + weekly", Type = QuotaPolicyType.Composite, ChildPolicyIds = ["aerolink-5h", "aerolink-weekly"] }
    ];
}
