using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Presentation;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class PresentationAndConcurrencyTests
{
    [TestMethod]
    public void Formatter_ShowsExactEstimatedAndUnknownResetsHonestly()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var key = NewKey();
        Assert.AreEqual("resets in 3h 32m", KeyStatusFormatter.Reset(key with
        {
            Status = KeyStatus.FiveHourLimited,
            FiveHourResetAt = now.AddHours(3).AddMinutes(32)
        }, now));
        Assert.AreEqual("estimated reset in 5h 0m", KeyStatusFormatter.Reset(key with
        {
            Status = KeyStatus.FiveHourLimited,
            FiveHourResetAt = now.AddHours(5),
            FiveHourResetEstimated = true
        }, now));
        Assert.AreEqual("weekly limit, reset unknown", KeyStatusFormatter.Reset(key with
        {
            Status = KeyStatus.WeeklyLimited,
            WeeklyBlockedUnknown = true
        }, now));
    }

    [TestMethod]
    public void Formatter_MasksKeyWithoutReturningPlaintext()
    {
        const string secret = "sk-ant-test-value-AbC9";
        var mask = KeyStatusFormatter.Mask(secret);
        Assert.AreEqual("sk-...AbC9", mask);
        Assert.AreNotEqual(secret, mask);
    }

    [TestMethod]
    public async Task JsonStore_SerializesConcurrentReadModifyWriteUpdates()
    {
        var root = Path.Combine(Path.GetFullPath("."), "TestResults", "concurrency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonFileStore(new AppPaths(root));
            store.SaveConfig(new ManagerConfig { Keys = [NewKey()] });

            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
                store.UpdateConfig(config => config with
                {
                    Keys = config.Keys.Select(key => key with { TotalRuns = key.TotalRuns + 1 }).ToList()
                }))));

            Assert.AreEqual(20, store.LoadConfig().Keys.Single().TotalRuns);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void JsonStore_BacksUpMalformedFileAndRecoversWithDefaults()
    {
        var root = Path.Combine(Path.GetFullPath("."), "TestResults", "malformed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            File.WriteAllText(paths.ConfigFile, "{ broken json");

            var config = new JsonFileStore(paths).LoadConfig();

            Assert.AreEqual(0, config.Keys.Count);
            Assert.AreEqual(1, Directory.GetFiles(root, "config.json.corrupt-*").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ApiKeyRecord NewKey() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Account",
        ApiKeyEncrypted = "ciphertext"
    };
}
