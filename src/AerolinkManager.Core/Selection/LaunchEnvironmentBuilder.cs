using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Selection;

public sealed class LaunchEnvironmentBuilder
{
    public IReadOnlyDictionary<string, string> Build(LaunchDecision decision, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!ProviderCompatibility.IsLauncherCompatible(decision.Provider))
        {
            throw new InvalidOperationException($"Provider '{decision.Provider.Id}' is not Launcher-compatible. Use a tested Gateway adapter before launching this protocol.");
        }

        var environment = new Dictionary<string, string>(decision.Provider.Env, StringComparer.OrdinalIgnoreCase)
        {
            ["ANTHROPIC_API_KEY"] = apiKey,
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1"
        };
        if (decision.Provider.Type != ProviderType.AnthropicOfficial && !string.IsNullOrWhiteSpace(decision.Provider.BaseUrl))
        {
            environment["ANTHROPIC_BASE_URL"] = decision.Provider.BaseUrl;
        }
        else
        {
            environment.Remove("ANTHROPIC_BASE_URL");
        }

        if (!decision.UserSuppliedModel && !string.IsNullOrWhiteSpace(decision.ResolvedModel))
        {
            environment["ANTHROPIC_MODEL"] = decision.ResolvedModel;
        }
        else
        {
            environment.Remove("ANTHROPIC_MODEL");
        }

        if (decision.Provider.ModelDiscoveryEnabled)
        {
            environment["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "1";
        }
        else
        {
            environment.Remove("CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY");
        }

        return environment;
    }

    /// <summary>
    /// Gateway Mode: routes ALL traffic through the local gateway. The only credential
    /// exposed to Claude Code is the local auth token — the real upstream provider key
    /// stays inside the gateway and is never put into the Claude Code environment.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildGatewayMode(int port, string localToken, string? modelOverride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(localToken);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANTHROPIC_API_KEY"] = localToken,
            ["ANTHROPIC_BASE_URL"] = $"http://127.0.0.1:{port}",
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            ["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "1"
        };

        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            environment["ANTHROPIC_MODEL"] = modelOverride;
        }

        return environment;
    }
}
