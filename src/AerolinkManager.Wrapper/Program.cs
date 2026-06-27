using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Diagnostics;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Quota;
using AerolinkManager.Core.Security;
using AerolinkManager.Core.Selection;
using AerolinkManager.Core.Wrapper;

return await WrapperProgram.RunAsync(args);

internal static class WrapperProgram
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var paths = AppPaths.Default;
        var store = new JsonFileStore(paths);
        var logger = new AppLogger(paths);
        try
        {
            var now = DateTimeOffset.Now;
            var config = store.LoadConfig();
            if (string.IsNullOrWhiteSpace(config.RealClaudePath))
            {
                return Fail("Claude Manager has not detected Claude Code. Open the app and retry detection.", 2);
            }

            var protector = new WindowsDpapiSecretProtector();

            if (config.Gateway.RoutingMode != RoutingMode.LocalGateway)
            {
                config = config with { Gateway = config.Gateway with { RoutingMode = RoutingMode.LocalGateway } };
                store.SaveConfig(config);
            }

            // Gateway-only product path: Claude Code always talks to the local gateway.
            return await RunGatewayModeAsync(config, args, now, store, logger, paths, protector).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            try
            {
                store.SaveState(new ManagerState
                {
                    NotificationId = Guid.NewGuid(),
                    NotificationTitle = "Claude Manager error",
                    NotificationMessage = exception.Message
                });
            }
            catch { }
            logger.Write("wrapper_error", exception.Message);
            return Fail($"Claude Manager could not start Claude Code: {exception.Message}", 3);
        }
    }

    private static async Task<int> RunLauncherModeAsync(
        ManagerConfig config,
        string[] args,
        DateTimeOffset now,
        JsonFileStore store,
        AppLogger logger,
        WindowsDpapiSecretProtector protector)
    {
        var decision = new LaunchPlanner().Plan(
            config, args, now,
            Environment.GetEnvironmentVariable("CLAUDE_MANAGER_PROFILE"));
        if (decision is not null && !decision.NormalizedKeys.SequenceEqual(config.Keys))
        {
            config = config with { Keys = decision.NormalizedKeys.ToList() };
            store.SaveConfig(config);
        }

        if (decision is null)
        {
            var nearestReset = new KeySelector().Select(config.Keys, now).NearestReset;
            var reset = nearestReset is null
                ? "Reset time is unknown."
                : $"Nearest reset in {FormatDuration(nearestReset.Value - now)}.";
            logger.Write("no_available_keys", reset);
            store.SaveState(new ManagerState { LastRunAt = now, NotificationId = Guid.NewGuid(), NotificationTitle = "No available API keys", NotificationMessage = reset });
            return Fail($"No available API keys for the selected launch profile. {reset}", 4);
        }

        var selected = decision.Key;
        var apiKey = protector.Unprotect(selected.ApiKeyEncrypted);
        store.UpdateConfig(latest => latest with
        {
            Keys = latest.Keys.Select(key => key.Id == selected.Id
                ? key with { Status = KeyStatus.Active, LastUsedAt = now, TotalRuns = key.TotalRuns + 1 }
                : key).ToList()
        });
        store.SaveState(new ManagerState { CurrentKeyId = selected.Id, CurrentProfileId = decision.Profile.Id, CurrentProviderId = decision.Provider.Id, CurrentModel = decision.ResolvedModel, LastRunAt = now, NotificationId = Guid.NewGuid(), NotificationTitle = "Claude launch prepared", NotificationMessage = $"{decision.Provider.Name} / {selected.Name}" });

        var plan = WrapperLaunchPlan.Create(config.RealClaudePath!, Environment.ProcessPath ?? string.Empty, Environment.CurrentDirectory, args);
        var env = new LaunchEnvironmentBuilder().Build(decision, apiKey);
        return await RunAndFinalizeAsync(config, args, now, store, logger, decision.Profile, decision.Provider, selected, decision.ResolvedModel, plan, env, apiKey).ConfigureAwait(false);
    }

    private static async Task<int> RunGatewayModeAsync(
        ManagerConfig config,
        string[] args,
        DateTimeOffset now,
        JsonFileStore store,
        AppLogger logger,
        AppPaths paths,
        WindowsDpapiSecretProtector protector)
    {
        // Launch the local gateway — start a new process, or reuse an existing one.
        var gateway = new GatewayProcessManager(paths, store, protector);
        var (port, token) = await gateway.EnsureReadyAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);

        // Build Gateway Mode environment: local loopback URL + local token only.
        // NO real upstream provider key is ever put into Claude Code's environment.
        // model override: resolve profile override, but respect user-supplied --model.
        var profile = new LaunchPlanner().Plan(config, args, now, Environment.GetEnvironmentVariable("CLAUDE_MANAGER_PROFILE"));
        string? modelOverride = null;
        if (profile is not null && !LaunchPlanner.HasModelArgument(args))
        {
            modelOverride = profile.ResolvedModel;
        }

        var env = new LaunchEnvironmentBuilder().BuildGatewayMode(port, token, modelOverride);

        store.SaveState(new ManagerState
        {
            CurrentProfileId = profile?.Profile?.Id ?? config.LaunchProfiles.FirstOrDefault(p => p.IsDefault)?.Id,
            CurrentModel = modelOverride,
            LastRunAt = now,
            NotificationId = Guid.NewGuid(),
            NotificationTitle = "Gateway Mode launch",
            NotificationMessage = $"http://127.0.0.1:{port}"
        });

        var plan = WrapperLaunchPlan.Create(config.RealClaudePath!, Environment.ProcessPath ?? string.Empty, Environment.CurrentDirectory, args);
        var result = await new ClaudeProcessRunner().RunAsync(plan, env).ConfigureAwait(false);

        // In Gateway Mode, quota/limit errors are handled per-request by the gateway.
        // We still capture coarse output for logging but don't apply key-level limits.
        var sanitized = new SecretSanitizer().Sanitize(result.CapturedOutput, [token]);
        var shortError = string.IsNullOrWhiteSpace(sanitized) ? null
            : sanitized.ReplaceLineEndings(" ").Trim()[..Math.Min(300, sanitized.ReplaceLineEndings(" ").Trim().Length)];

        store.SaveState(new ManagerState
        {
            CurrentKeyId = null,
            LastRunAt = now,
            NotificationId = result.ExitCode != 0 ? Guid.NewGuid() : null,
            NotificationTitle = result.ExitCode != 0 ? "Gateway Mode run" : null,
            NotificationMessage = result.ExitCode != 0 ? $"Exit code {result.ExitCode}" : null
        });

        logger.Write("claude_run_gateway",
            $"mode=gateway; port={port}; cwd={Environment.CurrentDirectory}; args={string.Join(' ', args)}; exit={result.ExitCode}",
            [token]);
        return result.ExitCode;
    }

    private static async Task<int> RunAndFinalizeAsync(
        ManagerConfig config,
        string[] args,
        DateTimeOffset now,
        JsonFileStore store,
        AppLogger logger,
        LaunchProfile profile,
        ProviderRecord provider,
        ApiKeyRecord key,
        string? resolvedModel,
        WrapperLaunchPlan plan,
        IReadOnlyDictionary<string, string> env,
        string apiKey)
    {
        var result = await new ClaudeProcessRunner().RunAsync(plan, env).ConfigureAwait(false);
        var errorPatterns = config.ErrorPatternSets.FirstOrDefault(p => p.Id == provider.ErrorPatternSetId);
        var classification = new QuotaErrorClassifier().Classify(result.CapturedOutput, errorPatterns);
        var sanitized = new SecretSanitizer().Sanitize(result.CapturedOutput, [apiKey]);
        var shortError = string.IsNullOrWhiteSpace(sanitized) ? null
            : sanitized.ReplaceLineEndings(" ").Trim()[..Math.Min(300, sanitized.ReplaceLineEndings(" ").Trim().Length)];

        store.UpdateConfig(latest => latest with
        {
            Keys = latest.Keys.Select(k => k.Id == key.Id
                ? new QuotaStateUpdater().Apply(k with { Status = KeyStatus.Available, LastSuccessAt = result.ExitCode == 0 ? now : k.LastSuccessAt, LastErrorAt = result.ExitCode == 0 ? k.LastErrorAt : now, LastErrorText = result.ExitCode == 0 ? k.LastErrorText : shortError, FailedRuns = k.FailedRuns + (result.ExitCode == 0 ? 0 : 1) }, classification, now, shortError)
                : k).ToList()
        });
        store.SaveState(new ManagerState { CurrentKeyId = null, CurrentProfileId = profile.Id, CurrentProviderId = provider.Id, CurrentModel = resolvedModel, LastRunAt = now, NotificationId = classification.Type == QuotaLimitType.None ? null : Guid.NewGuid(), NotificationTitle = classification.Type == QuotaLimitType.None ? null : "Provider quota detected", NotificationMessage = classification.Type == QuotaLimitType.None ? null : $"{key.Name}: {classification.Type}" });
        logger.Write("claude_run", $"profile={profile.Name}; provider={provider.Name}; key={key.Name}; model={resolvedModel ?? "claude-default"}; cwd={Environment.CurrentDirectory}; args={string.Join(' ', args)}; exit={result.ExitCode}; limit={classification.Type}; reset={classification.ResetAfter}", [apiKey]);
        return result.ExitCode;
    }

    private static int Fail(string message, int exitCode)
    {
        Console.Error.WriteLine(message);
        return exitCode;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var value = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{Math.Max(1, value.Minutes)}m";
    }
}
