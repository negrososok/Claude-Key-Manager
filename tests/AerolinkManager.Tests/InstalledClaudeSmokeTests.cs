using System.Diagnostics;
using System.Net;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Managed;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AerolinkManager.Tests;

[TestClass]
[TestCategory("LocalClaude")]
public sealed class InstalledClaudeSmokeTests
{
    [TestMethod]
    public async Task Wrapper_LaunchesInstalledClaudeHelpWithoutNetworkCredentialUse()
    {
        var root = Path.Combine(Path.GetFullPath("."), "TestResults", "installed claude", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string testSecret = "local-smoke-test-not-a-real-key";
        WebApplication? gateway = null;
        Process? process = null;
        try
        {
            var paths = new AppPaths(Path.Combine(root, "appdata"));
            // Resolve the real Claude executable exactly the way the product does,
            // so the smoke test picks a launchable candidate (not an npm shim).
            var candidate = new ClaudeDetector().FindCandidates(paths).FirstOrDefault();
            if (candidate is null)
            {
                Assert.Inconclusive("Claude Code is not installed on this machine.");
            }

            var gatewayPort = FreeLoopbackPort();
            const string localGatewayToken = "local-installed-smoke-token";
            gateway = StartFakeHealthGateway(gatewayPort, localGatewayToken);
            new JsonFileStore(paths).SaveConfig(new ManagerConfig
            {
                RealClaudePath = candidate,
                Gateway = new GatewaySettings
                {
                    RoutingMode = RoutingMode.LocalGateway,
                    Port = gatewayPort,
                    LocalAuthTokenEncrypted = new WindowsDpapiSecretProtector().Protect(localGatewayToken)
                },
                Keys = [new ApiKeyRecord
                {
                    Id = Guid.NewGuid(),
                    Name = "Local smoke key",
                    ApiKeyEncrypted = new WindowsDpapiSecretProtector().Protect(testSecret),
                    AddedOrder = 1
                }]
            });
            new JsonFileStore(paths).SaveState(new ManagerState { GatewayPort = gatewayPort, GatewayProcessId = Environment.ProcessId });
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var wrapper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.Wrapper", "bin", configuration, "net8.0-windows", "ClaudeManager.Wrapper.exe"));
            var start = new ProcessStartInfo
            {
                FileName = wrapper,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["CLAUDE_MANAGER_HOME"] = paths.RootDirectory;
            start.ArgumentList.Add("--help");

            process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout + await stderr;

            Assert.AreEqual(0, process.ExitCode, output);
            StringAssert.Contains(output.ToLowerInvariant(), "claude");
            Assert.IsFalse(output.Contains(testSecret, StringComparison.Ordinal));
            var persisted = string.Join("\n", Directory.GetFiles(paths.RootDirectory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.IsFalse(persisted.Contains(testSecret, StringComparison.Ordinal));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            process?.Dispose();
            if (gateway is not null)
            {
                await gateway.StopAsync();
                await gateway.DisposeAsync();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
}
