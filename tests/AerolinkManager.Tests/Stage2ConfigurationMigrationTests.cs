using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Models;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class Stage2ConfigurationMigrationTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClaudeManagerMigrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void LoadConfig_MigratesV1WithoutChangingCiphertextAndCreatesBackup()
    {
        var paths = new AppPaths(_root);
        File.WriteAllText(paths.ConfigFile, """
            {
              "schemaVersion": 1,
              "providerName": "Aerolink Legacy",
              "baseUrl": "https://legacy.example/",
              "realClaudePath": "C:\\tools\\claude.exe",
              "managedCommandEnabled": true,
              "language": "uk",
              "keys": [{
                "id": "11111111-1111-1111-1111-111111111111",
                "name": "Legacy key",
                "providerId": "aerolink",
                "apiKeyEncrypted": "unchanged-ciphertext",
                "enabled": true,
                "status": "five_hour_limited",
                "fiveHourResetAt": "2026-06-22T15:00:00+00:00",
                "fiveHourResetEstimated": true,
                "totalRuns": 12,
                "failedRuns": 2
              }]
            }
            """);

        var migrated = new JsonFileStore(paths).LoadConfig();

        Assert.AreEqual(3, migrated.SchemaVersion);
        Assert.AreEqual("unchanged-ciphertext", migrated.Keys.Single().ApiKeyEncrypted);
        Assert.AreEqual(12, migrated.Keys.Single().Usage.Runs);
        Assert.IsTrue(migrated.Keys.Single().QuotaState.FiveHourResetEstimated);
        Assert.AreEqual("https://legacy.example/", migrated.Providers.Single(p => p.Id == "aerolink").BaseUrl);
        Assert.AreEqual(1, Directory.GetFiles(_root, "config.json.v1.backup-*").Length);
        StringAssert.Contains(File.ReadAllText(paths.ConfigFile), "\"schemaVersion\": 3");
    }

    [TestMethod]
    public void LoadConfig_MigratesStage2ToStage3PreservingEntitiesAndGatewayDefault()
    {
        var paths = new AppPaths(_root);
        var store = new JsonFileStore(paths);
        var key = new ApiKeyRecord { Id = Guid.NewGuid(), Name = "Stage 2 key", ApiKeyEncrypted = "same-ciphertext" };
        var model = new ModelRecord { Id = "model", ProviderId = "aerolink", DisplayName = "Model", ModelValue = "claude-model" };
        store.SaveConfig(new ManagerConfig { Keys = [key], Models = [model] });
        File.WriteAllText(paths.ConfigFile, File.ReadAllText(paths.ConfigFile).Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2", StringComparison.Ordinal));

        var migrated = store.LoadConfig();

        Assert.AreEqual(3, migrated.SchemaVersion);
        Assert.AreEqual("same-ciphertext", migrated.Keys.Single().ApiKeyEncrypted);
        Assert.AreEqual("claude-model", migrated.Models.Single().ModelValue);
        Assert.AreEqual(RoutingMode.LocalGateway, migrated.Gateway.RoutingMode);
        Assert.AreEqual("chain-default", migrated.LaunchProfiles.Single(profile => profile.IsDefault).RoutingChainId);
        Assert.AreEqual(1, Directory.GetFiles(_root, "config.json.v2.backup-*").Length);
    }

    [TestMethod]
    public void Store_CopiesLegacyDirectoryOnlyWhenNewDirectoryDoesNotExist()
    {
        var legacy = Path.Combine(_root, "AerolinkManager");
        var current = Path.Combine(_root, "ClaudeManager");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "state.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "logs", "legacy.log"), "kept");

        _ = new JsonFileStore(new AppPaths(current, legacy));

        Assert.IsTrue(File.Exists(Path.Combine(current, "state.json")));
        Assert.AreEqual("kept", File.ReadAllText(Path.Combine(current, "logs", "legacy.log")));
        Assert.IsTrue(Directory.Exists(legacy));
    }

    [TestMethod]
    public void FreshConfig_StartsWithoutBundledProviders()
    {
        var config = new ManagerConfig();

        Assert.AreEqual(0, config.Providers.Count);
        Assert.AreEqual(0, config.LaunchProfiles.Single(profile => profile.IsDefault).ProviderIds.Count);
        Assert.AreEqual(0, config.RoutingChains.Single(chain => chain.Id == "chain-default").Steps.Single().ProviderIds.Count);
        Assert.AreEqual(QuotaPolicyType.Composite, config.QuotaPolicies.Single(p => p.Id == "aerolink-composite").Type);
    }
}
