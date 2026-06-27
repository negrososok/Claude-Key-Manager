using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ClaudeManager.Gateway;
using ClaudeManager.Gateway.Logging;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Security;

namespace AerolinkManager.Tests.Gateway;

/// <summary>
/// Exercises the REAL production wiring in <see cref="Program.Build"/> (not the
/// mirrored TestStartup): loopback binding, local-auth rejection/acceptance, the
/// redacting logger provider and the mapped endpoints. The only override is the
/// AppPaths singleton so the test reads an isolated config directory.
/// </summary>
[TestClass]
public sealed class GatewayProgramWiringTests
{
    private const string TokenPlaintext = "LOCAL_TOKEN_SHOULD_NOT_LEAK";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _tempDir = null!;
    private AppPaths _appPaths = null!;
    private string _ciphertext = null!;
    private readonly InMemoryLoggerProvider _loggerProvider = new();

    [TestInitialize]
    public async Task Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AerolinkGatewayProgram", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appPaths = new AppPaths(_tempDir);

        // Seed the PROTECTED token into the isolated config BEFORE Program.Build, so the
        // SecretRegistry (resolved during build) already knows the secret to redact.
        // The token is stored as DPAPI ciphertext; the plaintext is never persisted.
        _ciphertext = new WindowsDpapiSecretProtector().Protect(TokenPlaintext);
        new JsonFileStore(_appPaths).UpdateConfig(c => c with { Gateway = c.Gateway with { LocalAuthTokenEncrypted = _ciphertext } });

        _app = Program.Build(["--Gateway:Port=0"], builder =>
        {
            builder.Services.AddSingleton<AppPaths>(_appPaths);
            builder.Logging.AddProvider(_loggerProvider);
        });

        await _app.StartAsync();

        var server = _app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var uri = new Uri(address.Replace("localhost", "127.0.0.1"));
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{uri.Port}") };
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _client?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [TestMethod]
    public void Program_BindsOnlyToLoopback()
    {
        var server = _app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.IsTrue(addresses.Count > 0);
        foreach (var address in addresses)
        {
            Assert.IsTrue(address.Contains("127.0.0.1") || address.Contains("localhost"), $"Non-loopback address: {address}");
            Assert.IsFalse(address.Contains("0.0.0.0"));
        }
    }

    [TestMethod]
    public void Program_RegistersRedactingLoggerProvider()
    {
        var providers = _app.Services.GetServices<ILoggerProvider>();
        Assert.IsTrue(providers.Any(p => p is RedactingLoggerProvider), "Production wiring must wrap logging in RedactingLoggerProvider.");
    }

    [TestMethod]
    public async Task Program_Health_RequiresToken()
    {
        var anonymous = await _client.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var authed = new HttpRequestMessage(HttpMethod.Get, "/health");
        authed.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        var ok = await _client.SendAsync(authed);
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
    }

    [TestMethod]
    public async Task Program_MapsModelsEndpoint()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("x-api-key", "LOCAL_TOKEN_SHOULD_NOT_LEAK");
        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "list");
    }

    [TestMethod]
    public void LocalToken_NotStoredAsPlaintext()
    {
        var configText = File.ReadAllText(_appPaths.ConfigFile);
        Assert.IsFalse(configText.Contains(TokenPlaintext), "Local gateway token must not be stored as plaintext in config.");

        // The stored ciphertext must round-trip back to the plaintext via the protector.
        var store = _app.Services.GetRequiredService<JsonFileStore>();
        var stored = store.LoadConfig().Gateway.LocalAuthTokenEncrypted;
        Assert.IsNotNull(stored);
        Assert.AreNotEqual(TokenPlaintext, stored);
        Assert.AreEqual(TokenPlaintext, new WindowsDpapiSecretProtector().Unprotect(stored!));
    }

    [TestMethod]
    public async Task Program_WrongToken_Rejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("x-api-key", "definitely-not-the-token");
        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Program_TokenValues_RedactedInLogs()
    {
        // Make a request so the auth path runs, then force-log both forms.
        var authed = new HttpRequestMessage(HttpMethod.Get, "/health");
        authed.Headers.Add("x-api-key", TokenPlaintext);
        await _client.SendAsync(authed);

        var logger = _app.Services.GetRequiredService<ILogger<GatewayProgramWiringTests>>();
        logger.LogInformation("token {Plain} cipher {Cipher}", TokenPlaintext, _ciphertext);

        foreach (var log in _loggerProvider.Logs)
        {
            Assert.IsFalse(log.Contains(TokenPlaintext), "Plaintext local token leaked in logs!");
            Assert.IsFalse(log.Contains(_ciphertext), "Encrypted local token leaked in logs!");
        }
    }
}
