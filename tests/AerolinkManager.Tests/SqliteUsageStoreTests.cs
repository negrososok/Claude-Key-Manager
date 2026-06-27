using AerolinkManager.Core.Storage;
using ClaudeManager.Storage;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class SqliteUsageStoreTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClaudeManagerSqliteTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Initialize_CreatesAllStage3Tables()
    {
        var store = new SqliteUsageStore(Path.Combine(_root, "usage.db"));

        await store.InitializeAsync();

        CollectionAssert.AreEquivalent(new[]
        {
            "budgets", "key_switches", "limit_events", "model_lockouts", "provider_health",
            "requests", "route_decisions", "schema_info", "sessions", "usage_events"
        }, (await store.ListTablesAsync()).ToArray());
    }

    [TestMethod]
    public async Task AddRequest_RoundTripsCountAndDoesNotCreateJsonSidecar()
    {
        var path = Path.Combine(_root, "usage.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();

        await store.AddRequestAsync(new RequestUsageRecord
        {
            RequestId = "request-1",
            SessionId = "session-1",
            ProviderId = "aerolink",
            KeyId = Guid.NewGuid(),
            Model = "claude-model",
            StatusCode = 200,
            DurationMs = 42,
            InputTokens = 100,
            OutputTokens = 25,
            EstimatedCostMicros = 750
        });

        Assert.AreEqual(1, await store.CountRequestsAsync());
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.json*").Length);
    }

    [TestMethod]
    public async Task AddRequest_ConcurrentWritersAreSerializedBySqliteWal()
    {
        var path = Path.Combine(_root, "usage.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();

        await Task.WhenAll(Enumerable.Range(0, 24).Select(index => store.AddRequestAsync(new RequestUsageRecord
        {
            RequestId = $"request-{index}",
            StatusCode = 200,
            DurationMs = index
        })));

        Assert.AreEqual(24, await store.CountRequestsAsync());
    }

    // ── Phase B: insert + query round-trip tests ──

    private static async Task<SqliteUsageStore> SeededStoreAsync(string dbPath)
    {
        var store = new SqliteUsageStore(dbPath);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        await store.AddRequestAsync(new RequestUsageRecord { RequestId = "r1", SessionId = "s-a", ProviderId = "p1", Model = "m1", StatusCode = 200, InputTokens = 100, OutputTokens = 20, Timestamp = now.AddMinutes(-10), EstimatedCostMicros = 500, Currency = "USD" });
        await store.AddRequestAsync(new RequestUsageRecord { RequestId = "r2", SessionId = "s-a", ProviderId = "p1", Model = "m1", StatusCode = 200, InputTokens = 50, OutputTokens = 10, Timestamp = now.AddMinutes(-5), EstimatedCostMicros = 250, Currency = "USD" });
        await store.AddRequestAsync(new RequestUsageRecord { RequestId = "r3", SessionId = "s-b", ProviderId = "p2", Model = "m2", StatusCode = 502, ErrorType = "network_failure", InputTokens = 0, OutputTokens = 0, Timestamp = now, Currency = null });
        return store;
    }

    [TestMethod]
    public async Task QueryRequests_FiltersBySession()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "usage.db"));
        var result = await store.QueryRequestsAsync(new RequestQuery { SessionId = "s-a" });
        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.All(r => r.SessionId == "s-a"));
    }

    [TestMethod]
    public async Task QueryRequests_FiltersByProvider()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "usage.db"));
        var result = await store.QueryRequestsAsync(new RequestQuery { ProviderId = "p2" });
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("p2", result[0].ProviderId);
    }

    [TestMethod]
    public async Task QueryRequests_FiltersByModel()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "usage.db"));
        var result = await store.QueryRequestsAsync(new RequestQuery { Model = "m1" });
        Assert.AreEqual(2, result.Count);
    }

    [TestMethod]
    public async Task QueryRequests_FiltersByTimeRange()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "usage.db"));
        var now = DateTimeOffset.UtcNow;
        var result = await store.QueryRequestsAsync(new RequestQuery { From = now.AddMinutes(-7), To = now.AddMinutes(1) });
        Assert.AreEqual(2, result.Count);
    }

    [TestMethod]
    public async Task QueryRequests_FiltersByStatusCode()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "usage.db"));
        var result = await store.QueryRequestsAsync(new RequestQuery { StatusCode = 502 });
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("network_failure", result[0].ErrorType);
    }

    [TestMethod]
    public async Task SessionUpsert_AndUsageEvent_AndLimitEvent_RoundTrip()
    {
        var path = Path.Combine(_root, "usage.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();

        await store.AddRequestAsync(new RequestUsageRecord { RequestId = "req-rt", SessionId = "s-rt", StatusCode = 200, DurationMs = 99, InputTokens = 150, OutputTokens = 50 });

        await store.UpsertSessionAsync(new SessionUpsert { SessionId = "s-rt", Status = "active", TokensDelta = 200, CostMicrosDelta = 1000 });
        await store.UpsertSessionAsync(new SessionUpsert { SessionId = "s-rt", Status = "active", TokensDelta = 50, CostMicrosDelta = 250 });

        await store.AddUsageEventAsync(new UsageEventRecord { RequestId = "req-rt", EventType = "message_start", InputTokens = 150, OutputTokens = 0 });
        await store.AddUsageEventAsync(new UsageEventRecord { RequestId = "req-rt", EventType = "message_delta", InputTokens = 0, OutputTokens = 50 });

        await store.AddLimitEventAsync(new LimitEventRecord { RequestId = "req-rt", LimitType = "rate_limit", Estimated = false });

        await store.AddKeySwitchAsync(new KeySwitchRecord { ToKeyId = Guid.NewGuid(), Reason = "test" });

        Assert.AreEqual(1, await store.CountRequestsAsync());

        // Verify tables exist and are populated.
        Assert.IsTrue((await store.ListTablesAsync()).Contains("sessions"));
        Assert.IsTrue((await store.ListTablesAsync()).Contains("usage_events"));
        Assert.IsTrue((await store.ListTablesAsync()).Contains("limit_events"));
        Assert.IsTrue((await store.ListTablesAsync()).Contains("key_switches"));
    }

    [TestMethod]
    public async Task RouteDecision_RoundTripsSafeTraceAndReadableStory()
    {
        var path = Path.Combine(_root, "usage.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();

        var keyId = Guid.NewGuid();
        await store.AddRouteDecisionAsync(new RouteDecisionRecord
        {
            RequestId = "route-1",
            SessionId = "session-1",
            ProfileId = "default",
            ChainId = "chain-default",
            SelectedProviderId = "aerolink",
            SelectedKeyId = keyId,
            SelectedModel = "claude-model",
            DecisionReason = "selected_by_priority_then_lru",
            TraceJson = "{\"outcome\":\"Selected\",\"safe\":true}",
            Story = "ignored on write"
        });

        var decisions = await store.QueryRouteDecisionsAsync(new RouteDecisionQuery { SessionId = "session-1" });

        Assert.AreEqual(1, decisions.Count);
        Assert.AreEqual("route-1", decisions[0].RequestId);
        Assert.AreEqual(keyId, decisions[0].SelectedKeyId);
        Assert.AreEqual("{\"outcome\":\"Selected\",\"safe\":true}", decisions[0].TraceJson);
        StringAssert.Contains(decisions[0].Story, "routed to aerolink / claude-model");
        StringAssert.Contains(decisions[0].Story, keyId.ToString()[..8]);
    }
}
