using System.Text.Json;
using System.Text.Json.Serialization;

namespace AerolinkManager.Core.Configuration;

public sealed class ConfigurationTransferService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public void Export(ManagerConfig config, string destination)
    {
        ArgumentNullException.ThrowIfNull(config);
        Validate(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.WriteAllText(destination, JsonSerializer.Serialize(config, Options));
    }

    public ManagerConfig Import(string source)
    {
        var config = JsonSerializer.Deserialize<ManagerConfig>(File.ReadAllText(source), Options)
            ?? throw new InvalidDataException("The configuration file is empty.");
        Validate(config);
        return config with { SchemaVersion = ManagerConfig.CurrentSchemaVersion };
    }

    public static void Validate(ManagerConfig config)
    {
        if (config.SchemaVersion != ManagerConfig.CurrentSchemaVersion)
            throw new InvalidDataException($"Only schema v{ManagerConfig.CurrentSchemaVersion} can be imported.");
        if (config.Providers.Count == 0 || config.Providers.Any(provider => string.IsNullOrWhiteSpace(provider.Id) || string.IsNullOrWhiteSpace(provider.Name)))
            throw new InvalidDataException("At least one valid provider is required.");
        if (config.Providers.GroupBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("Provider IDs must be unique.");
        if (config.Providers.Any(provider => provider.CustomHeaders.Keys.Any(AerolinkManager.Core.Models.ProviderHeaderRules.IsProtected)))
            throw new InvalidDataException("Provider custom headers must not override authorization, content, host, or local gateway headers.");
        if (config.Keys.Any(key => string.IsNullOrWhiteSpace(key.ApiKeyEncrypted) || config.Providers.All(provider => provider.Id != key.ProviderId)))
            throw new InvalidDataException("Every key must be encrypted and reference an existing provider.");
        if (config.Models.Any(model => config.Providers.All(provider => provider.Id != model.ProviderId)))
            throw new InvalidDataException("Every model must reference an existing provider.");
        if (config.LaunchProfiles.Count == 0
            || config.LaunchProfiles.Any(profile => profile.ProviderIds.Any(id => config.Providers.All(provider => provider.Id != id))
                || profile.AllowedKeyIds.Any(id => config.Keys.All(key => key.Id != id))
                || profile.Strategy == AerolinkManager.Core.Models.SelectionStrategy.ManualKey && (profile.ManualKeyId is null || profile.AllowedKeyIds.Count != 1)))
            throw new InvalidDataException("Launch profiles contain invalid provider or key references.");
        if (config.LaunchProfiles.Count(profile => profile.IsDefault && profile.Enabled) > 1)
            throw new InvalidDataException("Only one enabled launch profile can be the default.");
        foreach (var profile in config.LaunchProfiles)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { profile.Id };
            var fallback = profile.FallbackProfileId;
            while (!string.IsNullOrWhiteSpace(fallback))
            {
                if (!visited.Add(fallback)) throw new InvalidDataException("Launch profile fallback cycle detected.");
                var next = config.LaunchProfiles.FirstOrDefault(candidate => candidate.Id == fallback)
                    ?? throw new InvalidDataException("Launch profile fallback target does not exist.");
                fallback = next.FallbackProfileId;
            }
        }
    }
}
