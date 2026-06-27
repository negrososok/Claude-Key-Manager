using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Routing;

/// <summary>
/// An eligible (provider, key, model) triple the planner may select. Pricing is
/// resolved up front so cost-based strategies do not re-query the snapshot.
/// </summary>
public sealed record RouteCandidate
{
    public required ProviderRecord Provider { get; init; }
    public required KeyRuntime KeyRuntime { get; init; }
    public string? Model { get; init; }
    public ModelPricing? Pricing { get; init; }

    public ApiKeyRecord Key => KeyRuntime.Key;
    public string ProviderId => Provider.Id;
    public Guid KeyId => KeyRuntime.Key.Id;
}
