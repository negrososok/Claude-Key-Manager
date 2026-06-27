using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AerolinkManager.Core.Storage;
using AerolinkManager.Core.Models;
using ClaudeManager.Gateway.Logging;

namespace AerolinkManager.Tests.Gateway;

/// <summary>
/// Phase B: prove that usage/cost is persisted safely for both streaming and
/// non-streaming paths. No secrets in the DB. No double-count on retry. Session
/// accumulation. Limit events. Uses <see cref="RecordingUsageStore"/> so assertions
/// are synchronous.
/// </summary>
[TestClass]
public sealed class UsagePersistenceTests
{
    private const string Token = "LOCAL_TOKEN_SHOULD_NOT_LEAK";

    private IHost _host = null!;
    private HttpClient _client = null!;
    private RecordingUsageStore _usageStore = null!;
    private readonly InMemoryLoggerProvider _loggerProvider = new();

    private void InitHost(Type startupType)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AerolinkUsageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var appPaths = new AerolinkManager.Core.Configuration.AppPaths(tempDir);

        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseStartup(startupType);
                web.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                web.ConfigureServices(services => services.AddSingleton(appPaths));
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(_loggerProvider);
            });

        _host = hostBuilder.Build();

        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        var protector = _host.Services.GetRequiredService<AerolinkManager.Core.Security.ISecretProtector>();
        store.UpdateConfig(c => c with { Gateway = c.Gateway with { LocalAuthTokenEncrypted = protector.Protect(Token) } });
        SetupValidRouteWithPricing(store, "UPSTREAM_KEY_SHOULD_NOT_LEAK");

        _host.StartAsync().GetAwaiter().GetResult();

        _usageStore = _host.Services.GetRequiredService<RecordingUsageStore>();

        var server = _host.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var uri = new Uri(address.Replace("localhost", "127.0.0.1"));
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{uri.Port}") };
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _client?.Dispose();
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private void InitBufferedHost() => InitHost(typeof(TestStartup));
    private void InitStreamingHost() => InitHost(typeof(StreamingTestStartup));

    private static void SetupValidRouteWithPricing(AerolinkManager.Core.Configuration.JsonFileStore store, string keySecret)
    {
        store.UpdateConfig(c => c with
        {
            Providers = [ProviderPresets.Aerolink()],
            Keys =
            [
                new ApiKeyRecord
                {
                    Id = Guid.NewGuid(),
                    ProviderId = ProviderPresets.AerolinkId,
                    ApiKeyEncrypted = keySecret,
                    Name = "test key"
                }
            ],
            RoutingChains =
            [
                new RoutingChain
                {
                    Id = "chain-default",
                    Name = "Default Chain",
                    Steps =
                    [
                        new RoutingChainStep
                        {
                            Order = 1,
                            ProviderIds = [ProviderPresets.AerolinkId]
                        }
                    ]
                }
            ],
            LaunchProfiles =
            [
                new LaunchProfile
                {
                    Id = "default",
                    Name = "default",
                    ProviderIds = [ProviderPresets.AerolinkId],
                    RoutingChainId = "chain-default",
                    IsDefault = true
                }
            ],
            ModelPricing =
            [
                new ModelPricing
                {
                    Id = "price-1",
                    ProviderId = ProviderPresets.AerolinkId,
                    ModelValue = "m",
                    InputPerMillion = 5m,
                    OutputPerMillion = 25m,
                    CacheReadPerMillion = 0.5m,
                    CacheWritePerMillion = 6.25m,
                    Currency = "USD"
                }
            ]
        });
    }

    private HttpRequestMessage Request(string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        req.Headers.Add("x-api-key", Token);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return req;
    }

    // ── Tests ──

    [TestMethod]
    public async Task NonStreaming_PersistsRequestRow_WithTokensAndCost()
    {
        InitBufferedHost();
        _usageStore.Clear();
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.MockResponse = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"msg\",\"usage\":{\"input_tokens\":100,\"output_tokens\":40}}", Encoding.UTF8, "application/json")
        };

        var response = await _client.SendAsync(Request("{\"model\":\"m\",\"stream\":false,\"messages\":[]}"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, _usageStore.Requests.Count, $"Expected 1 request, got {_usageStore.Requests.Count}. Logs: {_loggerProvider.Logs.Count}");

        var r = _usageStore.Requests[0];
        Assert.IsFalse(r.Streaming);
        Assert.AreEqual(200, r.StatusCode);
        Assert.IsNull(r.ErrorType);
        Assert.IsTrue(r.DurationMs >= 0);
        Assert.AreEqual(100, r.InputTokens);
        Assert.AreEqual(40, r.OutputTokens);
        Assert.IsNotNull(r.EstimatedCostMicros);
        Assert.AreEqual("USD", r.Currency);
    }

    [TestMethod]
    public async Task Streaming_PersistsRequestRow_FromSseUsage()
    {
        InitStreamingHost();
        _usageStore.Clear();
        var upstream = _host.Services.GetRequiredService<StreamingMockHandler>();
        upstream.ResponseForAttempt = _ => MockUpstreamResponse.Stream(
        [
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":100,\"output_tokens\":1}}}\n\n",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":50}}\n\n",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
        ]);

        var response = await _client.SendAsync(Request("{\"model\":\"m\",\"stream\":true,\"messages\":[]}"), HttpCompletionOption.ResponseHeadersRead);
        await response.Content.ReadAsStringAsync();

        Assert.AreEqual(1, _usageStore.Requests.Count);
        var r = _usageStore.Requests[0];
        Assert.IsTrue(r.Streaming);
        Assert.AreEqual(200, r.StatusCode);
        Assert.AreEqual(100, r.InputTokens);
        Assert.AreEqual(50, r.OutputTokens);
        Assert.IsNull(r.ErrorType);
        Assert.IsNotNull(r.EstimatedCostMicros);
    }

    [TestMethod]
    public async Task NoPricing_PersistsRowWithNullCost()
    {
        InitBufferedHost();
        _usageStore.Clear();
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => c with { ModelPricing = [] });
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.MockResponse = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"msg\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}", Encoding.UTF8, "application/json")
        };

        await _client.SendAsync(Request("{\"model\":\"m\",\"stream\":false,\"messages\":[]}"));

        Assert.AreEqual(1, _usageStore.Requests.Count);
        Assert.IsNull(_usageStore.Requests[0].EstimatedCostMicros);
        Assert.IsNull(_usageStore.Requests[0].Currency);
    }

    [TestMethod]
    public async Task FailedRequest_PersistsRowWithErrorType()
    {
        InitBufferedHost();
        _usageStore.Clear();
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.MockResponse = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        await _client.SendAsync(Request("{\"model\":\"m\",\"stream\":false,\"messages\":[]}"));

        Assert.AreEqual(1, _usageStore.Requests.Count);
        Assert.AreEqual("http_502", _usageStore.Requests[0].ErrorType);
        Assert.AreEqual(502, _usageStore.Requests[0].StatusCode);
    }

    [TestMethod]
    public async Task NoPromptsOrSecretsInPersistedData()
    {
        const string promptMarker = "PROMPT_SHOULD_NOT_BE_PERSISTED_42";
        InitBufferedHost();
        _usageStore.Clear();
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.MockResponse = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"msg\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}", Encoding.UTF8, "application/json") };

        var body = "{\"model\":\"m\",\"stream\":false,\"messages\":[{\"role\":\"user\",\"content\":\"" + promptMarker + "\"}]}";
        await _client.SendAsync(Request(body));

        Assert.AreEqual(1, _usageStore.Requests.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(_usageStore.Requests[0]);
        Assert.IsFalse(json.Contains(promptMarker), "Prompt must not be persisted.");
        Assert.IsFalse(json.Contains("UPSTREAM_KEY_SHOULD_NOT_LEAK"), "Upstream key must not be persisted.");
        Assert.IsFalse(json.Contains(Token), "Local token must not be persisted.");
    }

    [TestMethod]
    public async Task RetryBeforeFirstChunk_CreatesExactlyOneRequestRow()
    {
        InitStreamingHost();
        _usageStore.Clear();
        var upstream = _host.Services.GetRequiredService<StreamingMockHandler>();
        upstream.ResponseForAttempt = attempt => attempt == 1
            ? MockUpstreamResponse.Error(HttpStatusCode.ServiceUnavailable)
            : MockUpstreamResponse.Stream(["data: {\"type\":\"message_stop\"}\n\n"]);

        var response = await _client.SendAsync(Request("{\"model\":\"m\",\"stream\":true,\"messages\":[]}"), HttpCompletionOption.ResponseHeadersRead);
        await response.Content.ReadAsStringAsync();

        Assert.AreEqual(2, upstream.RequestCount, "Pre-stream retry must happen.");
        Assert.AreEqual(1, _usageStore.Requests.Count, "Only ONE request row, never one per attempt.");
        Assert.AreEqual(200, _usageStore.Requests[0].StatusCode);
    }

    [TestMethod]
    public async Task PartialStream_CreatesAtMostOneRequestRow_WithErrorType()
    {
        InitStreamingHost();
        _usageStore.Clear();
        var upstream = _host.Services.GetRequiredService<StreamingMockHandler>();
        upstream.ResponseForAttempt = _ => MockUpstreamResponse.Stream(
            ["data: chunk1\n\n", "data: chunk2\n\n"],
            throwAfterChunk: 2);

        var response = await _client.SendAsync(Request("{\"model\":\"m\",\"stream\":true,\"messages\":[]}"), HttpCompletionOption.ResponseHeadersRead);
        await response.Content.ReadAsStringAsync();

        Assert.AreEqual(1, upstream.RequestCount, "Mid-stream error must not be retried.");
        Assert.AreEqual(1, _usageStore.Requests.Count, "At most one row.");
        Assert.AreEqual("upstream_stream_failure", _usageStore.Requests[0].ErrorType);
        Assert.AreEqual(200, _usageStore.Requests[0].StatusCode);
    }

    [TestMethod]
    public async Task Session_UpsertAccumulatesAcrossRequests()
    {
        InitBufferedHost();
        _usageStore.Clear();
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.MockResponse = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"msg\",\"usage\":{\"input_tokens\":10,\"output_tokens\":2}}", Encoding.UTF8, "application/json") };

        var body = "{\"model\":\"m\",\"stream\":false,\"messages\":[]}";
        for (var i = 0; i < 2; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
            req.Headers.Add("x-api-key", Token);
            req.Headers.Add("X-Claude-Code-Session-Id", "session-acc");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            await _client.SendAsync(req);
        }

        Assert.IsTrue(_usageStore.Sessions.Count >= 2, $"Each request upserts the session. Got {_usageStore.Sessions.Count} sessions.");
        Assert.AreEqual("session-acc", _usageStore.Sessions[^1].SessionId);
        Assert.AreEqual("active", _usageStore.Sessions[^1].Status);
    }

    [TestMethod]
    public async Task LimitEvent_On429_IsRecorded()
    {
        InitBufferedHost();
        _usageStore.Clear();
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.MockResponse = _ =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            res.Content = new StringContent("{\"error\":\"rate limit\"}", Encoding.UTF8, "application/json");
            return res;
        };

        await _client.SendAsync(Request("{\"model\":\"m\",\"stream\":false,\"messages\":[]}"));

        Assert.AreEqual(1, _usageStore.LimitEvents.Count);
        Assert.AreEqual("rate_limit", _usageStore.LimitEvents[0].LimitType);
        Assert.IsNotNull(_usageStore.LimitEvents[0].ResetAtUtc);
        Assert.IsFalse(_usageStore.LimitEvents[0].Estimated);
    }

    [TestMethod]
    public async Task LimitEvent_On402_IsRecorded()
    {
        InitBufferedHost();
        _usageStore.Clear();
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.MockResponse = _ => new HttpResponseMessage(HttpStatusCode.PaymentRequired) { Content = new StringContent("{\"error\":\"billing\"}", Encoding.UTF8, "application/json") };

        await _client.SendAsync(Request("{\"model\":\"m\",\"stream\":false,\"messages\":[]}"));

        Assert.AreEqual(1, _usageStore.LimitEvents.Count);
        Assert.AreEqual("billing", _usageStore.LimitEvents[0].LimitType);
    }
}
