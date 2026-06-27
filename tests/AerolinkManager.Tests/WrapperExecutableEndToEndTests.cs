using System.Diagnostics;
using System.Net;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AerolinkManager.Tests;

[TestClass]
[TestCategory("Wrapper")]
public sealed class WrapperExecutableEndToEndTests
{
    [TestMethod]
    public async Task Executable_StartsGateway_ForwardsProcessWithoutSecretLeak()
    {
        var testRoot = Path.Combine(Path.GetFullPath("."), "TestResults", "wrapper e2e", Guid.NewGuid().ToString("N"));
        var workingDirectory = Path.Combine(testRoot, "working folder");
        Directory.CreateDirectory(workingDirectory);
        const string secret = "test-secret-wrapper-only";
        AppPaths? paths = null;
        WebApplication? gateway = null;
        try
        {
            paths = new AppPaths(Path.Combine(testRoot, "appdata"));
            var script = Path.Combine(testRoot, "fake claude.ps1");
            File.WriteAllText(script, "Write-Output ('cwd=' + (Get-Location).Path); Write-Output ('arg=' + $args[0]); Write-Output ('base=' + $env:ANTHROPIC_BASE_URL); Write-Error 'usage limit resets in 32m'; exit 29");
            var keyId = Guid.NewGuid();
            var gatewayPort = FreeLoopbackPort();
            const string localGatewayToken = "local-wrapper-token";
            gateway = StartFakeHealthGateway(gatewayPort, localGatewayToken);
            new JsonFileStore(paths).SaveConfig(new ManagerConfig
            {
                RealClaudePath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                Gateway = new GatewaySettings
                {
                    RoutingMode = RoutingMode.LocalGateway,
                    Port = gatewayPort,
                    LocalAuthTokenEncrypted = new WindowsDpapiSecretProtector().Protect(localGatewayToken)
                },
                Keys = [new ApiKeyRecord
                {
                    Id = keyId,
                    Name = "Account 1",
                    ApiKeyEncrypted = new WindowsDpapiSecretProtector().Protect(secret),
                    AddedOrder = 1
                }]
            });
            new JsonFileStore(paths).SaveState(new ManagerState { GatewayPort = gatewayPort, GatewayProcessId = Environment.ProcessId });

            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var wrapper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.Wrapper", "bin", configuration, "net8.0-windows", "ClaudeManager.Wrapper.exe"));
            Assert.IsTrue(File.Exists(wrapper), $"Wrapper missing at {wrapper}");

            var start = new ProcessStartInfo
            {
                FileName = wrapper,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["CLAUDE_MANAGER_HOME"] = paths.RootDirectory;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "value with spaces" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdoutTask + await stderrTask;

            Assert.AreEqual(29, process.ExitCode);
            StringAssert.Contains(output, $"cwd={workingDirectory}");
            StringAssert.Contains(output, "arg=value with spaces");
            StringAssert.Contains(output, $"base=http://127.0.0.1:{gatewayPort}");
            var saved = new JsonFileStore(paths).LoadConfig().Keys.Single();
            Assert.AreEqual(keyId, saved.Id);
            Assert.AreEqual(KeyStatus.Available, saved.Status);
            Assert.IsNull(saved.FiveHourResetAt);
            Assert.AreEqual(0, saved.TotalRuns);
            Assert.AreEqual(0, saved.FailedRuns);
            var persisted = string.Join("\n", Directory.GetFiles(paths.RootDirectory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.IsFalse(persisted.Contains(secret, StringComparison.Ordinal));
        }
        finally
        {
            if (gateway is not null)
            {
                await gateway.StopAsync();
                await gateway.DisposeAsync();
            }
            if (paths is not null)
            {
                try
                {
                    var pid = new JsonFileStore(paths).LoadState().GatewayProcessId;
                    if (pid is not null)
                    {
                        Process.GetProcessById(pid.Value).Kill(entireProcessTree: true);
                    }
                }
                catch { }
            }
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
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
