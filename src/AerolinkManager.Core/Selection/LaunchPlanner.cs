using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Selection;

public sealed record LaunchDecision(
    LaunchProfile Profile,
    ProviderRecord Provider,
    ApiKeyRecord Key,
    string? ResolvedModel,
    bool UserSuppliedModel,
    IReadOnlyList<ApiKeyRecord> NormalizedKeys,
    DateTimeOffset? NearestReset);

public sealed class LaunchPlanner
{
    private readonly KeySelector _selector = new();

    public LaunchDecision? Plan(
        ManagerConfig config,
        IReadOnlyList<string> arguments,
        DateTimeOffset now,
        string? profileId = null,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var profile = ResolveProfile(config, profileId);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return profile is null ? null : PlanProfile(config, profile, arguments, now, random, visited);
    }

    private LaunchDecision? PlanProfile(
        ManagerConfig config,
        LaunchProfile profile,
        IReadOnlyList<string> arguments,
        DateTimeOffset now,
        Random? random,
        HashSet<string> visited)
    {
        if (!profile.Enabled || !visited.Add(profile.Id))
        {
            return null;
        }

        var providerIds = profile.ProviderIds.Count > 0
            ? profile.ProviderIds
            : config.Providers.Where(provider => provider.Enabled).Select(provider => provider.Id).ToList();
        var allNormalized = config.Keys.Select(key => KeySelector.NormalizeExpiredLimit(key, now)).ToList();
        DateTimeOffset? nearestReset = null;

        foreach (var providerId in providerIds)
        {
            var provider = config.Providers.FirstOrDefault(candidate => candidate.Id == providerId
                && ProviderCompatibility.IsLauncherCompatible(candidate));
            if (provider is null)
            {
                continue;
            }

            var keys = allNormalized.Where(key => key.ProviderId == provider.Id
                && (profile.AllowedKeyIds.Count == 0 || profile.AllowedKeyIds.Contains(key.Id))).ToArray();
            var strategy = profile.Strategy == SelectionStrategy.ProviderFallback
                ? SelectionStrategy.PriorityThenLru
                : profile.Strategy;
            var selection = _selector.Select(keys, now, strategy, profile.ManualKeyId, random);
            if (selection.NearestReset is not null && (nearestReset is null || selection.NearestReset < nearestReset))
            {
                nearestReset = selection.NearestReset;
            }

            if (selection.Key is not null)
            {
                var userModel = HasModelArgument(arguments);
                return new LaunchDecision(
                    profile,
                    provider,
                    selection.Key,
                    userModel ? null : ResolveModel(config, profile, provider),
                    userModel,
                    allNormalized,
                    nearestReset);
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.FallbackProfileId))
        {
            var fallback = config.LaunchProfiles.FirstOrDefault(candidate => candidate.Id == profile.FallbackProfileId);
            if (fallback is not null)
            {
                return PlanProfile(config, fallback, arguments, now, random, visited);
            }
        }

        return null;
    }

    public static bool HasModelArgument(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals("--model", StringComparison.OrdinalIgnoreCase)
                || arguments[index].StartsWith("--model=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static LaunchProfile? ResolveProfile(ManagerConfig config, string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId)
            ? config.LaunchProfiles.FirstOrDefault(profile => profile.Id == profileId)
            : config.LaunchProfiles.FirstOrDefault(profile => profile.IsDefault && profile.Enabled)
                ?? config.LaunchProfiles.FirstOrDefault(profile => profile.Enabled);

    private static string? ResolveModel(ManagerConfig config, LaunchProfile profile, ProviderRecord provider)
    {
        var requested = profile.ModelOverride ?? provider.DefaultModelId;
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        var model = config.Models.FirstOrDefault(candidate => candidate.Enabled
            && candidate.ProviderId == provider.Id
            && (candidate.Id == requested || candidate.ModelValue == requested || candidate.DisplayName == requested));
        return model?.ModelValue ?? requested;
    }
}
