using System.Text.Json.Nodes;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Managed;
using AerolinkManager.Core.Models;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class PersistenceAndManagedCommandTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "AerolinkManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void JsonStore_RoundTripsEncryptedKeysAndState()
    {
        var paths = new AppPaths(_root);
        var store = new JsonFileStore(paths);
        var key = new ApiKeyRecord { Id = Guid.NewGuid(), Name = "Account 1", ApiKeyEncrypted = "ciphertext" };

        store.SaveConfig(new ManagerConfig { RealClaudePath = @"C:\claude.exe", Keys = [key] });
        store.SaveState(new ManagerState { CurrentKeyId = key.Id });

        Assert.AreEqual("ciphertext", store.LoadConfig().Keys.Single().ApiKeyEncrypted);
        Assert.AreEqual(key.Id, store.LoadState().CurrentKeyId);
        Assert.IsFalse(File.ReadAllText(paths.ConfigFile).Contains("api-key-plaintext", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SettingsService_PreservesPermissionsAndRemovesConflictingKey()
    {
        var claude = Path.Combine(_root, ".claude");
        Directory.CreateDirectory(claude);
        var settings = Path.Combine(claude, "settings.json");
        File.WriteAllText(settings, """
            { "env": { "ANTHROPIC_API_KEY": "remove-me", "OTHER": "keep" }, "permissions": { "allow": ["Read"] }, "custom": true }
            """);

        new ClaudeSettingsService(_root).Apply();

        var root = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        var environment = root["env"]!.AsObject();
        Assert.IsNull(environment["ANTHROPIC_API_KEY"]);
        Assert.IsNull(environment["ANTHROPIC_BASE_URL"]);
        Assert.IsNull(environment["ANTHROPIC_MODEL"]);
        Assert.AreEqual("keep", environment["OTHER"]!.GetValue<string>());
        Assert.AreEqual("Read", root["permissions"]!["allow"]![0]!.GetValue<string>());
        Assert.IsNull(root["apiKeyHelper"]);
        Assert.IsNull(root["skipDangerousModePermissionPrompt"]);
        Assert.IsTrue(File.Exists(settings + ".bak"));
    }

    [TestMethod]
    public void ManagedCommand_EnableAndDisableAreIdempotent()
    {
        var paths = new AppPaths(_root);
        var pathStore = new FakePathStore(@"C:\Windows;C:\Claude");
        var service = new ManagedCommandService(paths, pathStore);
        var wrapper = Path.Combine(_root, "AerolinkManager.Wrapper.exe");
        File.WriteAllText(wrapper, string.Empty);

        service.Enable(wrapper);
        service.Enable(wrapper);

        Assert.AreEqual(1, pathStore.Value!.Split(';').Count(entry => string.Equals(entry, paths.BinDirectory, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(pathStore.Value.StartsWith(paths.BinDirectory, StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(File.ReadAllText(paths.ManagedCommandFile), $"\"{wrapper}\" %*");

        service.Disable();
        service.Disable();
        Assert.IsFalse(pathStore.Value!.Contains(paths.BinDirectory, StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(pathStore.Value, @"C:\Claude");
    }

    private sealed class FakePathStore(string? value) : IUserPathStore
    {
        public string? Value { get; private set; } = value;
        public string? Get() => Value;
        public void Set(string updated) => Value = updated;
    }
}
