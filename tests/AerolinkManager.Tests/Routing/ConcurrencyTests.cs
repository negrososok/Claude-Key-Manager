using AerolinkManager.Core.Models;
using AerolinkManager.Core.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Routing;

[TestClass]
public class ConcurrencyTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ParallelAttempts_DoNotHammerLimitedKey()
    {
        // This test simulates the Gateway behavior: 
        // 10 concurrent requests arrive. They all evaluate RoutePlanner and select Key1.
        // They all try to lease Key1.
        // The first one gets the lease, makes the "HTTP call", fails with 429, and marks Key1 as limited.
        // The subsequent 9 requests acquire the lease sequentially, 
        // but BEFORE making the HTTP call, they check the LIVE state of Key1.
        // Because Key1 is now limited, they must abort/fallback immediately rather than hitting the API again.
        
        var keyId = Guid.NewGuid();
        var keyRecord = new ApiKeyRecord { Id = keyId, ProviderId = "p1", Name = "k1", ApiKeyEncrypted = "enc" };
        
        using var manager = new KeyLeaseManager(NoJitterProvider.Instance);

        int actualHttpCalls = 0;
        int fallbackAbortions = 0;

        async Task ProcessRequestSimulatedGateway()
        {
            // Lock the key (simulating what the Wrapper will do)
            await manager.LeaseAsync(keyId, async () =>
            {
                // INSIDE the lock, we must check if another thread just killed this key.
                if (keyRecord.Status == KeyStatus.Limited || keyRecord.Status == KeyStatus.FiveHourLimited)
                {
                    // State mutation was atomic, and we caught it!
                    Interlocked.Increment(ref fallbackAbortions);
                    return true; 
                }

                // Make the "HTTP Request"
                Interlocked.Increment(ref actualHttpCalls);
                await Task.Delay(10); // Simulate network latency

                // Simulate getting a 429 Rate Limit from Claude
                keyRecord = keyRecord with 
                { 
                    Status = KeyStatus.FiveHourLimited, 
                    QuotaState = new KeyQuotaState { FiveHourResetAt = Now.AddHours(5) } 
                };

                return true;
            });
        }

        // Fire 10 concurrent requests
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(ProcessRequestSimulatedGateway));
        }

        await Task.WhenAll(tasks);

        // Only ONE request should have actually made the HTTP call
        Assert.AreEqual(1, actualHttpCalls, "Only the first request should hit the API");
        
        // The other 9 requests should have aborted immediately due to catching the mutated state
        Assert.AreEqual(9, fallbackAbortions, "Subsequent requests must abort inside the lease");
        
        // The key must be limited
        Assert.AreEqual(KeyStatus.FiveHourLimited, keyRecord.Status);
    }
}
