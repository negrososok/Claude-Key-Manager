using System.Text.Json;
using System.Text.Json.Serialization;
using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Configuration;

public sealed class ConfigurationMigrator
{
    private readonly JsonSerializerOptions _options;

    public ConfigurationMigrator(JsonSerializerOptions options)
    {
        _options = options;
    }

    public ManagerConfig ReadAndMigrate(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("schemaVersion", out var version)
            && version.GetInt32() >= ManagerConfig.CurrentSchemaVersion)
        {
            return JsonSerializer.Deserialize<ManagerConfig>(json, _options) ?? new ManagerConfig();
        }

        if (document.RootElement.TryGetProperty("schemaVersion", out version) && version.GetInt32() == 2)
        {
            var stage2 = JsonSerializer.Deserialize<ManagerConfig>(json, _options) ?? new ManagerConfig();
            var defaultProfile = stage2.LaunchProfiles.FirstOrDefault(profile => profile.IsDefault)
                ?? stage2.LaunchProfiles.FirstOrDefault();
            var chainId = "chain-default";
            var chain = new RoutingChain
            {
                Id = chainId,
                Name = "Default Chain",
                Steps =
                [
                    new RoutingChainStep
                    {
                        Order = 1,
                        ProviderIds = defaultProfile?.ProviderIds.ToList() ?? [ProviderPresets.AerolinkId],
                        AllowedKeyIds = defaultProfile?.AllowedKeyIds.ToList() ?? [],
                        ModelOverride = defaultProfile?.ModelOverride,
                        Strategy = defaultProfile?.Strategy ?? SelectionStrategy.PriorityThenLru
                    }
                ]
            };
            var migratedStage3 = stage2 with
            {
                SchemaVersion = ManagerConfig.CurrentSchemaVersion,
                Gateway = new GatewaySettings { RoutingMode = RoutingMode.LocalGateway },
                RoutingChains = [chain],
                LaunchProfiles = stage2.LaunchProfiles.Select(profile => profile with { RoutingChainId = chainId }).ToList()
            };
            var stage2Backup = path + $".v2.backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Copy(path, stage2Backup, overwrite: false);
            return migratedStage3;
        }

        var legacy = JsonSerializer.Deserialize<LegacyManagerConfig>(json, _options) ?? new LegacyManagerConfig();
        var provider = ProviderPresets.Aerolink() with
        {
            Name = string.IsNullOrWhiteSpace(legacy.ProviderName) ? "Aerolink" : legacy.ProviderName,
            BaseUrl = string.IsNullOrWhiteSpace(legacy.BaseUrl) ? ProviderPresets.Aerolink().BaseUrl : legacy.BaseUrl
        };
        var migrated = new ManagerConfig
        {
            RealClaudePath = legacy.RealClaudePath,
            ManagedCommandEnabled = legacy.ManagedCommandEnabled,
            Language = legacy.Language,
            Providers = [provider],
            Keys = legacy.Keys.Select(MigrateKey).ToList()
        };

        var backup = path + $".v1.backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        File.Copy(path, backup, overwrite: false);
        return migrated;
    }

    private static ApiKeyRecord MigrateKey(LegacyApiKeyRecord key) => new()
    {
        Id = key.Id,
        ProviderId = string.IsNullOrWhiteSpace(key.ProviderId) ? ProviderPresets.AerolinkId : key.ProviderId,
        Name = key.Name,
        ApiKeyEncrypted = key.ApiKeyEncrypted,
        Enabled = key.Enabled,
        Priority = 100,
        Status = key.Status,
        LastUsedAt = key.LastUsedAt,
        LastSuccessAt = key.LastSuccessAt,
        LastErrorAt = key.LastErrorAt,
        LastErrorText = key.LastErrorText,
        AddedOrder = key.AddedOrder,
        QuotaState = new KeyQuotaState
        {
            FiveHourResetAt = key.FiveHourResetAt,
            FiveHourResetEstimated = key.FiveHourResetEstimated,
            WeeklyResetAt = key.WeeklyBlockedUntil,
            WeeklyResetUnknown = key.WeeklyBlockedUnknown
        },
        Usage = new KeyUsage { Runs = key.TotalRuns, FailedRuns = key.FailedRuns }
    };

    private sealed record LegacyManagerConfig
    {
        public string ProviderName { get; init; } = "Aerolink";
        public string BaseUrl { get; init; } = "https://capi.aerolink.lat/";
        public string? RealClaudePath { get; init; }
        public bool ManagedCommandEnabled { get; init; }
        public string Language { get; init; } = "en";
        public List<LegacyApiKeyRecord> Keys { get; init; } = [];
    }

    private sealed record LegacyApiKeyRecord
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ProviderId { get; init; } = ProviderPresets.AerolinkId;
        public string ApiKeyEncrypted { get; init; } = string.Empty;
        public bool Enabled { get; init; } = true;
        public KeyStatus Status { get; init; } = KeyStatus.Available;
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset? LastSuccessAt { get; init; }
        public DateTimeOffset? LastErrorAt { get; init; }
        public string? LastErrorText { get; init; }
        public DateTimeOffset? FiveHourResetAt { get; init; }
        public bool FiveHourResetEstimated { get; init; }
        public DateTimeOffset? WeeklyBlockedUntil { get; init; }
        public bool WeeklyBlockedUnknown { get; init; }
        public long TotalRuns { get; init; }
        public long FailedRuns { get; init; }
        public long AddedOrder { get; init; }
    }
}

public static class LegacyAppDataMigrator
{
    public static void CopyIfNeeded(AppPaths paths)
    {
        if (Directory.Exists(paths.RootDirectory)
            || string.IsNullOrWhiteSpace(paths.LegacyRootDirectory)
            || !Directory.Exists(paths.LegacyRootDirectory))
        {
            return;
        }

        var parent = Path.GetDirectoryName(paths.RootDirectory)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(paths.RootDirectory)}.migration-{Guid.NewGuid():N}");
        CopyDirectory(paths.LegacyRootDirectory, temporary);
        try
        {
            if (!Directory.Exists(paths.RootDirectory)) Directory.Move(temporary, paths.RootDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(paths.RootDirectory))
            {
                CopyDirectory(temporary, paths.RootDirectory);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            }
            catch
            {
                // A stale temp copy is less harmful than failing app startup.
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
