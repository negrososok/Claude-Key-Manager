using System.Diagnostics;
using System.Net;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Security;

namespace AerolinkManager.Core.Wrapper;

/// <summary>
/// Manages the lifecycle of the local <c>ClaudeManager.Gateway.exe</c> process.
/// Handles starting, detecting an already-running instance (via health check),
/// stale PID recovery, port conflict resolution, and a bounded /health poll.
/// </summary>
public sealed class GatewayProcessManager
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;
    private readonly ISecretProtector _protector;
    private readonly Func<int, Process?> _startGateway;
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(15);

    public GatewayProcessManager(AppPaths paths, JsonFileStore store, ISecretProtector protector, Func<int, Process?>? startGateway = null)
    {
        _paths = paths;
        _store = store;
        _protector = protector;
        _startGateway = startGateway ?? StartGateway;
    }

    /// <summary>
    /// Ensures a gateway is listening on a loopback port, starting one if needed.
    /// Returns the port and the local plaintext auth token that Claude Code must use.
    /// </summary>
    public async Task<(int Port, string Token)> EnsureReadyAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var timeoutDuration = timeout ?? DefaultHealthTimeout;
        var config = _store.LoadConfig();
        var startingPort = ResolvePort(config);
        var token = ResolveToken(config);

        // 1. Reuse an already-healthy gateway.
        var state = _store.LoadState();
        if (state.GatewayPort is { } existingPort && state.GatewayProcessId is { } existingPid)
        {
            if (await IsHealthyAsync(existingPort, token, cancellationToken).ConfigureAwait(false))
            {
                return (existingPort, token);
            }

            // Stale PID: clean up before starting fresh.
            TryKill(existingPid);
            _store.SaveState(state with { GatewayProcessId = null, GatewayPort = null, GatewayError = null });
        }

        // 2. Find a free loopback port.
        var port = startingPort;
        for (var attempt = 0; attempt < 10 && !IsPortFree(port); attempt++)
        {
            if (await IsHealthyAsync(port, token, CancellationToken.None).ConfigureAwait(false))
            {
                // Another gateway is already running on this port — reuse it.
                return (port, token);
            }

            port = (port < 17844 || port >= 17944) ? 17844 : port + 1;
        }

        // 3. Start a new gateway process.
        var process = _startGateway(port);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start the local gateway process. Is ClaudeManager.Gateway.exe installed?");
        }

        // 4. Wait for /health to become ready.
        var deadline = DateTimeOffset.UtcNow + timeoutDuration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var error = $"Gateway process exited with code {process.ExitCode}.";
                _store.SaveState(state with { GatewayError = error, GatewayProcessId = null, GatewayPort = null });
                throw new InvalidOperationException(error);
            }

            if (await IsHealthyAsync(port, token, cancellationToken).ConfigureAwait(false))
            {
                _store.SaveState(state with
                {
                    GatewayProcessId = process.Id,
                    GatewayPort = port,
                    GatewayStartedAt = DateTimeOffset.UtcNow,
                    GatewayError = null
                });
                process.Dispose(); // let it run independently
                return (port, token);
            }

            try { await Task.Delay(HealthPollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        }

        // Timed out.
        try { process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
        _store.SaveState(state with { GatewayError = "Gateway health check timed out.", GatewayProcessId = null, GatewayPort = null });
        throw new TimeoutException($"Gateway did not become healthy within {timeoutDuration.TotalSeconds:F0} seconds on port {port}.");
    }

    /// <summary>
    /// Stops the gateway process recorded in state, if any, and clears gateway runtime state.
    /// </summary>
    public bool Stop()
    {
        var state = _store.LoadState();
        var hadGatewayState = state.GatewayProcessId.HasValue || state.GatewayPort.HasValue;

        if (state.GatewayProcessId is { } pid)
        {
            TryKill(pid);
        }

        _store.SaveState(state with
        {
            GatewayProcessId = null,
            GatewayPort = null,
            GatewayStartedAt = null,
            GatewayError = null
        });

        return hadGatewayState;
    }

    private static Process? StartGateway(int port)
    {
        var wrapperDir = AppContext.BaseDirectory;
        var gatewayExe = Path.Combine(wrapperDir, "ClaudeManager.Gateway.exe");
        if (!File.Exists(gatewayExe))
        {
            // Try relative to the wrapper binary.
            var alt = Path.GetFullPath(Path.Combine(wrapperDir, "..", "ClaudeManager.Gateway", "ClaudeManager.Gateway.exe"));
            if (File.Exists(alt))
            {
                gatewayExe = alt;
            }
            else
            {
                return null;
            }
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = gatewayExe,
            ArgumentList = { "--Gateway:Port=" + port },
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsHealthyAsync(int port, string token, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/health");
            request.Headers.TryAddWithoutValidation("x-api-key", token);
            var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string ResolveToken(ManagerConfig config)
    {
        var stored = config.Gateway.LocalAuthTokenEncrypted;
        if (string.IsNullOrEmpty(stored))
        {
            var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var encrypted = _protector.Protect(token);
            _store.UpdateConfig(current => current with
            {
                Gateway = current.Gateway with
                {
                    RoutingMode = RoutingMode.LocalGateway,
                    LocalAuthTokenEncrypted = encrypted
                }
            });
            return token;
        }

        try
        {
            return _protector.Unprotect(stored);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Gateway local auth token could not be unprotected.", ex);
        }
    }

    private int ResolvePort(ManagerConfig config)
    {
        var port = config.Gateway.Port;
        if (port <= 0 || port > 65535)
        {
            port = 17844;
        }
        return port;
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process already gone or inaccessible — that's the goal.
        }
    }
}
