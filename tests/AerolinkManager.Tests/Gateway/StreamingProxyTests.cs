using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Gateway;

[TestClass]
public sealed class StreamingProxyTests
{
    private const string Token = "LOCAL_TOKEN_SHOULD_NOT_LEAK";

    private IHost _host = null!;
    private HttpClient _client = null!;
    private StreamingMockHandler _upstream = null!;
    private readonly InMemoryLoggerProvider _loggerProvider = new();

    [TestInitialize]
    public async Task Setup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AerolinkStreamingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var appPaths = new AerolinkManager.Core.Configuration.AppPaths(tempDir);

        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseStartup<StreamingTestStartup>();
                web.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                web.ConfigureServices(services => services.AddSingleton(appPaths));
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(_loggerProvider);
            })
            .ConfigureServices(services =>
            {
                var descriptors = services.Where(d => d.ServiceType == typeof(ILoggerProvider)).ToList();
                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                    services.Add(new ServiceDescriptor(typeof(ILoggerProvider), sp =>
                    {
                        var registry = sp.GetRequiredService<ClaudeManager.Gateway.Logging.SecretRegistry>();
                        ILoggerProvider inner = descriptor.ImplementationInstance is not null
                            ? (ILoggerProvider)descriptor.ImplementationInstance
                            : descriptor.ImplementationFactory is not null
                                ? (ILoggerProvider)descriptor.ImplementationFactory(sp)
                                : (ILoggerProvider)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
                        return new ClaudeManager.Gateway.Logging.RedactingLoggerProvider(inner, registry);
                    }, descriptor.Lifetime));
                }
            });

        _host = hostBuilder.Build();

        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        var protector = _host.Services.GetRequiredService<AerolinkManager.Core.Security.ISecretProtector>();
        store.UpdateConfig(c => c with { Gateway = c.Gateway with { LocalAuthTokenEncrypted = protector.Protect(Token) } });
        SetupValidRoute(store, "UPSTREAM_KEY_SHOULD_NOT_LEAK");

        await _host.StartAsync();

        _upstream = _host.Services.GetRequiredService<StreamingMockHandler>();

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

    private static void SetupValidRoute(AerolinkManager.Core.Configuration.JsonFileStore store, string keySecret)
    {
        store.UpdateConfig(c => c with
        {
            Providers = [AerolinkManager.Core.Models.ProviderPresets.Aerolink()],
            Keys =
            [
                new AerolinkManager.Core.Models.ApiKeyRecord
                {
                    Id = Guid.NewGuid(),
                    ProviderId = AerolinkManager.Core.Models.ProviderPresets.AerolinkId,
                    ApiKeyEncrypted = keySecret,
                    Name = "test key"
                }
            ],
            RoutingChains =
            [
                new AerolinkManager.Core.Models.RoutingChain
                {
                    Id = "chain-default",
                    Name = "Default Chain",
                    Steps =
                    [
                        new AerolinkManager.Core.Models.RoutingChainStep
                        {
                            Order = 1,
                            ProviderIds = [AerolinkManager.Core.Models.ProviderPresets.AerolinkId]
                        }
                    ]
                }
            ],
            LaunchProfiles =
            [
                new AerolinkManager.Core.Models.LaunchProfile
                {
                    Id = "default",
                    Name = "default",
                    ProviderIds = [AerolinkManager.Core.Models.ProviderPresets.AerolinkId],
                    RoutingChainId = "chain-default",
                    IsDefault = true
                }
            ]
        });
    }

    private HttpRequestMessage StreamRequest(string body = "{\"model\":\"m\",\"stream\":true,\"messages\":[]}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", Token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    [TestMethod]
    public async Task RetryBeforeFirstChunk_OnPreStream503_RetriesAndStreams()
    {
        var chunks = new[]
        {
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}}\n\n",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
        };
        _upstream.ResponseForAttempt = attempt => attempt == 1
            ? MockUpstreamResponse.Error(HttpStatusCode.ServiceUnavailable)
            : MockUpstreamResponse.Stream(chunks);

        var response = await _client.SendAsync(StreamRequest(), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, _upstream.RequestCount, "Pre-stream 503 must be retried once.");
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(string.Concat(chunks), body);
    }

    [TestMethod]
    public async Task RetryAfterFirstChunk_OnMidStreamError_NotRetried()
    {
        var chunks = new[]
        {
            "event: message_start\ndata: {\"type\":\"message_start\"}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\"}\n\n"
        };
        // Stream both chunks, then fail. Must NOT retry and must NOT duplicate.
        _upstream.ResponseForAttempt = _ => MockUpstreamResponse.Stream(chunks, throwAfterChunk: 2);

        var response = await _client.SendAsync(StreamRequest(), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(1, _upstream.RequestCount, "A mid-stream failure after bytes were sent must never be retried.");
        Assert.AreEqual(string.Concat(chunks), body, "Client must receive exactly the forwarded chunks, no duplication.");
    }

    [TestMethod]
    public async Task NoDuplicateChunks_WhenPreStreamRetryHappens()
    {
        var chunks = new[] { "data: e1\n\n", "data: e2\n\n", "data: e3\n\n" };
        _upstream.ResponseForAttempt = attempt => attempt == 1
            ? MockUpstreamResponse.Error(HttpStatusCode.ServiceUnavailable)
            : MockUpstreamResponse.Stream(chunks);

        var response = await _client.SendAsync(StreamRequest(), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("data: e1\n\ndata: e2\n\ndata: e3\n\n", body);
        Assert.AreEqual(2, _upstream.RequestCount);
    }

    [TestMethod]
    public async Task UnknownSseEvent_PassesThroughByteForByte()
    {
        var chunks = new[]
        {
            ": ping comment\n\n",
            "event: some_future_event\ndata: {\"type\":\"some_future_event\",\"x\":1}   \n\n",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
        };
        _upstream.ResponseForAttempt = _ => MockUpstreamResponse.Stream(chunks);

        var response = await _client.SendAsync(StreamRequest(), HttpCompletionOption.ResponseHeadersRead);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        var expected = Encoding.UTF8.GetBytes(string.Concat(chunks));
        CollectionAssert.AreEqual(expected, bytes, "Unknown events / comments / whitespace must pass through byte-for-byte.");
    }

    [TestMethod]
    public async Task SseEventOrder_IsPreserved()
    {
        var chunks = Enumerable.Range(1, 6).Select(i => $"event: e{i}\ndata: {{\"n\":{i}}}\n\n").ToArray();
        _upstream.ResponseForAttempt = _ => MockUpstreamResponse.Stream(chunks, perChunkDelay: TimeSpan.FromMilliseconds(15));

        var response = await _client.SendAsync(StreamRequest(), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(string.Concat(chunks), body, "Event order must be preserved.");
    }

    [TestMethod]
    public async Task Streaming_DoesNotLogSecretsOrPrompts()
    {
        const string promptMarker = "PROMPT_MARKER_SHOULD_NOT_LEAK_42";
        _upstream.ResponseForAttempt = _ => MockUpstreamResponse.Stream(["data: {\"type\":\"message_stop\"}\n\n"]);

        var registry = _host.Services.GetRequiredService<ClaudeManager.Gateway.Logging.SecretRegistry>();
        registry.RegisterSecret("UPSTREAM_KEY_SHOULD_NOT_LEAK");

        var body = "{\"model\":\"m\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"" + promptMarker + "\"}]}";
        var response = await _client.SendAsync(StreamRequest(body), HttpCompletionOption.ResponseHeadersRead);
        await response.Content.ReadAsStringAsync();

        foreach (var log in _loggerProvider.Logs)
        {
            Assert.IsFalse(log.Contains("UPSTREAM_KEY_SHOULD_NOT_LEAK"), "Upstream key leaked in logs!");
            Assert.IsFalse(log.Contains(Token), "Local token leaked in logs!");
            Assert.IsFalse(log.Contains(promptMarker), "Prompt body leaked in logs!");
        }
    }

    [TestMethod]
    public async Task PreStream4xx_NotRetried_StatusForwarded()
    {
        _upstream.ResponseForAttempt = _ => MockUpstreamResponse.Error(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}");

        var response = await _client.SendAsync(StreamRequest(), HttpCompletionOption.ResponseHeadersRead);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(1, _upstream.RequestCount, "A pre-stream 4xx must not be retried.");
    }

    [TestMethod]
    public async Task ClientCancellation_StopsWithoutRetry()
    {
        var chunks = Enumerable.Range(1, 50).Select(i => $"data: chunk{i}\n\n").ToArray();
        _upstream.ResponseForAttempt = _ => MockUpstreamResponse.Stream(chunks, perChunkDelay: TimeSpan.FromMilliseconds(60));

        var response = await _client.SendAsync(StreamRequest(), HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();

        var buffer = new byte[16];
        await stream.ReadAsync(buffer); // read at least the first chunk
        // Client goes away mid-stream: disposing the response aborts the connection,
        // which the gateway observes (RequestAborted) and stops consuming upstream.
        response.Dispose();

        // The upstream must observe the gateway stopped consuming; poll to avoid flakiness.
        for (var i = 0; i < 60 && !_upstream.CancellationObserved; i++)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(_upstream.CancellationObserved, "Client disconnect must propagate so the upstream stream stops.");
        Assert.AreEqual(1, _upstream.RequestCount, "Cancellation must never trigger a retry.");
    }
}
