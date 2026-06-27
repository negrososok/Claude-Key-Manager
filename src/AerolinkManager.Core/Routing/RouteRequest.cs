namespace AerolinkManager.Core.Routing;

/// <summary>
/// Per-request routing inputs. For the live gateway these come from the HTTP
/// request (headers + body); for the simulator they are supplied by the user.
/// </summary>
public sealed record RouteRequest
{
    public string? RequestId { get; init; }
    public string? SessionId { get; init; }
    public string? AgentId { get; init; }
    public string? ParentAgentId { get; init; }

    /// <summary>Explicit profile to route through; falls back to the default profile when null.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Model requested in the message body by Claude Code / the user, if any.</summary>
    public string? RequestedModel { get; init; }

    public bool Streaming { get; init; }

    /// <summary>Optional token estimates used to compute an estimated cost for the chosen route.</summary>
    public long? EstimatedInputTokens { get; init; }
    public long? EstimatedOutputTokens { get; init; }
    public long? EstimatedCacheReadTokens { get; init; }
    public long? EstimatedCacheWriteTokens { get; init; }
}
