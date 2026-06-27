using System.Text;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Security;

namespace AerolinkManager.Core.Diagnostics;

/// <summary>
/// Produces a secret-safe diagnostics text report. Never includes API keys,
/// local gateway tokens, encrypted blobs, prompts, response bodies, or raw
/// Authorization/x-api-key headers. All secrets are redacted before output.
/// </summary>
public static class DiagnosticsExporter
{
    public static string Export(
        ManagerConfig config,
        ManagerState state,
        ISecretProtector protector,
        IReadOnlyList<string>? recentLogLines = null)
    {
        // Collect every secret value (ciphertext + decrypted plaintext) so we can scrub
        // them from any free-text section (logs, error strings). Names/IDs are safe.
        var secrets = CollectSecrets(config, protector);

        var sb = new StringBuilder();
        sb.AppendLine("=== Claude Manager Diagnostics ===");
        sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Schema version: {config.SchemaVersion}");

        // Version / build info (safe — no secrets)
        sb.AppendLine();
        sb.AppendLine("--- Build ---");
        sb.AppendLine($"App version: 1.0.0 (Stage 3)");
        sb.AppendLine($"Target: net8.0-windows");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"Machine: {Environment.MachineName}");

        // Gateway status
        sb.AppendLine();
        sb.AppendLine("--- Gateway ---");
        sb.AppendLine($"Routing mode: {config.Gateway.RoutingMode}");
        sb.AppendLine($"Configured port: {config.Gateway.Port}");
        sb.AppendLine($"Gateway PID (state): {state.GatewayProcessId?.ToString() ?? "none"}");
        sb.AppendLine($"Gateway port (state): {state.GatewayPort?.ToString() ?? "none"}");
        sb.AppendLine($"Gateway started: {state.GatewayStartedAt?.ToString("O") ?? "never"}");
        sb.AppendLine($"Gateway error: {Scrub(state.GatewayError, secrets)}");
        sb.AppendLine($"Auto-start with app: {config.Gateway.AutoStartWithApp}");
        sb.AppendLine($"Start when wrapper runs: {config.Gateway.StartWhenWrapperRuns}");
        sb.AppendLine($"Model discovery enabled: {config.Gateway.EnableModelDiscovery}");
        sb.AppendLine($"Usage tracking: {config.Gateway.UsageTrackingEnabled}");
        sb.AppendLine($"Cost tracking: {config.Gateway.CostTrackingEnabled}");
        sb.AppendLine($"Dev mode: {config.Gateway.DeveloperMode}");
        sb.AppendLine($"Max request body MB: {config.Gateway.MaxRequestBodyMb}");

        // Provider summary (no keys)
        sb.AppendLine();
        sb.AppendLine("--- Providers ---");
        sb.AppendLine($"Count: {config.Providers.Count}");
        foreach (var p in config.Providers.Take(50))
        {
            sb.AppendLine($"  {p.Id}: {p.Name} (type={p.Type}, auth={p.AuthScheme}, enabled={p.Enabled}, gateway={p.GatewayEnabled})");
            if (p.Capabilities is { } c)
            {
                sb.AppendLine($"    caps: msgs={c.Messages}, stream={c.Streaming}, countTokens={c.CountTokens}, models={c.Models}, rateHeaders={c.RateLimitHeaders}");
            }
        }

        // Key summary (names + masked IDs only, NO keys/ciphertext)
        sb.AppendLine();
        sb.AppendLine("--- Keys ---");
        sb.AppendLine($"Count: {config.Keys.Count}");
        sb.AppendLine($"Enabled: {config.Keys.Count(k => k.Enabled)}");
        sb.AppendLine($"Available/Active: {config.Keys.Count(k => k.Enabled && k.Status is Models.KeyStatus.Available or Models.KeyStatus.Active)}");
        sb.AppendLine($"Limited: {config.Keys.Count(k => k.Enabled && k.Status is Models.KeyStatus.Limited or Models.KeyStatus.WeeklyLimited)}");
        sb.AppendLine($"Disabled: {config.Keys.Count(k => !k.Enabled)}");
        foreach (var k in config.Keys.Take(100))
        {
            sb.AppendLine($"  {k.Name}: provider={k.ProviderId}, status={k.Status}, priority={k.Priority}, runs={k.Usage.Runs}, failed={k.Usage.FailedRuns}");
        }

        // Models
        sb.AppendLine();
        sb.AppendLine("--- Models ---");
        sb.AppendLine($"Count: {config.Models.Count}");
        foreach (var m in config.Models.Take(100))
        {
            sb.AppendLine($"  {m.Id}: {m.DisplayName} (value={m.ModelValue}, provider={m.ProviderId}, enabled={m.Enabled})");
        }

        // Pricing
        sb.AppendLine();
        sb.AppendLine("--- Pricing ---");
        sb.AppendLine($"Count: {config.ModelPricing.Count}");
        foreach (var p in config.ModelPricing.Take(50))
        {
            sb.AppendLine($"  {p.ModelValue}@{p.ProviderId}: in={p.InputPerMillion}/M out={p.OutputPerMillion}/M cacheR={p.CacheReadPerMillion}/M cacheW={p.CacheWritePerMillion}/M {p.Currency}");
        }

        // Profiles
        sb.AppendLine();
        sb.AppendLine("--- Profiles ---");
        foreach (var p in config.LaunchProfiles.Take(50))
        {
            sb.AppendLine($"  {p.Id}: {p.Name} (enabled={p.Enabled}, mode={p.ModelMode}, default={p.IsDefault}, chain={p.RoutingChainId}, maxRetries={p.MaxRetries})");
        }

        // Routing chains
        sb.AppendLine();
        sb.AppendLine("--- Routing Chains ---");
        foreach (var c in config.RoutingChains.Take(50))
        {
            sb.AppendLine($"  {c.Id}: {c.Name} (enabled={c.Enabled}, steps={c.Steps.Count}, maxFallback={c.MaxFallbackSteps})");
        }

        // Budgets
        sb.AppendLine();
        sb.AppendLine("--- Budgets ---");
        foreach (var b in config.BudgetPolicies.Take(50))
        {
            sb.AppendLine($"  {b.Id}: {b.Name} (scope={b.Scope}, type={b.Type}, limit={b.Limit})");
        }

        // State summary (no secrets)
        sb.AppendLine();
        sb.AppendLine("--- State ---");
        sb.AppendLine($"Current profile: {state.CurrentProfileId ?? "none"}");
        sb.AppendLine($"Current provider: {state.CurrentProviderId ?? "none"}");
        sb.AppendLine($"Current model: {state.CurrentModel ?? "none"}");
        sb.AppendLine($"Last run: {state.LastRunAt?.ToString("O") ?? "never"}");

        // Recent logs (redacted)
        sb.AppendLine();
        sb.AppendLine("--- Recent logs (secrets redacted) ---");
        if (recentLogLines is { Count: > 0 })
        {
            foreach (var line in recentLogLines.Take(200))
            {
                sb.AppendLine($"  {Scrub(line, secrets)}");
            }
        }
        else
        {
            sb.AppendLine("  (no logs available)");
        }

        return sb.ToString();
    }

    private static HashSet<string> CollectSecrets(ManagerConfig config, ISecretProtector protector)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        // Gateway local token — ciphertext + plaintext.
        var storedToken = config.Gateway.LocalAuthTokenEncrypted;
        if (!string.IsNullOrEmpty(storedToken))
        {
            secrets.Add(storedToken);
            try { secrets.Add(protector.Unprotect(storedToken)); } catch { }
        }

        // Every API key — ciphertext + plaintext.
        foreach (var key in config.Keys)
        {
            if (string.IsNullOrEmpty(key.ApiKeyEncrypted) || key.ApiKeyEncrypted.Length < 8) continue;
            secrets.Add(key.ApiKeyEncrypted);
            try { secrets.Add(protector.Unprotect(key.ApiKeyEncrypted)); } catch { }
        }

        // Provider custom header values may contain secrets (tokens, signing keys).
        // The report never prints them, but scrub them from any free-text logs too.
        foreach (var provider in config.Providers)
        {
            foreach (var value in provider.CustomHeaders.Values)
            {
                if (!string.IsNullOrWhiteSpace(value) && value.Length >= 5) secrets.Add(value);
            }
        }

        return secrets;
    }

    private static string Scrub(string? value, HashSet<string> secrets)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        var result = value;
        foreach (var secret in secrets)
        {
            if (secret.Length >= 5) result = result.Replace(secret, "[REDACTED]");
        }

        // Also scrub common header patterns that may contain secrets.
        result = result.Replace("x-api-key:", "x-api-key:[REDACTED]");
        if (result.Contains("Authorization:"))
        {
            // Authorization: Bearer/Basic ... → truncate the credential part.
            var authIndex = result.IndexOf("Authorization:", StringComparison.OrdinalIgnoreCase);
            if (authIndex >= 0)
            {
                var endLine = result.IndexOf('\n', authIndex);
                if (endLine < 0) endLine = result.Length;
                var authPart = result[authIndex..endLine];
                result = result.Replace(authPart, "Authorization: [REDACTED]");
            }
        }

        return result;
    }
}
