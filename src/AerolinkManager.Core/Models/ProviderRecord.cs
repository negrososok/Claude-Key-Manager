namespace AerolinkManager.Core.Models;

public enum ProviderType
{
    AnthropicOfficial,
    AnthropicCompatible,
    CustomAnthropicCompatible,
    OpenAiCompatible,
    OpenAiResponsesCompatible,
    CustomAdapter,
    Unknown
}

public enum ProviderSupportLevel
{
    Supported,
    Partial,
    Experimental,
    NotSupportedYet
}

public sealed record ProviderRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ProviderType Type { get; init; }
    public string? BaseUrl { get; init; }
    public bool Enabled { get; init; } = true;
    public string? DefaultModelId { get; init; }
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SupportsModelDiscovery { get; init; }
    public bool ModelDiscoveryEnabled { get; init; }
    public string? QuotaPolicyId { get; init; }
    public string? ErrorPatternSetId { get; init; }
    public ProviderAuthScheme AuthScheme { get; init; } = ProviderAuthScheme.XApiKey;
    public string? CustomAuthHeader { get; init; }
    public bool GatewayEnabled { get; init; } = true;
    public ProviderCapabilities Capabilities { get; init; } = new();

    /// <summary>
    /// Optional extra HTTP headers the Gateway forwards upstream to this provider.
    /// These are HTTP headers (Gateway Mode only), NOT process environment variables —
    /// see <see cref="Env"/> for launcher env vars. Protected headers (auth, content,
    /// host, local gateway token) are never forwarded; see <see cref="ProviderHeaderRules"/>.
    /// </summary>
    public Dictionary<string, string> CustomHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ProviderCompatibility
{
    public static bool IsAnthropicCompatible(ProviderRecord provider) =>
        provider.Type is ProviderType.AnthropicOfficial
            or ProviderType.AnthropicCompatible
            or ProviderType.CustomAnthropicCompatible;

    public static bool IsLauncherCompatible(ProviderRecord provider) =>
        provider.Enabled && IsAnthropicCompatible(provider);

    public static bool IsGatewayCompatible(ProviderRecord provider) =>
        provider.Enabled && provider.GatewayEnabled && IsAnthropicCompatible(provider);

    public static ProviderSupportLevel SupportLevel(ProviderRecord provider) => provider.Type switch
    {
        ProviderType.AnthropicOfficial => ProviderSupportLevel.Supported,
        ProviderType.AnthropicCompatible => ProviderSupportLevel.Supported,
        ProviderType.CustomAnthropicCompatible => ProviderSupportLevel.Experimental,
        ProviderType.CustomAdapter => ProviderSupportLevel.Partial,
        ProviderType.OpenAiCompatible => ProviderSupportLevel.NotSupportedYet,
        ProviderType.OpenAiResponsesCompatible => ProviderSupportLevel.NotSupportedYet,
        _ => ProviderSupportLevel.NotSupportedYet
    };

    public static string ProtocolResourceKey(ProviderRecord provider) => provider.Type switch
    {
        ProviderType.AnthropicOfficial => "ProviderProtocolAnthropic",
        ProviderType.AnthropicCompatible => "ProviderProtocolAnthropic",
        ProviderType.CustomAnthropicCompatible => "ProviderProtocolAnthropic",
        ProviderType.OpenAiCompatible => "ProviderProtocolOpenAi",
        ProviderType.OpenAiResponsesCompatible => "ProviderProtocolResponses",
        ProviderType.CustomAdapter => "ProviderProtocolCustomAdapter",
        _ => "ProviderProtocolUnknown"
    };

    public static string SupportResourceKey(ProviderRecord provider) => SupportLevel(provider) switch
    {
        ProviderSupportLevel.Supported => "ProviderSupportSupported",
        ProviderSupportLevel.Partial => "ProviderSupportPartial",
        ProviderSupportLevel.Experimental => "ProviderSupportExperimental",
        _ => "ProviderSupportNotSupportedYet"
    };
}

/// <summary>
/// Shared rules for which HTTP header names are protected and must never be
/// settable as a provider custom header (they are controlled by the auth scheme,
/// the request body, or the local gateway). Used by both config validation and
/// the Gateway upstream forwarding path (defense in depth).
/// </summary>
public static class ProviderHeaderRules
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "x-api-key",
        "anthropic-api-key",
        "content-type",
        "content-length",
        "host",
    };

    private const string LocalGatewayPrefix = "x-claude-manager-";

    /// <summary>True when the header name is controlled by the system and must not be a provider custom header.</summary>
    public static bool IsProtected(string headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return true;
        }

        var trimmed = headerName.Trim();
        return Blocked.Contains(trimmed)
            || trimmed.StartsWith(LocalGatewayPrefix, StringComparison.OrdinalIgnoreCase);
    }
}

public static class ProviderPresets
{
    public const string AerolinkId = "aerolink";
    public const string AnthropicOfficialId = "anthropic-official";
    public const string AerolinkQuotaPolicyId = "aerolink-composite";

    public static ProviderRecord Aerolink() => new()
    {
        Id = AerolinkId,
        Name = "Aerolink",
        Type = ProviderType.AnthropicCompatible,
        BaseUrl = "https://capi.aerolink.lat/",
        SupportsModelDiscovery = true,
        Capabilities = new ProviderCapabilities { Models = true, RateLimitHeaders = true },
        QuotaPolicyId = AerolinkQuotaPolicyId,
        ErrorPatternSetId = "aerolink"
    };

    public static ProviderRecord AnthropicOfficial() => new()
    {
        Id = AnthropicOfficialId,
        Name = "Anthropic Official",
        Type = ProviderType.AnthropicOfficial,
        BaseUrl = null,
        Capabilities = new ProviderCapabilities { Models = true, RateLimitHeaders = true },
        QuotaPolicyId = "none",
        ErrorPatternSetId = "anthropic"
    };

    public static ProviderRecord Custom(string id = "custom-anthropic-compatible", string name = "Custom Anthropic-Compatible") => new()
    {
        Id = id,
        Name = name,
        Type = ProviderType.CustomAnthropicCompatible,
        QuotaPolicyId = "manual",
        ErrorPatternSetId = "generic-anthropic-compatible"
    };

    public static List<ProviderRecord> Defaults() => [];
}
