using AerolinkManager.Core.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Routing;

[TestClass]
public class KeyLeaseManagerTests
{
    [TestMethod]
    public async Task LeaseAsync_RunsActionAndReleasesLock()
    {
        using var manager = new KeyLeaseManager(NoJitterProvider.Instance);
        var keyId = Guid.NewGuid();

        var ran = false;
        await manager.LeaseAsync(keyId, () =>
        {
            ran = true;
            Assert.IsTrue(manager.IsHeld(keyId));
            return Task.FromResult(true);
        });

        Assert.IsTrue(ran);
        Assert.IsFalse(manager.IsHeld(keyId));
    }

    [TestMethod]
    public async Task LeaseAsync_AppliesJitter()
    {
        var jitterProvider = new FixedJitterProvider(TimeSpan.FromMilliseconds(50));
        using var manager = new KeyLeaseManager(jitterProvider, TimeSpan.FromMilliseconds(100));
        var keyId = Guid.NewGuid();

        TimeSpan? appliedDelay = null;
        Task MockDelay(TimeSpan delay, CancellationToken ct)
        {
            appliedDelay = delay;
            return Task.CompletedTask;
        }

        await manager.LeaseAsync(keyId, () => Task.FromResult(true), MockDelay);

        Assert.AreEqual(TimeSpan.FromMilliseconds(50), appliedDelay);
    }

    [TestMethod]
    public async Task LeaseAsync_UnrelatedKeys_AreNotSerialized()
    {
        using var manager = new KeyLeaseManager(NoJitterProvider.Instance);
        var key1 = Guid.NewGuid();
        var key2 = Guid.NewGuid();

        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();

        var task1 = manager.LeaseAsync(key1, async () =>
        {
            await tcs1.Task;
            return true;
        });

        var task2 = manager.LeaseAsync(key2, async () =>
        {
            await tcs2.Task;
            return true;
        });

        await Task.Yield();

        Assert.IsTrue(manager.IsHeld(key1));
        Assert.IsTrue(manager.IsHeld(key2));

        tcs1.SetResult();
        tcs2.SetResult();

        await Task.WhenAll(task1, task2);

        Assert.IsFalse(manager.IsHeld(key1));
        Assert.IsFalse(manager.IsHeld(key2));
    }

    private class FixedJitterProvider : IJitterProvider
    {
        private readonly TimeSpan _jitter;
        public FixedJitterProvider(TimeSpan jitter) => _jitter = jitter;
        public TimeSpan Next(TimeSpan max) => _jitter;
    }
}
