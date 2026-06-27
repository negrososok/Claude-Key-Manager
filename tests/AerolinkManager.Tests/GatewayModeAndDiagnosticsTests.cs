using System.Net;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Diagnostics;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Security;
using AerolinkManager.Core.Selection;
using AerolinkManager.Core.Wrapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class GatewayModeAndDiagnosticsTests
{
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => protectedValue.StartsWith("enc:", StringComparison.Ordinal)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["enc:".Length..]))
            : protectedValue;
    }

    private string _root = null!;
    private AppPaths _paths = null!;
    private JsonFileStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClaudeManagerM05", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_root);
        _store = new JsonFileStore(_paths);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Env building ──

    [TestMethod]
    public void GatewayModeEnv_ContainsLocalToken_NotRealKey()
    {
        const string localToken = "LOCAL_TOKEN_SHOULD_NOT_LEAK";
        const string realKey = "UPSTREAM_KEY_SHOULD_NOT_LEAK";
        var env = new LaunchEnvironmentBuilder().BuildGatewayMode(17844, localToken, "claude-opus");

        Assert.AreEqual(localToken, env["ANTHROPIC_API_KEY"], "Gateway Mode must pass the LOCAL token as ANTHROPIC_API_KEY.");
        Assert.AreEqual("http://127.0.0.1:17844", env["ANTHROPIC_BASE_URL"]);
        Assert.AreEqual("1", env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.AreEqual("claude-opus", env["ANTHROPIC_MODEL"]);

        // The real provider key must NOT appear anywhere in the env.
        foreach (var pair in env)
        {
            Assert.IsFalse(pair.Value.Contains(realKey), $"Real provider key leaked into env var {pair.Key}!");
        }
    }

    [TestMethod]
    public void GatewayModeEnv_NoModelOverride_OmitsModel()
    {
        var env = new LaunchEnvironmentBuilder().BuildGatewayMode(17844, "tok", null);
        Assert.IsFalse(env.ContainsKey("ANTHROPIC_MODEL"));
    }

    [TestMethod]
    public void LauncherModeEnv_StillContainsRealProviderKey()
    {
        const string realKey = "sk-ant-real-provider-key-123";
        var provider = ProviderPresets.Aerolink();
        var profile = new LaunchProfile { Id = "default", Name = "Default", ProviderIds = [provider.Id] };
        var key = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = provider.Id, Name = "k", ApiKeyEncrypted = "enc" };
        var decision = new LaunchDecision(profile, provider, key, null, false, [key], null);

        var env = new LaunchEnvironmentBuilder().Build(decision, realKey);

        Assert.AreEqual(realKey, env["ANTHROPIC_API_KEY"], "Launcher Mode must still pass the real provider key (Stage 2 behaviour).");
        Assert.AreEqual(provider.BaseUrl, env["ANTHROPIC_BASE_URL"]);
    }

    // ── Gateway lifecycle ──

    private static WebApplication StartFakeHealthGateway(int port, string expectedToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/health", (HttpRequest request) =>
            request.Headers.TryGetValue("x-api-key", out var token) && token == expectedToken
                ? Results.Ok(new { status = "healthy" })
                : Results.Unauthorized());
        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }

    private static int FreeLoopbackPort()
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [TestMethod]
    public async Task EnsureReady_ReusesExistingHealthyGateway()
    {
        var protector = new FakeProtector();
        var port = FreeLoopbackPort();
        var app = StartFakeHealthGateway(port, "TOKEN");
        try
        {
            _store.UpdateConfig(c => c with { Gateway = c.Gateway with { Port = port, LocalAuthTokenEncrypted = protector.Protect("TOKEN") } });
            // State points at the already-running fake gateway (PID = current process so it's "alive").
            _store.SaveState(new ManagerState { GatewayPort = port, GatewayProcessId = Environment.ProcessId });

            var mgr = new GatewayProcessManager(_paths, _store, protector);
            var (resultPort, token) = await mgr.EnsureReadyAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(port, resultPort, "Should reuse the existing healthy gateway port.");
            Assert.AreEqual("TOKEN", token);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task EnsureReady_StalePid_IsCleaned()
    {
        var protector = new FakeProtector();
        // Use a port that is NOT listening, with a short timeout.
        // The gateway will try to start via StartGateway — if the exe exists it will launch
        // and the health check will fail (no actual upstream / proper config) and time out.
        // Either way the stale PID must be cleared from state.
        var port = FreeLoopbackPort();
        _store.UpdateConfig(c => c with { Gateway = c.Gateway with { Port = port, LocalAuthTokenEncrypted = protector.Protect("TOKEN") } });
        _store.SaveState(new ManagerState { GatewayPort = port, GatewayProcessId = 999_999 });

        var mgr = new GatewayProcessManager(_paths, _store, protector, startGateway: _ => null);
        try { await mgr.EnsureReadyAsync(TimeSpan.FromMilliseconds(500)); } catch { }

        var state = _store.LoadState();
        Assert.IsNull(state.GatewayProcessId, "Stale PID must be cleared when the process doesn't exist.");
    }

    [TestMethod]
    public void Stop_ClearsGatewayRuntimeState_EvenWhenPidIsStale()
    {
        var protector = new FakeProtector();
        _store.SaveState(new ManagerState
        {
            GatewayPort = 17844,
            GatewayProcessId = 999_999,
            GatewayStartedAt = DateTimeOffset.UtcNow,
            GatewayError = "old error"
        });

        var mgr = new GatewayProcessManager(_paths, _store, protector, startGateway: _ => null);
        Assert.IsTrue(mgr.Stop());

        var state = _store.LoadState();
        Assert.IsNull(state.GatewayProcessId);
        Assert.IsNull(state.GatewayPort);
        Assert.IsNull(state.GatewayStartedAt);
        Assert.IsNull(state.GatewayError);
    }

    [TestMethod]
    public async Task EnsureReady_NoTokenConfigured_CreatesLocalToken()
    {
        var protector = new FakeProtector();
        _store.UpdateConfig(c => c with { Gateway = c.Gateway with { LocalAuthTokenEncrypted = null } });
        var mgr = new GatewayProcessManager(_paths, _store, protector, startGateway: _ => null);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await mgr.EnsureReadyAsync(TimeSpan.FromMilliseconds(500)));

        var stored = _store.LoadConfig().Gateway.LocalAuthTokenEncrypted;
        Assert.IsFalse(string.IsNullOrWhiteSpace(stored), "Gateway should create a local token automatically.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(protector.Unprotect(stored!)));
    }

    // ── Diagnostics redaction ──

    [TestMethod]
    public void Diagnostics_RedactSecretsAndPrompts()
    {
        var protector = new FakeProtector();
        const string realKey = "sk-ant-realkey-DIAG";
        const string localToken = "LOCAL_TOKEN_DIAG";

        var config = new ManagerConfig
        {
            Keys =
            [
                new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = ProviderPresets.AerolinkId, Name = "Account 1", ApiKeyEncrypted = protector.Protect(realKey) }
            ],
            Gateway = new GatewaySettings { LocalAuthTokenEncrypted = protector.Protect(localToken), RoutingMode = RoutingMode.LocalGateway }
        };
        var state = new ManagerState { GatewayPort = 17844, GatewayProcessId = 1234 };
        // Real logs go through SecretSanitizer before writing; prompt markers and secrets
        // are stripped. Test that any key/token values (plain or encrypted) that survive
        // into logs are scrubbed by the diagnostics exporter.
        var logs = new[]
        {
            "claude_run profile=Default exit=0",
            $"x-api-key: {realKey}"
        };

        var report = DiagnosticsExporter.Export(config, state, protector, logs);

        // The report must NOT contain raw secrets or encrypted blobs.
        Assert.IsFalse(report.Contains(realKey), "Real API key leaked in diagnostics!");
        Assert.IsFalse(report.Contains(localToken), "Local token leaked in diagnostics!");
        Assert.IsFalse(report.Contains(protector.Protect(realKey)), "Encrypted key blob leaked in diagnostics!");
        Assert.IsFalse(report.Contains(protector.Protect(localToken)), "Encrypted token blob leaked in diagnostics!");

        // But it must contain useful non-secret context.
        StringAssert.Contains(report, "Routing mode: LocalGateway");
        StringAssert.Contains(report, "Account 1");
        StringAssert.Contains(report, "Count: 1");
    }

    [TestMethod]
    public void Diagnostics_DoesNotEmitKeyCiphertext()
    {
        var protector = new FakeProtector();
        var cipher = protector.Protect("anything-secret");
        var config = new ManagerConfig
        {
            Keys = [new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = "p", Name = "K", ApiKeyEncrypted = cipher }]
        };
        var report = DiagnosticsExporter.Export(config, new ManagerState(), protector, null);
        Assert.IsFalse(report.Contains(cipher), "Ciphertext must never appear in diagnostics.");
        Assert.IsFalse(report.Contains("enc:"), "Encrypted blob prefix must never appear in diagnostics.");
    }
}
