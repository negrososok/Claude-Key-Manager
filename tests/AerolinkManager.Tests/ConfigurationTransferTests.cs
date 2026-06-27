using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Models;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class ConfigurationTransferTests
{
    [TestMethod]
    public void ExportImport_RoundTripsEncryptedValueWithoutPlaintextTransformation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"claude-manager-export-{Guid.NewGuid():N}.json");
        try
        {
            var provider = ProviderPresets.Custom("provider", "Provider");
            var config = new ManagerConfig
            {
                Providers = [provider],
                Keys = [new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = provider.Id, Name = "key", ApiKeyEncrypted = "dpapi-ciphertext" }],
                LaunchProfiles = [SafeProfile()]
            };
            var service = new ConfigurationTransferService();

            service.Export(config, path);
            var imported = service.Import(path);

            Assert.AreEqual("dpapi-ciphertext", imported.Keys.Single().ApiKeyEncrypted);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void Import_RejectsKeyWithUnknownProvider()
    {
        var invalid = new ManagerConfig { Keys = [new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = "missing", Name = "key", ApiKeyEncrypted = "cipher" }] };

        Assert.ThrowsException<InvalidDataException>(() => ConfigurationTransferService.Validate(invalid));
    }

    [TestMethod]
    public void Validate_RejectsProviderCustomHeaderOverridingProtectedName()
    {
        var provider = ProviderPresets.Custom("hdrtest", "Header Test") with
        {
            CustomHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer x" }
        };
        var invalid = new ManagerConfig { Providers = [provider] };

        Assert.ThrowsException<InvalidDataException>(() => ConfigurationTransferService.Validate(invalid));
    }

    [TestMethod]
    public void Validate_AllowsProviderWithSafeCustomHeader()
    {
        var provider = ProviderPresets.Custom("hdrtest", "Header Test") with
        {
            CustomHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-tenant"] = "acme" }
        };
        var config = new ManagerConfig { Providers = [provider], LaunchProfiles = [SafeProfile()] };

        ConfigurationTransferService.Validate(config); // must not throw
    }

    private static LaunchProfile SafeProfile() => new()
    {
        Id = "default",
        Name = "Default",
        ProviderIds = [],
        IsDefault = true
    };

    [TestMethod]
    public void ExportImport_RoundTripsProviderCustomHeaders()
    {
        var path = Path.Combine(Path.GetTempPath(), $"claude-manager-hdr-{Guid.NewGuid():N}.json");
        try
        {
            var provider = ProviderPresets.Custom("hdrtest", "Header Test") with
            {
                CustomHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-tenant"] = "acme", ["x-region"] = "eu" }
            };
            var config = new ManagerConfig { Providers = [provider], LaunchProfiles = [SafeProfile()] };
            var service = new ConfigurationTransferService();

            service.Export(config, path);
            var imported = service.Import(path);

            var roundTripped = imported.Providers.Single(p => p.Id == "hdrtest");
            Assert.AreEqual("acme", roundTripped.CustomHeaders["x-tenant"]);
            Assert.AreEqual("eu", roundTripped.CustomHeaders["x-region"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ProviderHeaderRules_FlagsProtectedNames()
    {
        Assert.IsTrue(ProviderHeaderRules.IsProtected("Authorization"));
        Assert.IsTrue(ProviderHeaderRules.IsProtected("x-api-key"));
        Assert.IsTrue(ProviderHeaderRules.IsProtected("Content-Type"));
        Assert.IsTrue(ProviderHeaderRules.IsProtected("host"));
        Assert.IsTrue(ProviderHeaderRules.IsProtected("x-claude-manager-token"));
        Assert.IsTrue(ProviderHeaderRules.IsProtected("   "));
        Assert.IsFalse(ProviderHeaderRules.IsProtected("x-tenant"));
        Assert.IsFalse(ProviderHeaderRules.IsProtected("x-custom-routing"));
    }
}
