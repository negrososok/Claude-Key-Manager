using AerolinkManager.Core.Models;
using AerolinkManager.Core.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Routing;

[TestClass]
public class RouteStrategiesTests
{
    private static RouteCandidate MakeCandidate(
        string keyId, 
        int priority, 
        DateTimeOffset? lastUsedAt = null, 
        int windowRequestCount = 0,
        long? rateLimitTokensRemaining = null,
        DateTimeOffset? rateLimitResetAt = null,
        decimal inputPerMillion = 0m)
    {
        var key = new ApiKeyRecord
        {
            Id = Guid.Parse(keyId),
            ProviderId = "test-provider",
            ApiKeyEncrypted = "enc",
            Name = $"Key-{keyId}",
            Priority = priority,
            LastUsedAt = lastUsedAt,
            AddedOrder = priority
        };

        var runtime = new KeyRuntime
        {
            Key = key,
            WindowRequestCount = windowRequestCount,
            RateLimitTokensRemaining = rateLimitTokensRemaining,
            RateLimitResetAt = rateLimitResetAt
        };

        return new RouteCandidate
        {
            Provider = new ProviderRecord { Id = "test-provider", Name = "Test" },
            KeyRuntime = runtime,
            Model = "test-model",
            Pricing = new ModelPricing 
            { 
                Id = "test-pricing", 
                ProviderId = "test-provider", 
                ModelValue = "test-model",
                InputPerMillion = inputPerMillion
            }
        };
    }

    [TestMethod]
    public void PriorityOrder_OrdersByPriorityThenAddedOrder()
    {
        var candidates = new[]
        {
            MakeCandidate("00000000-0000-0000-0000-000000000002", priority: 2),
            MakeCandidate("00000000-0000-0000-0000-000000000001", priority: 1),
            MakeCandidate("00000000-0000-0000-0000-000000000003", priority: 3)
        };

        var ordered = RouteStrategies.Order(SelectionStrategy.PriorityOrder, candidates);

        Assert.AreEqual(3, ordered.Count);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), ordered[0].KeyId);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000002"), ordered[1].KeyId);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000003"), ordered[2].KeyId);
    }

    [TestMethod]
    public void PriorityThenLru_PrioritizesUnused_ThenOldestUsed()
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = new[]
        {
            MakeCandidate("00000000-0000-0000-0000-000000000002", priority: 1, lastUsedAt: now.AddMinutes(-5)), // Oldest used
            MakeCandidate("00000000-0000-0000-0000-000000000001", priority: 1, lastUsedAt: now.AddMinutes(-1)), // Recently used
            MakeCandidate("00000000-0000-0000-0000-000000000003", priority: 1, lastUsedAt: null)                 // Never used
        };

        var ordered = RouteStrategies.Order(SelectionStrategy.PriorityThenLru, candidates);

        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000003"), ordered[0].KeyId); // Never used is best
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000002"), ordered[1].KeyId); // Oldest is second
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), ordered[2].KeyId); // Newest is last
    }

    [TestMethod]
    public void LeastUsed_OrdersByWindowRequestCount()
    {
        var candidates = new[]
        {
            MakeCandidate("00000000-0000-0000-0000-000000000002", priority: 1, windowRequestCount: 50),
            MakeCandidate("00000000-0000-0000-0000-000000000001", priority: 1, windowRequestCount: 10),
            MakeCandidate("00000000-0000-0000-0000-000000000003", priority: 1, windowRequestCount: 100)
        };

        var ordered = RouteStrategies.Order(SelectionStrategy.LeastUsed, candidates);

        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), ordered[0].KeyId); // 10
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000002"), ordered[1].KeyId); // 50
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000003"), ordered[2].KeyId); // 100
    }

    [TestMethod]
    public void RoundRobin_RotatesByCursor()
    {
        var candidates = new[]
        {
            MakeCandidate("00000000-0000-0000-0000-000000000001", priority: 1), // AddedOrder=1
            MakeCandidate("00000000-0000-0000-0000-000000000002", priority: 2), // AddedOrder=2
            MakeCandidate("00000000-0000-0000-0000-000000000003", priority: 3)  // AddedOrder=3
        };

        var ordered0 = RouteStrategies.Order(SelectionStrategy.RoundRobin, candidates, roundRobinCursor: 0);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), ordered0[0].KeyId);

        var ordered1 = RouteStrategies.Order(SelectionStrategy.RoundRobin, candidates, roundRobinCursor: 1);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000002"), ordered1[0].KeyId);

        var ordered2 = RouteStrategies.Order(SelectionStrategy.RoundRobin, candidates, roundRobinCursor: 2);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000003"), ordered2[0].KeyId);

        var ordered3 = RouteStrategies.Order(SelectionStrategy.RoundRobin, candidates, roundRobinCursor: 3);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), ordered3[0].KeyId);
    }

    [TestMethod]
    public void FillFirst_PrioritizesHighestWindowUsageInSamePriority()
    {
        var candidates = new[]
        {
            MakeCandidate("00000000-0000-0000-0000-000000000002", priority: 1, windowRequestCount: 50),
            MakeCandidate("00000000-0000-0000-0000-000000000001", priority: 1, windowRequestCount: 10)
        };

        var ordered = RouteStrategies.Order(SelectionStrategy.FillFirst, candidates);

        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000002"), ordered[0].KeyId);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), ordered[1].KeyId);
    }

    [TestMethod]
    public void CostOptimized_OrdersByLowestBlendedRate()
    {
        var candidates = new[]
        {
            MakeCandidate("00000000-0000-0000-0000-000000000002", priority: 1, inputPerMillion: 10m),
            MakeCandidate("00000000-0000-0000-0000-000000000001", priority: 1, inputPerMillion: 5m)
        };

        var ordered = RouteStrategies.Order(SelectionStrategy.CostOptimized, candidates);

        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), ordered[0].KeyId);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000002"), ordered[1].KeyId);
    }

    [TestMethod]
    public void ResetAware_OrdersByTokensRemainingThenResetTime()
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = new[]
        {
            MakeCandidate("00000000-0000-0000-0000-000000000001", priority: 1, rateLimitTokensRemaining: 1000, rateLimitResetAt: now.AddSeconds(10)),
            MakeCandidate("00000000-0000-0000-0000-000000000002", priority: 1, rateLimitTokensRemaining: 5000, rateLimitResetAt: now.AddSeconds(30)),
            MakeCandidate("00000000-0000-0000-0000-000000000003", priority: 1, rateLimitTokensRemaining: 5000, rateLimitResetAt: now.AddSeconds(5))
        };

        var ordered = RouteStrategies.Order(SelectionStrategy.ResetAware, candidates);

        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000003"), ordered[0].KeyId);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000002"), ordered[1].KeyId);
        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), ordered[2].KeyId);
    }

    [TestMethod]
    public void Manual_FiltersForExactKeyId()
    {
        var candidates = new[]
        {
            MakeCandidate("00000000-0000-0000-0000-000000000001", priority: 1),
            MakeCandidate("00000000-0000-0000-0000-000000000002", priority: 1)
        };

        var manualKeyId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var ordered = RouteStrategies.Order(SelectionStrategy.ManualKey, candidates, manualKeyId: manualKeyId);

        Assert.AreEqual(1, ordered.Count);
        Assert.AreEqual(manualKeyId, ordered[0].KeyId);
    }
}
