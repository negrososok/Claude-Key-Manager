using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Routing;

/// <summary>
/// Deterministic ordering of eligible candidates for each Stage 3 strategy.
/// Every comparator ends with the same stable tie-breakers (AddedOrder, then
/// key Id) so identical snapshots always yield identical orderings — the
/// property the live gateway and the dry-run simulator both depend on.
/// </summary>
public static class RouteStrategies
{
    /// <summary>Maps a strategy enum value to its canonical Stage 3 reason token.</summary>
    public static string Name(SelectionStrategy strategy) => strategy switch
    {
        SelectionStrategy.PriorityOrder => "priority_order",
        SelectionStrategy.PriorityThenLru or SelectionStrategy.ProviderFallback => "priority_then_lru",
        SelectionStrategy.LeastRecentlyUsed => "least_recently_used",
        SelectionStrategy.LeastUsed => "least_used",
        SelectionStrategy.RoundRobin => "round_robin",
        SelectionStrategy.FillFirst => "fill_first",
        SelectionStrategy.CostOptimized => "cost_optimized",
        SelectionStrategy.ResetAware => "reset_aware",
        SelectionStrategy.Manual or SelectionStrategy.ManualKey => "manual",
        SelectionStrategy.Random => "random",
        _ => strategy.ToString()
    };

    /// <summary>
    /// Orders <paramref name="candidates"/> best-first for the given strategy.
    /// <paramref name="roundRobinCursor"/> only matters for round_robin;
    /// <paramref name="manualKeyId"/> only matters for manual.
    /// </summary>
    public static IReadOnlyList<RouteCandidate> Order(
        SelectionStrategy strategy,
        IReadOnlyList<RouteCandidate> candidates,
        int roundRobinCursor = 0,
        Guid? manualKeyId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count <= 1 && strategy is not (SelectionStrategy.Manual or SelectionStrategy.ManualKey))
        {
            return candidates;
        }

        return strategy switch
        {
            SelectionStrategy.PriorityOrder => PriorityOrder(candidates),
            SelectionStrategy.LeastRecentlyUsed => LeastRecentlyUsed(candidates),
            SelectionStrategy.LeastUsed => LeastUsed(candidates),
            SelectionStrategy.RoundRobin => RoundRobin(candidates, roundRobinCursor),
            SelectionStrategy.FillFirst => FillFirst(candidates),
            SelectionStrategy.CostOptimized => CostOptimized(candidates),
            SelectionStrategy.ResetAware => ResetAware(candidates),
            SelectionStrategy.Manual or SelectionStrategy.ManualKey => Manual(candidates, manualKeyId),
            SelectionStrategy.Random => PriorityThenLru(candidates), // deterministic substitute; gateway never randomizes
            _ => PriorityThenLru(candidates)
        };
    }

    private static IReadOnlyList<RouteCandidate> PriorityOrder(IReadOnlyList<RouteCandidate> c) => c
        .OrderBy(x => x.Key.Priority)
        .ThenBy(x => x.Key.AddedOrder)
        .ThenBy(x => x.Key.Id)
        .ToArray();

    private static IReadOnlyList<RouteCandidate> PriorityThenLru(IReadOnlyList<RouteCandidate> c) => c
        .OrderBy(x => x.Key.Priority)
        .ThenBy(x => x.Key.LastUsedAt.HasValue)            // never-used first
        .ThenBy(x => x.Key.LastUsedAt)
        .ThenBy(x => x.Key.AddedOrder)
        .ThenBy(x => x.Key.Id)
        .ToArray();

    private static IReadOnlyList<RouteCandidate> LeastRecentlyUsed(IReadOnlyList<RouteCandidate> c) => c
        .OrderBy(x => x.Key.LastUsedAt.HasValue)
        .ThenBy(x => x.Key.LastUsedAt)
        .ThenBy(x => x.Key.AddedOrder)
        .ThenBy(x => x.Key.Id)
        .ToArray();

    private static IReadOnlyList<RouteCandidate> LeastUsed(IReadOnlyList<RouteCandidate> c) => c
        .OrderBy(x => x.KeyRuntime.WindowRequestCount)
        .ThenBy(x => x.Key.LastUsedAt.HasValue)
        .ThenBy(x => x.Key.LastUsedAt)
        .ThenBy(x => x.Key.AddedOrder)
        .ThenBy(x => x.Key.Id)
        .ToArray();

    private static IReadOnlyList<RouteCandidate> RoundRobin(IReadOnlyList<RouteCandidate> c, int cursor)
    {
        var ordered = c
            .OrderBy(x => x.Key.AddedOrder)
            .ThenBy(x => x.Key.Id)
            .ToArray();
        if (ordered.Length == 0)
        {
            return ordered;
        }

        var start = ((cursor % ordered.Length) + ordered.Length) % ordered.Length; // safe for negative cursors
        var rotated = new RouteCandidate[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            rotated[i] = ordered[(start + i) % ordered.Length];
        }

        return rotated;
    }

    // Drain the key already in use before spreading load to a fresh one: highest
    // window usage first within the best priority group.
    private static IReadOnlyList<RouteCandidate> FillFirst(IReadOnlyList<RouteCandidate> c) => c
        .OrderBy(x => x.Key.Priority)
        .ThenByDescending(x => x.KeyRuntime.WindowRequestCount)
        .ThenBy(x => x.Key.AddedOrder)
        .ThenBy(x => x.Key.Id)
        .ToArray();

    private static IReadOnlyList<RouteCandidate> CostOptimized(IReadOnlyList<RouteCandidate> c) => c
        .OrderBy(x => PricingCalculator.BlendedRatePerMillion(x.Pricing))
        .ThenBy(x => x.Key.Priority)
        .ThenBy(x => x.Key.AddedOrder)
        .ThenBy(x => x.Key.Id)
        .ToArray();

    // Prefer the most remaining headroom; unknown remaining is treated as max
    // headroom, then break ties by the soonest useful reset.
    private static IReadOnlyList<RouteCandidate> ResetAware(IReadOnlyList<RouteCandidate> c) => c
        .OrderByDescending(x => x.KeyRuntime.RateLimitTokensRemaining ?? long.MaxValue)
        .ThenBy(x => x.KeyRuntime.RateLimitResetAt ?? DateTimeOffset.MaxValue)
        .ThenBy(x => x.Key.Priority)
        .ThenBy(x => x.Key.AddedOrder)
        .ThenBy(x => x.Key.Id)
        .ToArray();

    private static IReadOnlyList<RouteCandidate> Manual(IReadOnlyList<RouteCandidate> c, Guid? manualKeyId) =>
        manualKeyId is null ? [] : c.Where(x => x.Key.Id == manualKeyId.Value).ToArray();
}
