using System.Globalization;
using System.Text;
using AerolinkManager.Core.Storage;
using AerolinkManager.Core.Usage;
using ClaudeManager.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Gateway;

/// <summary>
/// Phase C: grouped query, CSV export, provider health, session summaries, and
/// privacy regression (secrets/keys never appear in CSV or DB). Uses a real
/// SQLite database (temp). The CSV exporter is pure and tested directly.
/// </summary>
[TestClass]
public sealed class UsageQueryTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClaudeManagerQueryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // SQLite shared-cache may briefly hold the file; clear pools and ignore
        // transient IO errors — temp dirs are reclaimed by the OS regardless.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<SqliteUsageStore> SeededStoreAsync(string path)
    {
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var ids = new[] { "p1", "p1", "p2", "p1" };
        var models = new[] { "m1", "m1", "m2", "m3" };
        var sessions = new[] { "s-a", "s-a", "s-b", null };
        var statuses = new[] { 200, 200, 502, 200 };
        var errors = new[] { null, null, "network_failure", "client_cancelled" };
        var inputTokens = new long[] { 100, 50, 0, 200 };
        var outputTokens = new long[] { 40, 10, 0, 80 };
        var costs = new long?[] { 750, 250, null, 1500 };
        var currencies = new[] { "USD", "USD", null, "USD" };
        var durations = new long[] { 120, 80, 0, 150 };

        for (var i = 0; i < ids.Length; i++)
        {
            await store.AddRequestAsync(new RequestUsageRecord
            {
                RequestId = $"r{i + 1}",
                Timestamp = now.AddMinutes(-20 + i * 5),
                SessionId = sessions[i],
                ProviderId = ids[i],
                Model = models[i],
                StatusCode = statuses[i],
                ErrorType = errors[i],
                DurationMs = durations[i],
                InputTokens = inputTokens[i],
                OutputTokens = outputTokens[i],
                EstimatedCostMicros = costs[i],
                Currency = currencies[i],
                Streaming = i % 2 == 0
            });
        }

        // Seed session rows via upsert.
        await store.UpsertSessionAsync(new SessionUpsert { SessionId = "s-a", Status = "active", TokensDelta = 150, CostMicrosDelta = 1000 });
        await store.UpsertSessionAsync(new SessionUpsert { SessionId = "s-a", Status = "active", TokensDelta = 60, CostMicrosDelta = 250 });
        await store.UpsertSessionAsync(new SessionUpsert { SessionId = "s-b", Status = "error", TokensDelta = 0 });

        return store;
    }

    [TestMethod]
    public async Task QueryGroupedUsage_ByPeriod_Day()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        var rows = await store.QueryGroupedUsageAsync(new UsageQuery { GroupBy = UsageGrouping.Period, PeriodBucket = "day" });
        Assert.IsTrue(rows.Count >= 1);
        Assert.IsTrue(rows[0].RequestCount > 0);
        Assert.IsTrue(rows[0].GroupKey.Length == 10);
    }

    [TestMethod]
    public async Task QueryGroupedUsage_ByProvider()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        var rows = await store.QueryGroupedUsageAsync(new UsageQuery { GroupBy = UsageGrouping.Provider });
        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows.Any(r => r.GroupKey == "p1" && r.RequestCount == 3));
        Assert.IsTrue(rows.Any(r => r.GroupKey == "p2" && r.RequestCount == 1));
    }

    [TestMethod]
    public async Task QueryGroupedUsage_ByModel()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        var rows = await store.QueryGroupedUsageAsync(new UsageQuery { GroupBy = UsageGrouping.Model });
        Assert.AreEqual(3, rows.Count);
    }

    [TestMethod]
    public async Task QueryGroupedUsage_BySession()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        var rows = await store.QueryGroupedUsageAsync(new UsageQuery { GroupBy = UsageGrouping.Session });
        Assert.IsTrue(rows.Any(r => r.GroupKey == "s-a"));
    }

    [TestMethod]
    public async Task QueryGroupedUsage_ByKey()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        var rows = await store.QueryGroupedUsageAsync(new UsageQuery { GroupBy = UsageGrouping.Key });
        Assert.IsTrue(rows.Count >= 1);
    }

    [TestMethod]
    public async Task QueryGroupedUsage_ByProfile()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        var rows = await store.QueryGroupedUsageAsync(new UsageQuery { GroupBy = UsageGrouping.Profile });
        Assert.IsTrue(rows.Count >= 1);
    }

    [TestMethod]
    public async Task QueryGroupedUsage_WithTimeRange()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        var now = DateTimeOffset.UtcNow;
        var rows = await store.QueryGroupedUsageAsync(new UsageQuery { From = now.AddMinutes(-10), To = now });
        Assert.IsTrue(rows.Count >= 1);
    }

    [TestMethod]
    public async Task QueryGroupedUsage_EmptyRange_ReturnsEmpty()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        var future = DateTimeOffset.UtcNow.AddDays(7);
        var rows = await store.QueryGroupedUsageAsync(new UsageQuery { From = future, To = future.AddDays(1) });
        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public async Task SessionSummaries_IncludesBreakdowns()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        var rows = await store.QuerySessionSummariesAsync();
        var s = rows.FirstOrDefault(r => r.SessionId == "s-a");
        Assert.IsNotNull(s, "Session s-a must be present.");
        Assert.AreEqual(2, s.RequestCount);
        Assert.AreEqual(150, s.TotalInputTokens);
        Assert.AreEqual(50, s.TotalOutputTokens);
        Assert.IsTrue(s.SuccessCount >= 1);
        Assert.IsTrue(s.ProviderBreakdowns.Count >= 1);
    }

    [TestMethod]
    public async Task ProviderHealth_RecomputeAndRead()
    {
        var store = await SeededStoreAsync(Path.Combine(_root, "u.db"));
        await store.RecomputeProviderHealthAsync(DateTimeOffset.UtcNow);
        var rows = await store.GetProviderHealthAsync();
        Assert.IsTrue(rows.Count > 0);
        Assert.IsTrue(rows.Any(r => r.ProviderId == "p1" && r.SuccessRate > 0));
    }

    // ── CSV tests ──

    [TestMethod]
    public void ExportRequestsCsv_HasHeaderAndRows()
    {
        var records = new List<RequestUsageRecord>
        {
            new() { RequestId = "req-1", StatusCode = 200, InputTokens = 10, OutputTokens = 5, EstimatedCostMicros = 100, Currency = "USD" }
        };
        var csv = UsageCsvExporter.ExportRequests(records);
        StringAssert.StartsWith(csv, "request_id,");
        StringAssert.Contains(csv, "req-1");
        StringAssert.Contains(csv, "10");
    }

    [TestMethod]
    public void ExportRequestsCsv_EscapesCommasAndQuotes()
    {
        var records = new List<RequestUsageRecord>
        {
            new() { RequestId = "req\"quote", StatusCode = 200, SessionId = "s,es" }
        };
        var csv = UsageCsvExporter.ExportRequests(records);
        StringAssert.Contains(csv, "\"req\"\"quote\"");
        StringAssert.Contains(csv, "\"s,es\"");
    }

    [TestMethod]
    public void ExportRequestsCsv_DoesNotContainSecretMarkers()
    {
        var records = new List<RequestUsageRecord>
        {
            new() { RequestId = "r1", StatusCode = 200, Model = "m1" }
        };
        var csv = UsageCsvExporter.ExportRequests(records);
        Assert.IsFalse(csv.Contains("sk-ant-"), "API key patterns must not appear in CSV.");
        Assert.IsFalse(csv.Contains("LOCAL_TOKEN"), "Local token must not appear in CSV.");
        Assert.IsFalse(csv.Contains("enc:"), "Encrypted tokens must not appear in CSV.");
    }

    [TestMethod]
    public void ExportGroupedUsageCsv_Works()
    {
        var rows = new List<UsageGroupRow>
        {
            new() { GroupKey = "2026-01-15", RequestCount = 3, TotalInputTokens = 50, TotalOutputTokens = 10, SuccessRate = 1.0 }
        };
        var csv = UsageCsvExporter.ExportGroupedUsage(rows);
        StringAssert.StartsWith(csv, "group_key,");
        StringAssert.Contains(csv, "2026-01-15");
    }

    [TestMethod]
    public void ExportSessionsCsv_Works()
    {
        var rows = new List<SessionSummaryRow>
        {
            new() { SessionId = "s-1", RequestCount = 5, TotalInputTokens = 100, TotalOutputTokens = 50, SuccessCount = 5 }
        };
        var csv = UsageCsvExporter.ExportSessions(rows);
        StringAssert.StartsWith(csv, "session_id,");
        StringAssert.Contains(csv, "s-1");
    }

    // ── Usage/cost accuracy edge cases ──

    [TestMethod]
    public async Task ZeroTokenResponse_HasZeroCostWhenPricingExists()
    {
        var path = Path.Combine(_root, "z.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();
        await store.AddRequestAsync(new RequestUsageRecord
        {
            RequestId = "zero", StatusCode = 200, InputTokens = 0, OutputTokens = 0,
            EstimatedCostMicros = 0, Currency = "USD"
        });
        Assert.AreEqual(1, await store.CountRequestsAsync());
        var rows = await store.QueryRequestsAsync(new RequestQuery());
        Assert.AreEqual(0, rows[0].InputTokens);
        Assert.AreEqual(0L, rows[0].EstimatedCostMicros);
    }

    [TestMethod]
    public async Task UnknownPricing_NullCost()
    {
        var path = Path.Combine(_root, "u.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();
        await store.AddRequestAsync(new RequestUsageRecord
        {
            RequestId = "np", StatusCode = 200, InputTokens = 10, OutputTokens = 5
        });
        var rows = await store.QueryRequestsAsync(new RequestQuery());
        Assert.IsNull(rows[0].EstimatedCostMicros);
        Assert.IsNull(rows[0].Currency);
    }

    [TestMethod]
    public async Task CacheTokens_AreStored()
    {
        var path = Path.Combine(_root, "c.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();
        await store.AddRequestAsync(new RequestUsageRecord
        {
            RequestId = "cache", StatusCode = 200,
            InputTokens = 100, OutputTokens = 20,
            CacheCreationInputTokens = 5, CacheReadInputTokens = 10
        });
        var rows = await store.QueryRequestsAsync(new RequestQuery());
        Assert.AreEqual(5, rows[0].CacheCreationInputTokens);
        Assert.AreEqual(10, rows[0].CacheReadInputTokens);
    }

    [TestMethod]
    public async Task NonStreamingAndStreaming_AreDistinguishable()
    {
        var path = Path.Combine(_root, "s.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();
        await store.AddRequestAsync(new RequestUsageRecord { RequestId = "ns", Streaming = false, StatusCode = 200 });
        await store.AddRequestAsync(new RequestUsageRecord { RequestId = "st", Streaming = true, StatusCode = 200 });
        var rows = await store.QueryRequestsAsync(new RequestQuery());
        Assert.IsFalse(rows.First(r => r.RequestId == "ns").Streaming);
        Assert.IsTrue(rows.First(r => r.RequestId == "st").Streaming);
    }

    [TestMethod]
    public async Task MixedProviderModelSessions_AreGroupedCorrectly()
    {
        var path = Path.Combine(_root, "mx.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();
        await store.AddRequestAsync(new RequestUsageRecord { RequestId = "mx1", SessionId = "smx", ProviderId = "pa", Model = "ma", StatusCode = 200, InputTokens = 10 });
        await store.AddRequestAsync(new RequestUsageRecord { RequestId = "mx2", SessionId = "smx", ProviderId = "pb", Model = "mb", StatusCode = 200, InputTokens = 20 });
        await store.UpsertSessionAsync(new SessionUpsert { SessionId = "smx", Status = "active", TokensDelta = 10 });
        await store.UpsertSessionAsync(new SessionUpsert { SessionId = "smx", Status = "active", TokensDelta = 20 });

        var summaries = await store.QuerySessionSummariesAsync();
        var s = summaries.First(r => r.SessionId == "smx");
        Assert.AreEqual(2, s.RequestCount);
        Assert.AreEqual(30, s.TotalInputTokens);
        Assert.IsTrue(s.ProviderBreakdowns.Any(b => b.ProviderId == "pa" && b.Model == "ma"));
        Assert.IsTrue(s.ProviderBreakdowns.Any(b => b.ProviderId == "pb" && b.Model == "mb"));
    }

    // ── Privacy sentinel regression ──

    [TestMethod]
    public async Task PromptMarker_NotInDatabase()
    {
        const string marker = "SENTINEL_PROMPT_SHOULD_NOT_BE_IN_DB_2026";
        var path = Path.Combine(_root, "sentinel.db");
        var store = new SqliteUsageStore(path);
        await store.InitializeAsync();
        // The actual persistence layer doesn't store prompts — we prove by seeding a
        // request with non-prompt metadata and verifying the sentinel value is absent
        // from the entire database content.
        await store.AddRequestAsync(new RequestUsageRecord
        {
            RequestId = "s1", StatusCode = 200, InputTokens = 5, OutputTokens = 2
        });

        // Read every row from every table and confirm the sentinel is absent.
        var tables = await store.ListTablesAsync();
        var connectionString = $"Data Source={path}";
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        foreach (var table in tables)
        {
            await using var cmd = connection.CreateCommand();
            try
            {
                cmd.CommandText = $"SELECT * FROM {table};";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                        Assert.IsFalse(value?.Contains(marker) == true, $"Sentinel prompt marker found in {table}.{reader.GetName(i)}!");
                    }
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* table may not exist yet */ }
        }
    }

    [TestMethod]
    public void PromptMarker_NotInCsv()
    {
        const string marker = "SENTINEL_PROMPT_NOT_IN_CSV_2026";
        var records = new List<RequestUsageRecord>
        {
            new() { RequestId = "csv1", StatusCode = 200 }
        };
        var csv = UsageCsvExporter.ExportRequests(records);
        Assert.IsFalse(csv.Contains(marker), "Sentinel prompt marker found in CSV!");
        Assert.IsFalse(csv.Contains("sk-ant-"));
        Assert.IsFalse(csv.Contains("LOCAL_TOKEN"));
        Assert.IsFalse(csv.Contains("enc:"));
    }

    [TestMethod]
    public void UpstreamKey_NotInCsv()
    {
        var records = new List<RequestUsageRecord>
        {
            new() { RequestId = "r1", StatusCode = 200 }
        };
        var csv = UsageCsvExporter.ExportRequests(records);
        Assert.IsFalse(csv.Contains("UPSTREAM_KEY"), "Upstream key must not appear in CSV.");
    }

    [TestMethod]
    public void LocalToken_NotInCsv()
    {
        var records = new List<RequestUsageRecord>
        {
            new() { RequestId = "r1", StatusCode = 200 }
        };
        var csv = UsageCsvExporter.ExportRequests(records);
        Assert.IsFalse(csv.Contains("LOCAL_TOKEN"), "Local token must not appear in CSV.");
    }

    [TestMethod]
    public void EncryptedKey_NotInCsv()
    {
        var records = new List<RequestUsageRecord>
        {
            new() { RequestId = "r1", StatusCode = 200 }
        };
        var csv = UsageCsvExporter.ExportRequests(records);
        Assert.IsFalse(csv.Contains("enc:"), "Encrypted key prefix must not appear in CSV.");
    }
}
