namespace AerolinkManager.Core.Routing;

public enum RouteOutcome
{
    /// <summary>A provider/key/model was selected.</summary>
    Selected,

    /// <summary>No candidate is currently available and waiting is not advised.</summary>
    NoCandidate,

    /// <summary>No candidate now, but the nearest reset is close enough that the profile allows waiting.</summary>
    WaitRecommended
}

/// <summary>A candidate that was considered and rejected, with a human-readable reason.</summary>
public sealed record SkippedCandidate(string ProviderId, Guid? KeyId, string? Model, string Reason);

/// <summary>Trace of a single routing-chain step: which candidates it skipped and whether it produced the selection.</summary>
public sealed record RouteStepTrace
{
    public required int Order { get; init; }
    public required string Strategy { get; init; }
    public IReadOnlyList<SkippedCandidate> Skipped { get; init; } = [];
    public bool Selected { get; init; }
    public string? Note { get; init; }
}

/// <summary>Computed advice to wait for the nearest cooldown instead of failing immediately.</summary>
public sealed record WaitRecommendation(DateTimeOffset NearestReset, TimeSpan WaitFor, string Reason);

/// <summary>
/// Fully explainable result of a routing decision. The same instance backs both
/// the live route (persisted as a route_decision) and the simulator output.
/// </summary>
public sealed record RoutePlan
{
    public required RouteOutcome Outcome { get; init; }
    public string? RequestId { get; init; }
    public string? SessionId { get; init; }
    public string? ProfileId { get; init; }
    public string? ChainId { get; init; }
    public string? RequestedModel { get; init; }

    public int? SelectedStepOrder { get; init; }
    public string? SelectedProviderId { get; init; }
    public Guid? SelectedKeyId { get; init; }
    public string? SelectedModel { get; init; }

    public string DecisionReason { get; init; } = string.Empty;

    /// <summary>Every candidate skipped across all attempted steps, in evaluation order.</summary>
    public IReadOnlyList<SkippedCandidate> SkippedCandidates { get; init; } = [];

    /// <summary>Per-step traces, including fallback steps that produced no candidate.</summary>
    public IReadOnlyList<RouteStepTrace> Steps { get; init; } = [];

    public decimal? EstimatedCost { get; init; }
    public string? Currency { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>True when session affinity reused the session's previous route.</summary>
    public bool AffinityHonored { get; init; }

    public WaitRecommendation? Wait { get; init; }

    public bool HasSelection => Outcome == RouteOutcome.Selected && SelectedProviderId is not null;
}

/// <summary>Canonical skip/decision reason strings so live and simulator traces read identically.</summary>
public static class RouteReasons
{
    public const string ProviderUnknown = "provider_unknown";
    public const string ProviderDisabled = "provider_disabled";
    public const string ProviderGatewayDisabled = "provider_gateway_disabled";
    public const string ProviderCircuitOpen = "provider_circuit_open";
    public const string ModelLocked = "model_locked";
    public const string ModelUnavailable = "model_unavailable";
    public const string KeyDisabled = "key_disabled";
    public const string KeyFiveHourLimited = "five_hour_limited";
    public const string KeyWeeklyLimited = "weekly_limited";
    public const string KeyManualBlocked = "manual_blocked";
    public const string KeyCooldown = "key_cooldown";
    public const string KeyNotEligible = "key_not_eligible";
    public const string KeyNotAllowedInStep = "key_not_allowed_in_step";
    public const string BudgetExhausted = "budget_exhausted";
    public const string NoKeysForProvider = "no_keys_for_provider";
    public const string ManualKeyUnavailable = "manual_key_unavailable";
    public const string NoProfile = "no_profile";
    public const string NoChain = "no_chain";
    public const string ChainDisabled = "chain_disabled";
    public const string MaxFallbackStepsReached = "max_fallback_steps_reached";
    public const string SessionAffinity = "session_affinity";
}
