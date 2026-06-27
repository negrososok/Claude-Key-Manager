using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Selection;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class LaunchPresetProvisionerTests
{
    [TestMethod]
    public void EnsureDefaultPreset_CreatesPreset_WhenReadyProviderKeyAndModelExist()
    {
        var provider = ProviderPresets.Custom("p", "Provider") with { BaseUrl = "https://example.test", DefaultModelId = "model-1" };
        var key = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = provider.Id, Name = "Key", ApiKeyEncrypted = "enc" };
        var config = new ManagerConfig
        {
            Providers = [provider],
            Keys = [key],
            Models = [],
            LaunchProfiles = []
        };

        var updated = new LaunchPresetProvisioner().EnsureDefaultPreset(config);

        Assert.AreEqual(1, updated.LaunchProfiles.Count);
        Assert.AreEqual("default", updated.LaunchProfiles[0].Id);
        Assert.IsTrue(updated.LaunchProfiles[0].IsDefault);
        Assert.AreEqual(0, updated.LaunchProfiles[0].ProviderIds.Count);
        Assert.IsNull(updated.LaunchProfiles[0].ModelOverride);
        Assert.AreEqual(ModelMode.RespectUser, updated.LaunchProfiles[0].ModelMode);
    }

    [TestMethod]
    public void EnsureDefaultPreset_DoesNotCreatePreset_WhenModelIsMissing()
    {
        var provider = ProviderPresets.Custom("p", "Provider") with { BaseUrl = "https://example.test" };
        var key = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = provider.Id, Name = "Key", ApiKeyEncrypted = "enc" };
        var config = new ManagerConfig
        {
            Providers = [provider],
            Keys = [key],
            Models = [],
            LaunchProfiles = []
        };

        var updated = new LaunchPresetProvisioner().EnsureDefaultPreset(config);

        Assert.AreEqual(0, updated.LaunchProfiles.Count);
    }

    [TestMethod]
    public void EnsureDefaultPreset_PreservesExistingUsablePreset()
    {
        var existing = new LaunchProfile { Id = "custom", Name = "Custom", Enabled = true, IsDefault = true };
        var config = new ManagerConfig { LaunchProfiles = [existing] };

        var updated = new LaunchPresetProvisioner().EnsureDefaultPreset(config);

        Assert.AreEqual("custom", updated.LaunchProfiles.Single().Id);
    }

    [TestMethod]
    public void EnsureDefaultPreset_NormalizesAutoDefault_ToUseWholePool()
    {
        var pinned = new LaunchProfile
        {
            Id = "default",
            Name = "Default",
            ProviderIds = ["old-provider"],
            AllowedKeyIds = [Guid.NewGuid()],
            ModelOverride = "old-model",
            RoutingChainId = "chain-default",
            IsDefault = true,
            ModelMode = ModelMode.ForceProfile
        };
        var config = new ManagerConfig { LaunchProfiles = [pinned] };

        var updated = new LaunchPresetProvisioner().EnsureDefaultPreset(config);
        var profile = updated.LaunchProfiles.Single();

        Assert.AreEqual("default", profile.Id);
        Assert.AreEqual(0, profile.ProviderIds.Count);
        Assert.AreEqual(0, profile.AllowedKeyIds.Count);
        Assert.IsNull(profile.ModelOverride);
        Assert.AreEqual("chain-default", profile.RoutingChainId);
        Assert.AreEqual(SelectionStrategy.PriorityThenLru, profile.Strategy);
        Assert.AreEqual(ModelMode.RespectUser, profile.ModelMode);
        Assert.IsTrue(profile.Enabled);
    }
}
