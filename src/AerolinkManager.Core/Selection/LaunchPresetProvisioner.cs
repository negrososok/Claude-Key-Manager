using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Selection;

public sealed class LaunchPresetProvisioner
{
    public const string DefaultPresetId = "default";

    public ManagerConfig EnsureDefaultPreset(ManagerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var existingDefaultIndex = config.LaunchProfiles.FindIndex(profile =>
            string.Equals(profile.Id, DefaultPresetId, StringComparison.OrdinalIgnoreCase)
            && profile.IsDefault);
        if (existingDefaultIndex >= 0)
        {
            var profiles = config.LaunchProfiles.ToList();
            var existingDefault = profiles[existingDefaultIndex];
            profiles[existingDefaultIndex] = existingDefault with
            {
                ProviderIds = [],
                AllowedKeyIds = [],
                ModelOverride = null,
                RoutingChainId = config.RoutingChains.FirstOrDefault()?.Id,
                Strategy = SelectionStrategy.PriorityThenLru,
                ModelMode = ModelMode.RespectUser,
                Enabled = true
            };

            return config with { LaunchProfiles = profiles };
        }

        if (config.LaunchProfiles.Any(profile => profile.Enabled))
        {
            return config;
        }

        var provider = config.Providers.FirstOrDefault(candidate => candidate.Enabled
            && config.Keys.Any(key => key.Enabled && key.ProviderId == candidate.Id)
            && HasModel(config, candidate));
        if (provider is null)
        {
            return config;
        }

        var profile = new LaunchProfile
        {
            Id = DefaultPresetId,
            Name = "Default",
            ProviderIds = [],
            ModelOverride = null,
            RoutingChainId = config.RoutingChains.FirstOrDefault()?.Id,
            IsDefault = true,
            Strategy = SelectionStrategy.PriorityThenLru,
            ModelMode = ModelMode.RespectUser
        };

        return config with { LaunchProfiles = [profile] };
    }

    private static bool HasModel(ManagerConfig config, ProviderRecord provider) =>
        !string.IsNullOrWhiteSpace(provider.DefaultModelId)
        || config.Models.Any(model => model.Enabled && model.ProviderId == provider.Id);
}
