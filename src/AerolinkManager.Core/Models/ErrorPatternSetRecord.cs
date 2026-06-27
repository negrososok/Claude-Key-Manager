namespace AerolinkManager.Core.Models;

public sealed record ErrorPatternSetRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<string> LimitSignals { get; init; } = [];
    public List<string> WeeklySignals { get; init; } = [];

    public static List<ErrorPatternSetRecord> Defaults() =>
    [
        new() { Id = "anthropic", Name = "Anthropic", LimitSignals = ["usage limit", "rate limit", "resets in", "try again in"], WeeklySignals = ["weekly limit"] },
        new() { Id = "aerolink", Name = "Aerolink", LimitSignals = ["quota exceeded", "usage limit", "rate limit", "try again in"], WeeklySignals = ["weekly", "credits exhausted"] },
        new() { Id = "generic-anthropic-compatible", Name = "Generic Anthropic-Compatible", LimitSignals = ["429", "too many requests", "quota exceeded", "rate limit"], WeeklySignals = ["weekly", "monthly"] }
    ];
}
