using System.Text.Json.Serialization;

namespace AerolinkManager.Core.Models;

public enum KeyStatus
{
    Available,
    Active,
    Limited,
    FiveHourLimited = Limited,
    WeeklyLimited,
    Disabled,
    Unknown
}

public sealed record KeyQuotaState
{
    public DateTimeOffset? FiveHourResetAt { get; init; }
    public bool FiveHourResetEstimated { get; init; }
    public DateTimeOffset? WeeklyResetAt { get; init; }
    public bool WeeklyResetUnknown { get; init; }
    public DateTimeOffset? ManualBlockedUntil { get; init; }
}

public sealed record KeyUsage
{
    public long Runs { get; init; }
    public long FailedRuns { get; init; }
    public long? EstimatedTokens { get; init; }
    public decimal? EstimatedCost { get; init; }
}

public sealed record ApiKeyRecord
{
    private KeyQuotaState _quotaState = new();
    private KeyUsage _usage = new();

    public required Guid Id { get; init; }
    public string ProviderId { get; init; } = ProviderPresets.AerolinkId;
    public required string Name { get; init; }
    public required string ApiKeyEncrypted { get; init; }
    public bool Enabled { get; init; } = true;
    public int Priority { get; init; } = 100;
    public string? PriorityGroup { get; init; }
    public KeyStatus Status { get; init; } = KeyStatus.Available;
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastErrorAt { get; init; }
    public string? LastErrorText { get; init; }
    public KeyQuotaState QuotaState { get => _quotaState; init => _quotaState = value ?? new(); }
    public KeyUsage Usage { get => _usage; init => _usage = value ?? new(); }
    public long AddedOrder { get; init; }

    // Transitional source compatibility for the Stage 1 UI and quota updater.
    [JsonIgnore] public DateTimeOffset? FiveHourResetAt { get => _quotaState.FiveHourResetAt; init => _quotaState = _quotaState with { FiveHourResetAt = value }; }
    [JsonIgnore] public bool FiveHourResetEstimated { get => _quotaState.FiveHourResetEstimated; init => _quotaState = _quotaState with { FiveHourResetEstimated = value }; }
    [JsonIgnore] public DateTimeOffset? WeeklyBlockedUntil { get => _quotaState.WeeklyResetAt; init => _quotaState = _quotaState with { WeeklyResetAt = value }; }
    [JsonIgnore] public bool WeeklyBlockedUnknown { get => _quotaState.WeeklyResetUnknown; init => _quotaState = _quotaState with { WeeklyResetUnknown = value }; }
    [JsonIgnore] public long TotalRuns { get => _usage.Runs; init => _usage = _usage with { Runs = value }; }
    [JsonIgnore] public long FailedRuns { get => _usage.FailedRuns; init => _usage = _usage with { FailedRuns = value }; }
}
