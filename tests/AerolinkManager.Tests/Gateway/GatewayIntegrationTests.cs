using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ClaudeManager.Gateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AerolinkManager.Tests.Gateway;

[TestClass]
public class GatewayIntegrationTests
{
    private IHost _host = null!;
    private HttpClient _client = null!;
    private int _port;
    private InMemoryLoggerProvider _loggerProvider = new();

    [TestInitialize]
    public async Task Setup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AerolinkGatewayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var appPaths = new AerolinkManager.Core.Configuration.AppPaths(tempDir);

        // Setup real Kestrel host on random loopback port (not just TestServer)
        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<TestStartup>();
                webBuilder.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, 0);
                });
                webBuilder.ConfigureServices(services => 
                {
                    services.AddSingleton<AerolinkManager.Core.Configuration.AppPaths>(appPaths);
                });
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(_loggerProvider);
            })
            .ConfigureServices(services => 
            {
                var providerDescriptors = services.Where(d => d.ServiceType == typeof(ILoggerProvider)).ToList();
                foreach (var descriptor in providerDescriptors)
                {
                    services.Remove(descriptor);
                    services.Add(new ServiceDescriptor(typeof(ILoggerProvider), sp =>
                    {
                        var registry = sp.GetRequiredService<ClaudeManager.Gateway.Logging.SecretRegistry>();
                        ILoggerProvider inner;
                        if (descriptor.ImplementationInstance != null)
                            inner = (ILoggerProvider)descriptor.ImplementationInstance;
                        else if (descriptor.ImplementationFactory != null)
                            inner = (ILoggerProvider)descriptor.ImplementationFactory(sp);
                        else
                            inner = (ILoggerProvider)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
                        
                        return new ClaudeManager.Gateway.Logging.RedactingLoggerProvider(inner, registry);
                    }, descriptor.Lifetime));
                }
            })
            .ConfigureAppConfiguration(config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "Gateway:LocalAuthTokenEncrypted", "LOCAL_TOKEN_SHOULD_NOT_LEAK" }
                });
            });

        var host = hostBuilder.Build();
        _host = host;

        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        var protector = _host.Services.GetRequiredService<AerolinkManager.Core.Security.ISecretProtector>();
        store.UpdateConfig(c =>
        {
            // Stored protected; clients still send the plaintext token.
            var g = c.Gateway with { LocalAuthTokenEncrypted = protector.Protect("LOCAL_TOKEN_SHOULD_NOT_LEAK") };
            return c with { Gateway = g };
        });

        await _host.StartAsync();
        
        var server = _host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addressFeature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var address = addressFeature?.Addresses.FirstOrDefault();
        
        if (address != null)
        {
            var uri = new Uri(address.Replace("localhost", "127.0.0.1"));
            _port = uri.Port;
            _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        }
        else 
        {
            // fallback
            _client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5000") };
        }
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

    [TestMethod]
    public async Task Request_WithoutToken_IsRejected()
    {
        var response = await _client.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Request_WithWrongToken_IsRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("x-api-key", "wrong-token");
        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Request_WithCorrectToken_IsAccepted()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Request_WithBearerToken_IsAccepted()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
    [TestMethod]
    public void Kestrel_Binds_OnlyToLoopback()
    {
        var server = _host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addressFeature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        Assert.IsNotNull(addressFeature);
        Assert.IsTrue(addressFeature.Addresses.Count > 0);
        foreach (var address in addressFeature.Addresses)
        {
            Assert.IsTrue(address.Contains("127.0.0.1") || address.Contains("[::1]") || address.Contains("localhost"), $"Non-loopback address found: {address}");
            Assert.IsFalse(address.Contains("0.0.0.0"), "Wildcard address 0.0.0.0 found!");
        }
    }

    [TestMethod]
    public async Task Logs_DoNotContainLocalToken_OrKeys()
    {
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => 
        { 
            var newKey = new AerolinkManager.Core.Models.ApiKeyRecord 
            { 
                Id = Guid.NewGuid(), 
                ProviderId = AerolinkManager.Core.Models.ProviderPresets.AnthropicOfficialId, 
                ApiKeyEncrypted = "UPSTREAM_KEY_SHOULD_NOT_LEAK", 
                Name = "test key"
            };
            return c with {
                Providers = [AerolinkManager.Core.Models.ProviderPresets.AnthropicOfficial()],
                Keys = [newKey],
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
                                ProviderIds = [AerolinkManager.Core.Models.ProviderPresets.AnthropicOfficialId]
                            }
                        ]
                    }
                ],
                LaunchProfiles = [new AerolinkManager.Core.Models.LaunchProfile { 
                    Id = "default", 
                    Name = "default", 
                    ProviderIds = [AerolinkManager.Core.Models.ProviderPresets.AnthropicOfficialId], 
                    RoutingChainId = "chain-default", 
                    IsDefault = true 
                }]
            };
        });
        
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");
        
        // This will attempt to reach the upstream and probably fail with network error since it's not mocked, but that's fine for log checking.
        var secretRegistry = _host.Services.GetRequiredService<ClaudeManager.Gateway.Logging.SecretRegistry>();
        secretRegistry.RegisterSecret("LOCAL_TOKEN_SHOULD_NOT_LEAK");
        secretRegistry.RegisterSecret("UPSTREAM_KEY_SHOULD_NOT_LEAK");
        await _client.SendAsync(request);

        // Force a log with the key just to see if it gets redacted!
        var logger = _host.Services.GetRequiredService<ILogger<GatewayIntegrationTests>>();
        logger.LogInformation("Testing token LOCAL_TOKEN_SHOULD_NOT_LEAK and key UPSTREAM_KEY_SHOULD_NOT_LEAK");

        foreach(var log in _loggerProvider.Logs)
        {
            if (log.Contains("LOCAL_TOKEN_SHOULD_NOT_LEAK") || log.Contains("UPSTREAM_KEY_SHOULD_NOT_LEAK")) {
                Console.WriteLine("LEAKED LOG: " + log);
            }
            Assert.IsFalse(log.Contains("LOCAL_TOKEN_SHOULD_NOT_LEAK"), "Local token leaked in logs!");
            Assert.IsFalse(log.Contains("UPSTREAM_KEY_SHOULD_NOT_LEAK"), "Upstream key leaked in logs!");
        }
    }

    [TestMethod]
    public async Task Messages_WithStreamTrue_StreamsResponse()
    {
        // Streaming is implemented in M04: stream:true is forwarded (no more 501).
        SetupValidRoute("UPSTREAM_KEY_SHOULD_NOT_LEAK");
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.MockResponse = null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"stream\":true,\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("msg_mock"), "Streamed upstream body should be forwarded to the client.");
    }

    [TestMethod]
    public void Gateway_HasNoWpfDependencies()
    {
        var assembly = typeof(ClaudeManager.Gateway.Program).Assembly;
        var refs = assembly.GetReferencedAssemblies();
        foreach (var r in refs)
        {
            Assert.IsFalse(r.Name?.Contains("PresentationFramework"), "WPF dependency found!");
            Assert.IsFalse(r.Name?.Contains("WindowsBase"), "WPF dependency found!");
        }
    }

    [TestMethod]
    public async Task Models_ReturnsListOfEnabledModels()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("\"type\":\"list\"") || body.Contains("\"type\": \"list\""));
    }

    [TestMethod]
    public async Task CountTokens_ForwardsToUpstream()
    {
        // This will attempt to reach the upstream and fail network, but we just check if it routes
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/count_tokens");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request);
        // It should return 502/504 because no upstream is actually running at api.anthropic.com locally, but we ensure it's not 404 or 501.
        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [TestMethod]
    public async Task UpstreamForwarding_RemovesLocalToken_UsesProviderAuthScheme()
    {
        // Aerolink is an Anthropic-compatible provider whose AuthScheme is XApiKey,
        // so the upstream credential must go in x-api-key (NOT Authorization: Bearer).
        SetupValidRoute("UPSTREAM_KEY_SHOULD_NOT_LEAK");

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.LastRequest = null;
        mock.MockResponse = null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.IsNotNull(mock.LastRequest);
        Assert.AreEqual(1, mock.RequestCount);

        var upstreamHeaders = mock.LastRequest.Headers;
        Assert.IsTrue(upstreamHeaders.Contains("x-api-key"), "Aerolink (XApiKey) must forward via x-api-key.");
        Assert.AreEqual("UPSTREAM_KEY_SHOULD_NOT_LEAK", upstreamHeaders.GetValues("x-api-key").First());
        Assert.IsFalse(upstreamHeaders.Contains("Authorization"), "x-api-key scheme must not send Authorization.");
        // The local gateway token must never be forwarded upstream.
        Assert.AreNotEqual("LOCAL_TOKEN_SHOULD_NOT_LEAK", upstreamHeaders.Contains("x-api-key") ? upstreamHeaders.GetValues("x-api-key").First() : null);
    }

    [TestMethod]
    public async Task UpstreamForwarding_BearerProvider_UsesAuthorizationHeader()
    {
        // A provider explicitly configured with Bearer auth must use Authorization: Bearer.
        SetupValidRouteWithProvider("UPSTREAM_KEY_SHOULD_NOT_LEAK", AerolinkManager.Core.Models.ProviderAuthScheme.Bearer);

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.LastRequest = null;
        mock.MockResponse = null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");

        await _client.SendAsync(request);

        var upstreamHeaders = mock.LastRequest!.Headers;
        Assert.IsTrue(upstreamHeaders.Contains("Authorization"));
        Assert.AreEqual("Bearer UPSTREAM_KEY_SHOULD_NOT_LEAK", upstreamHeaders.GetValues("Authorization").First());
        Assert.IsFalse(upstreamHeaders.Contains("x-api-key"));
    }

    [TestMethod]
    public async Task AuthFailure401_DisablesBadKey_AndFallsBackToNextProvider()
    {
        var (badKeyId, goodKeyId) = SetupTwoProviderFallbackRoute();
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        var seenUpstreamKeys = new List<string>();
        mock.RequestCount = 0;
        mock.LastRequest = null;
        mock.MockResponse = request =>
        {
            var upstreamKey = request.Headers.GetValues("x-api-key").Single();
            seenUpstreamKeys.Add(upstreamKey);
            return upstreamKey == "BAD_UPSTREAM_KEY"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"Unauthorized - Invalid token\"}", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                };
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-opus-4.8\",\"messages\":[]}", Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { "BAD_UPSTREAM_KEY", "GOOD_UPSTREAM_KEY" }, seenUpstreamKeys);
        Assert.AreEqual(2, mock.RequestCount);

        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        var config = store.LoadConfig();
        var badKey = config.Keys.Single(key => key.Id == badKeyId);
        var goodKey = config.Keys.Single(key => key.Id == goodKeyId);
        Assert.IsFalse(badKey.Enabled, "401/403 upstream auth failures should remove the bad key from rotation.");
        Assert.AreEqual(AerolinkManager.Core.Models.KeyStatus.Disabled, badKey.Status);
        StringAssert.Contains(badKey.LastErrorText, "HTTP 401");
        Assert.IsTrue(goodKey.Enabled, "Fallback key should remain available.");
    }

    [TestMethod]
    public async Task RateLimit429_MarksBadKeyLimited_AndFallsBackToNextProvider()
    {
        var (limitedKeyId, goodKeyId) = SetupTwoProviderFallbackRoute();
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        var seenUpstreamKeys = new List<string>();
        mock.RequestCount = 0;
        mock.LastRequest = null;
        mock.MockResponse = request =>
        {
            var upstreamKey = request.Headers.GetValues("x-api-key").Single();
            seenUpstreamKeys.Add(upstreamKey);
            return upstreamKey == "BAD_UPSTREAM_KEY"
                ? new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{\"error\":{\"type\":\"rate_limit_error\"}}", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                };
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-opus-4.8\",\"messages\":[]}", Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { "BAD_UPSTREAM_KEY", "GOOD_UPSTREAM_KEY" }, seenUpstreamKeys);
        Assert.AreEqual(2, mock.RequestCount);

        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        var config = store.LoadConfig();
        var limitedKey = config.Keys.Single(key => key.Id == limitedKeyId);
        var goodKey = config.Keys.Single(key => key.Id == goodKeyId);
        Assert.AreEqual(AerolinkManager.Core.Models.KeyStatus.Limited, limitedKey.Status);
        Assert.IsTrue(goodKey.Enabled, "Fallback key should remain available.");
    }

    [TestMethod]
    public async Task StreamingRateLimit429_BeforeBody_FallsBackToNextProvider()
    {
        var (limitedKeyId, _) = SetupTwoProviderFallbackRoute();
        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        var seenUpstreamKeys = new List<string>();
        mock.RequestCount = 0;
        mock.LastRequest = null;
        mock.MockResponse = request =>
        {
            var upstreamKey = request.Headers.GetValues("x-api-key").Single();
            seenUpstreamKeys.Add(upstreamKey);
            return upstreamKey == "BAD_UPSTREAM_KEY"
                ? new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{\"error\":{\"type\":\"rate_limit_error\"}}", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream")
                };
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-opus-4.8\",\"stream\":true,\"messages\":[]}", Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(body, "message_stop");
        CollectionAssert.AreEqual(new[] { "BAD_UPSTREAM_KEY", "GOOD_UPSTREAM_KEY" }, seenUpstreamKeys);

        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        var config = store.LoadConfig();
        var limitedKey = config.Keys.Single(key => key.Id == limitedKeyId);
        Assert.AreEqual(AerolinkManager.Core.Models.KeyStatus.Limited, limitedKey.Status);
    }

    [TestMethod]
    public async Task CustomHeaders_AllowedHeaders_ForwardedToUpstream()
    {
        // A provider with custom headers must have those headers added to the
        // upstream request (Gateway Mode), in addition to the auth header.
        SetupValidRouteWithHeaders("UPSTREAM_KEY_SHOULD_NOT_LEAK", new Dictionary<string, string>
        {
            ["x-custom-routing"] = "tenant-42",
            ["x-provider-region"] = "eu-west"
        });

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.LastRequest = null;
        mock.MockResponse = null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");

        await _client.SendAsync(request);

        var upstreamHeaders = mock.LastRequest!.Headers;
        Assert.IsTrue(upstreamHeaders.Contains("x-custom-routing"), "Custom header must be forwarded upstream.");
        Assert.AreEqual("tenant-42", upstreamHeaders.GetValues("x-custom-routing").First());
        Assert.IsTrue(upstreamHeaders.Contains("x-provider-region"));
        Assert.AreEqual("eu-west", upstreamHeaders.GetValues("x-provider-region").First());
        // Auth still applied normally.
        Assert.IsTrue(upstreamHeaders.Contains("x-api-key"));
        Assert.AreEqual("UPSTREAM_KEY_SHOULD_NOT_LEAK", upstreamHeaders.GetValues("x-api-key").First());
    }

    [TestMethod]
    public async Task CustomHeaders_ProtectedHeaders_NotForwarded_CannotOverrideAuth()
    {
        // Even if a malformed config carries protected header names, the gateway
        // must never let a custom header override the auth/content/host headers.
        SetupValidRouteWithHeaders("UPSTREAM_KEY_SHOULD_NOT_LEAK", new Dictionary<string, string>
        {
            ["x-api-key"] = "ATTACKER_KEY",
            ["Authorization"] = "Bearer ATTACKER",
            ["Host"] = "evil.example.com",
            ["x-claude-manager-token"] = "LOCAL_LEAK",
            ["x-safe-header"] = "ok"
        });

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.LastRequest = null;
        mock.MockResponse = null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");

        await _client.SendAsync(request);

        var upstreamHeaders = mock.LastRequest!.Headers;
        // x-api-key is the real upstream key from ApplyAuth, NOT the attacker value.
        Assert.AreEqual("UPSTREAM_KEY_SHOULD_NOT_LEAK", upstreamHeaders.GetValues("x-api-key").First());
        Assert.IsFalse(upstreamHeaders.Contains("Authorization"), "Custom header must not inject Authorization for an XApiKey provider.");
        Assert.IsFalse(upstreamHeaders.Contains("x-claude-manager-token"), "Local gateway headers must never be forwarded.");
        // The non-protected header still goes through.
        Assert.IsTrue(upstreamHeaders.Contains("x-safe-header"));
        Assert.AreEqual("ok", upstreamHeaders.GetValues("x-safe-header").First());
    }

    [TestMethod]
    public void CustomHeaders_Values_RedactedFromLogs()
    {
        // Custom header values may carry secrets; registering them must redact
        // them from any log output.
        const string secretHeaderValue = "SECRET_SIGNING_TOKEN_VALUE";
        var registry = _host.Services.GetRequiredService<ClaudeManager.Gateway.Logging.SecretRegistry>();
        registry.RegisterSecret(secretHeaderValue);

        var logger = _host.Services.GetRequiredService<ILogger<GatewayIntegrationTests>>();
        logger.LogInformation("forwarding header {Value}", secretHeaderValue);

        foreach (var log in _loggerProvider.Logs)
        {
            Assert.IsFalse(log.Contains(secretHeaderValue), "Custom header value leaked in logs!");
        }
    }

    [TestMethod]
    public async Task UpstreamForwarding_DecryptsCiphertext_PlaintextUpstream_NoLeakInLogs()
    {
        // Config stores ciphertext (enc:...). The gateway must forward the decrypted
        // plaintext upstream, and neither ciphertext nor plaintext may reach the logs.
        const string plaintext = "UPSTREAM_KEY_SHOULD_NOT_LEAK";
        var protector = _host.Services.GetRequiredService<AerolinkManager.Core.Security.ISecretProtector>();
        var ciphertext = protector.Protect(plaintext);
        SetupValidRoute(ciphertext);

        var secretRegistry = _host.Services.GetRequiredService<ClaudeManager.Gateway.Logging.SecretRegistry>();
        secretRegistry.RegisterSecret(ciphertext);
        secretRegistry.RegisterSecret(plaintext);

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.LastRequest = null;
        mock.MockResponse = null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");

        await _client.SendAsync(request);

        var upstreamHeaders = mock.LastRequest!.Headers;
        Assert.AreEqual(plaintext, upstreamHeaders.GetValues("x-api-key").First(), "Upstream must receive decrypted plaintext.");
        Assert.AreNotEqual(ciphertext, upstreamHeaders.GetValues("x-api-key").First(), "Upstream must not receive ciphertext.");

        // Force-log both values and confirm redaction.
        var logger = _host.Services.GetRequiredService<ILogger<GatewayIntegrationTests>>();
        logger.LogInformation("cipher {Cipher} plain {Plain}", ciphertext, plaintext);
        foreach (var log in _loggerProvider.Logs)
        {
            Assert.IsFalse(log.Contains(plaintext), "Plaintext key leaked in logs!");
            Assert.IsFalse(log.Contains(ciphertext), "Ciphertext key leaked in logs!");
        }
    }

    [TestMethod]
    public async Task RouteDecisionTrace_IsPersisted_WithoutSecretsHeadersOrBodies()
    {
        const string plaintext = "UPSTREAM_KEY_SHOULD_NOT_LEAK";
        const string customHeaderSecret = "CUSTOM_HEADER_SECRET_SHOULD_NOT_LEAK";
        const string requestHeaderSecret = "REQUEST_HEADER_SECRET_SHOULD_NOT_LEAK";
        const string promptSecret = "PROMPT_BODY_SECRET_SHOULD_NOT_LEAK";
        var protector = _host.Services.GetRequiredService<AerolinkManager.Core.Security.ISecretProtector>();
        var ciphertext = protector.Protect(plaintext);
        SetupValidRouteWithHeaders(ciphertext, new Dictionary<string, string>
        {
            ["x-custom-routing-secret"] = customHeaderSecret
        });

        var usageStore = _host.Services.GetRequiredService<RecordingUsageStore>();
        usageStore.Clear();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Headers.Add("x-request-secret", requestHeaderSecret);
        request.Content = new StringContent(
            "{\"model\":\"claude-3-haiku-20240307\",\"messages\":[{\"role\":\"user\",\"content\":\"" + promptSecret + "\"}]}",
            Encoding.UTF8,
            "application/json");

        await _client.SendAsync(request);

        var decision = AssertSingle(usageStore.RouteDecisions);
        Assert.AreEqual(AerolinkManager.Core.Models.ProviderPresets.AerolinkId, decision.SelectedProviderId);
        StringAssert.Contains(decision.Story, "routed to");
        StringAssert.Contains(decision.TraceJson, "skippedCandidates");
        StringAssert.Contains(decision.TraceJson, "requestedModel");
        StringAssert.Contains(decision.TraceJson, "resolvedModel");
        StringAssert.Contains(decision.TraceJson, "upstreamModel");
        AssertTraceDoesNotContain(decision.TraceJson, "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        AssertTraceDoesNotContain(decision.TraceJson, plaintext);
        AssertTraceDoesNotContain(decision.TraceJson, ciphertext);
        AssertTraceDoesNotContain(decision.TraceJson, customHeaderSecret);
        AssertTraceDoesNotContain(decision.TraceJson, requestHeaderSecret);
        AssertTraceDoesNotContain(decision.TraceJson, promptSecret);
        AssertTraceDoesNotContain(decision.TraceJson, "messages");
        AssertTraceDoesNotContain(decision.TraceJson, "x-api-key");
        AssertTraceDoesNotContain(decision.TraceJson, "Authorization");
    }

    [TestMethod]
    public async Task RouteDecisionTrace_StreamingDocumentsRetryBlockedAfterBodyStarts()
    {
        SetupValidRoute("UPSTREAM_KEY_SHOULD_NOT_LEAK");
        var usageStore = _host.Services.GetRequiredService<RecordingUsageStore>();
        usageStore.Clear();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"stream\":true,\"messages\":[]}", Encoding.UTF8, "application/json");

        await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        var decision = AssertSingle(usageStore.RouteDecisions);
        StringAssert.Contains(decision.TraceJson, "retry_before_response_body_only");
        StringAssert.Contains(decision.TraceJson, "blocked_to_avoid_duplicate_stream_chunks");
    }

    [TestMethod]
    public async Task ModelMode_ForceProfile_RewritesBodyModel()
    {
        SetupValidRouteWithModelMode("UPSTREAM_KEY_SHOULD_NOT_LEAK", AerolinkManager.Core.Models.ModelMode.ForceProfile, "forced-profile-model");

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.LastRequestBody = null;
        mock.MockResponse = null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"user-model\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");

        await _client.SendAsync(request);

        Assert.IsNotNull(mock.LastRequestBody);
        StringAssert.Contains(mock.LastRequestBody, "forced-profile-model");
        Assert.IsFalse(mock.LastRequestBody!.Contains("user-model"), "force_profile must override the user model.");
    }

    [TestMethod]
    public async Task ModelMode_RespectUser_KeepsRequestModel()
    {
        SetupValidRouteWithModelMode("UPSTREAM_KEY_SHOULD_NOT_LEAK", AerolinkManager.Core.Models.ModelMode.RespectUser, "profile-model");

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.LastRequestBody = null;
        mock.MockResponse = null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"user-model\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");

        await _client.SendAsync(request);

        Assert.IsNotNull(mock.LastRequestBody);
        StringAssert.Contains(mock.LastRequestBody, "user-model");
        Assert.IsFalse(mock.LastRequestBody!.Contains("profile-model"), "respect_user must not override an explicit request model.");
    }

    [TestMethod]
    public async Task BodyLimit_FromSettings_RejectsOversizedRequest()
    {
        SetupValidRoute("UPSTREAM_KEY_SHOULD_NOT_LEAK");
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => c with { Gateway = c.Gateway with { MaxRequestBodyMb = 1 } });

        var big = new string('a', 2 * 1024 * 1024); // 2 MB > 1 MB limit
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"m\",\"messages\":[\"" + big + "\"]}", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [TestMethod]
    public async Task BodyLimit_AppliesToCountTokens()
    {
        SetupValidRoute("UPSTREAM_KEY_SHOULD_NOT_LEAK");
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => c with { Gateway = c.Gateway with { MaxRequestBodyMb = 1 } });

        var big = new string('a', 2 * 1024 * 1024);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/count_tokens");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"m\",\"messages\":[\"" + big + "\"]}", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [TestMethod]
    public async Task Models_AreFilteredByActiveProfile()
    {
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => c with
        {
            Providers =
            [
                AerolinkManager.Core.Models.ProviderPresets.Aerolink(),
                AerolinkManager.Core.Models.ProviderPresets.AnthropicOfficial()
            ],
            Models =
            [
                new AerolinkManager.Core.Models.ModelRecord { Id = "in", ProviderId = AerolinkManager.Core.Models.ProviderPresets.AerolinkId, DisplayName = "In Profile", ModelValue = "model-in-profile", Enabled = true },
                new AerolinkManager.Core.Models.ModelRecord { Id = "out", ProviderId = AerolinkManager.Core.Models.ProviderPresets.AnthropicOfficialId, DisplayName = "Out Of Profile", ModelValue = "model-out-of-profile", Enabled = true }
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
                new AerolinkManager.Core.Models.LaunchProfile { Id = "default", Name = "default", ProviderIds = [AerolinkManager.Core.Models.ProviderPresets.AerolinkId], RoutingChainId = "chain-default", IsDefault = true }
            ]
        });

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(body, "model-in-profile");
        Assert.IsFalse(body.Contains("model-out-of-profile"), "Models from providers outside the active profile must not be exposed.");
    }

    [TestMethod]
    public async Task Models_DefaultWholePoolProfile_ExposesAllEnabledGatewayProviders()
    {
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => c with
        {
            Providers =
            [
                AerolinkManager.Core.Models.ProviderPresets.Aerolink(),
                AerolinkManager.Core.Models.ProviderPresets.AnthropicOfficial()
            ],
            Models =
            [
                new AerolinkManager.Core.Models.ModelRecord { Id = "aerolink-opus", ProviderId = AerolinkManager.Core.Models.ProviderPresets.AerolinkId, DisplayName = "Aerolink Opus", ModelValue = "aerolink-opus", Enabled = true },
                new AerolinkManager.Core.Models.ModelRecord { Id = "official-sonnet", ProviderId = AerolinkManager.Core.Models.ProviderPresets.AnthropicOfficialId, DisplayName = "Official Sonnet", ModelValue = "official-sonnet", Enabled = true }
            ],
            RoutingChains =
            [
                new AerolinkManager.Core.Models.RoutingChain
                {
                    Id = "chain-default",
                    Name = "Default Chain",
                    Steps = [new AerolinkManager.Core.Models.RoutingChainStep { Order = 1, ProviderIds = [] }]
                }
            ],
            LaunchProfiles =
            [
                new AerolinkManager.Core.Models.LaunchProfile { Id = "default", Name = "default", ProviderIds = [], RoutingChainId = "chain-default", IsDefault = true }
            ]
        });

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(body, "aerolink-opus");
        StringAssert.Contains(body, "official-sonnet");
    }

    [TestMethod]
    public async Task RateLimit429_MarksKeyLimited_AndSetsReset()
    {
        SetupValidRoute("UPSTREAM_KEY_SHOULD_NOT_LEAK");
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        var keyId = store.LoadConfig().Keys.First().Id;

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.MockResponse = req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return res;
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"m\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.AreEqual(1, mock.RequestCount, "With no alternative key, Gateway should return the original 429 after marking this key limited.");

        var key = store.LoadConfig().Keys.First(k => k.Id == keyId);
        Assert.AreEqual(AerolinkManager.Core.Models.KeyStatus.Limited, key.Status);
        Assert.IsNotNull(key.QuotaState.FiveHourResetAt);
        Assert.IsFalse(key.QuotaState.FiveHourResetEstimated, "Reset should come from Retry-After, not be estimated.");
    }

    [TestMethod]
    public async Task Billing402_MarksKeyLimited()
    {
        SetupValidRoute("UPSTREAM_KEY_SHOULD_NOT_LEAK");
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        var keyId = store.LoadConfig().Keys.First().Id;

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.MockResponse = _ => new HttpResponseMessage(HttpStatusCode.PaymentRequired);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"m\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.AreEqual(1, mock.RequestCount, "With no alternative key, Gateway should return the original 402 after marking this key limited.");
        var key = store.LoadConfig().Keys.First(k => k.Id == keyId);
        Assert.AreEqual(AerolinkManager.Core.Models.KeyStatus.Limited, key.Status);
    }

    [TestMethod]
    public async Task SafeRetry_RetriesOn503_SucceedsOnSecondTry()
    {
        SetupValidRoute("UPSTREAM_KEY_SHOULD_NOT_LEAK");

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.MockResponse = req => 
        {
            if (mock.RequestCount == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");
        
        var response = await _client.SendAsync(request);
        
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, mock.RequestCount); // Retried once
    }

    [TestMethod]
    public async Task RateLimit_HeadersAreParsedAndPassedDown()
    {
        SetupValidRoute("UPSTREAM_KEY_SHOULD_NOT_LEAK");

        var mock = _host.Services.GetRequiredService<MockHttpMessageHandler>();
        mock.RequestCount = 0;
        mock.MockResponse = req => 
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            res.Headers.Add("x-ratelimit-reset", "123456789");
            return res;
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        request.Content = new StringContent("{\"model\":\"claude-3-haiku-20240307\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json");
        
        var response = await _client.SendAsync(request);
        
        Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        Assert.AreEqual("123456789", response.Headers.GetValues("x-ratelimit-reset").First());
    }

    private void SetupValidRoute(string keySecret)
    {
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c =>
        {
            var newKey = new AerolinkManager.Core.Models.ApiKeyRecord
            {
                Id = Guid.NewGuid(),
                ProviderId = AerolinkManager.Core.Models.ProviderPresets.AerolinkId,
                ApiKeyEncrypted = keySecret,
                Name = "test key"
            };
            return c with {
                Providers = [AerolinkManager.Core.Models.ProviderPresets.Aerolink()],
                Keys = [newKey],
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
                LaunchProfiles = [new AerolinkManager.Core.Models.LaunchProfile {
                    Id = "default",
                    Name = "default",
                    ProviderIds = [AerolinkManager.Core.Models.ProviderPresets.AerolinkId],
                    RoutingChainId = "chain-default",
                    IsDefault = true
                }]
            };
        });
    }

    private (Guid BadKeyId, Guid GoodKeyId) SetupTwoProviderFallbackRoute()
    {
        var badKeyId = Guid.NewGuid();
        var goodKeyId = Guid.NewGuid();
        var firstProvider = AerolinkManager.Core.Models.ProviderPresets.Custom("first-provider", "First Provider") with
        {
            BaseUrl = "https://first-provider.test",
            DefaultModelId = "claude-opus-4.7"
        };
        var secondProvider = AerolinkManager.Core.Models.ProviderPresets.Custom("second-provider", "Second Provider") with
        {
            BaseUrl = "https://second-provider.test",
            DefaultModelId = "claude-opus-4.8"
        };

        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => c with
        {
            Providers = [firstProvider, secondProvider],
            Keys =
            [
                new AerolinkManager.Core.Models.ApiKeyRecord
                {
                    Id = badKeyId,
                    ProviderId = firstProvider.Id,
                    ApiKeyEncrypted = "BAD_UPSTREAM_KEY",
                    Name = "bad key",
                    Priority = 10,
                    AddedOrder = 1
                },
                new AerolinkManager.Core.Models.ApiKeyRecord
                {
                    Id = goodKeyId,
                    ProviderId = secondProvider.Id,
                    ApiKeyEncrypted = "GOOD_UPSTREAM_KEY",
                    Name = "good key",
                    Priority = 20,
                    AddedOrder = 2
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
                            ProviderIds = []
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
                    ProviderIds = [],
                    RoutingChainId = "chain-default",
                    IsDefault = true,
                    ModelMode = AerolinkManager.Core.Models.ModelMode.RespectUser
                }
            ]
        });

        return (badKeyId, goodKeyId);
    }

    private void SetupValidRouteWithProvider(string keySecret, AerolinkManager.Core.Models.ProviderAuthScheme scheme)
    {
        SetupValidRoute(keySecret);
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => c with
        {
            Providers = c.Providers
                .Select(p => p.Id == AerolinkManager.Core.Models.ProviderPresets.AerolinkId ? p with { AuthScheme = scheme } : p)
                .ToList()
        });
    }

    private void SetupValidRouteWithHeaders(string keySecret, Dictionary<string, string> headers)
    {
        SetupValidRoute(keySecret);
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => c with
        {
            Providers = c.Providers
                .Select(p => p.Id == AerolinkManager.Core.Models.ProviderPresets.AerolinkId
                    ? p with { CustomHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase) }
                    : p)
                .ToList()
        });
    }

    private void SetupValidRouteWithModelMode(string keySecret, AerolinkManager.Core.Models.ModelMode mode, string profileModel)
    {
        SetupValidRoute(keySecret);
        var store = _host.Services.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
        store.UpdateConfig(c => c with
        {
            LaunchProfiles = c.LaunchProfiles
                .Select(p => p.IsDefault ? p with { ModelMode = mode, ModelOverride = profileModel } : p)
                .ToList()
        });
    }

    private static T AssertSingle<T>(IReadOnlyList<T> items)
    {
        Assert.AreEqual(1, items.Count);
        return items[0];
    }

    private static void AssertTraceDoesNotContain(string traceJson, string forbidden)
    {
        Assert.IsFalse(traceJson.Contains(forbidden, StringComparison.Ordinal), $"Route trace leaked forbidden value: {forbidden}");
    }
}

public class InMemoryLoggerProvider : ILoggerProvider
{
    public readonly List<string> Logs = new();
    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(this);
    public void Dispose() { }
}

public class InMemoryLogger : ILogger
{
    private readonly InMemoryLoggerProvider _provider;
    public InMemoryLogger(InMemoryLoggerProvider provider) => _provider = provider;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _provider.Logs.Add(formatter(state, exception));
    }
}

/// <summary>
/// Test secret protector using an "enc:" prefix convention. Values stored without
/// the prefix pass through unchanged (so existing plaintext fixtures keep working),
/// while "enc:SECRET" decrypts to "SECRET" — letting a test prove that the gateway
/// forwards the decrypted plaintext, not the stored ciphertext.
/// </summary>
public sealed class FakeSecretProtector : AerolinkManager.Core.Security.ISecretProtector
{
    // Ciphertext is "enc:" + base64(plaintext) so the stored form never contains the
    // plaintext substring (mirrors DPAPI). Values without the prefix pass through
    // unchanged, so existing plaintext fixtures keep working.
    public string Protect(string plaintext) =>
        "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

    public string Unprotect(string protectedValue) =>
        protectedValue.StartsWith("enc:", StringComparison.Ordinal)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["enc:".Length..]))
            : protectedValue;
}

public class MockHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; set; }
    public string? LastRequestBody { get; set; }
    public int RequestCount { get; set; }
    public Func<HttpRequestMessage, HttpResponseMessage>? MockResponse { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        RequestCount++;
        LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

        if (MockResponse != null)
        {
            return Task.FromResult(MockResponse(request));
        }

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"msg_mock\"}", System.Text.Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

public class TestStartup
{
    public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddRouting();
        services.AddSingleton<MockHttpMessageHandler>();
        services.AddHttpClient("Upstream").ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<MockHttpMessageHandler>());
        services.AddSingleton<AerolinkManager.Core.Configuration.JsonFileStore>();
        services.AddSingleton<AerolinkManager.Core.Routing.RoutePlanner>();
        services.AddSingleton<AerolinkManager.Core.Security.ISecretProtector, FakeSecretProtector>();
        var usageStore = new RecordingUsageStore();
        services.AddSingleton<AerolinkManager.Core.Storage.IUsageStore>(usageStore);
        services.AddSingleton(usageStore);

        services.AddSingleton<ClaudeManager.Gateway.Logging.SecretRegistry>(sp => {
            var store = sp.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
            var protector = sp.GetRequiredService<AerolinkManager.Core.Security.ISecretProtector>();
            var config = store.LoadConfig();
            var registry = new ClaudeManager.Gateway.Logging.SecretRegistry();
            var stored = config.Gateway.LocalAuthTokenEncrypted;
            if (!string.IsNullOrEmpty(stored)) {
                registry.RegisterSecret(stored);
                try { registry.RegisterSecret(protector.Unprotect(stored)); } catch { }
            }
            return registry;
        });
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseMiddleware<ClaudeManager.Gateway.Logging.SanitizationMiddleware>();
        app.UseMiddleware<ClaudeManager.Gateway.Security.LocalAuthMiddleware>();

        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/health", () => Microsoft.AspNetCore.Http.Results.Ok(new { status = "healthy" }));
            ClaudeManager.Gateway.Endpoints.GatewayEndpoints.MapClaudeEndpoints(endpoints);
        });
    }
}
