using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Selection;

public sealed record KeySelection(ApiKeyRecord? Key, IReadOnlyList<ApiKeyRecord> NormalizedKeys, DateTimeOffset? NearestReset)
{
    public bool HasKey => Key is not null;
}

public sealed class KeySelector
{
    public KeySelection Select(IReadOnlyCollection<ApiKeyRecord> keys, DateTimeOffset now) =>
        Select(keys, now, SelectionStrategy.LeastRecentlyUsed);

    public KeySelection Select(
        IReadOnlyCollection<ApiKeyRecord> keys,
        DateTimeOffset now,
        SelectionStrategy strategy,
        Guid? manualKeyId = null,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var normalized = keys.Select(key => NormalizeExpiredLimit(key, now)).ToArray();
        var eligible = normalized.Where(key => IsEligible(key, now)).ToArray();
        var selected = strategy switch
        {
            SelectionStrategy.PriorityOrder => OrderByPriority(eligible).FirstOrDefault(),
            SelectionStrategy.PriorityThenLru => OrderByPriorityThenLru(eligible).FirstOrDefault(),
            SelectionStrategy.Random => eligible.Length == 0 ? null : eligible[(random ?? Random.Shared).Next(eligible.Length)],
            SelectionStrategy.ManualKey => eligible.FirstOrDefault(key => key.Id == manualKeyId),
            SelectionStrategy.ProviderFallback => OrderByPriorityThenLru(eligible).FirstOrDefault(),
            _ => OrderByLru(eligible).FirstOrDefault()
        };

        var nearestReset = normalized
            .Where(key => key.Enabled)
            .SelectMany(key => new[] { key.QuotaState.FiveHourResetAt, key.QuotaState.WeeklyResetAt, key.QuotaState.ManualBlockedUntil })
            .Where(reset => reset > now)
            .Min();

        return new KeySelection(selected, normalized, nearestReset);
    }

    public static ApiKeyRecord NormalizeExpiredLimit(ApiKeyRecord key, DateTimeOffset now)
    {
        var quota = key.QuotaState;
        var manualBlocked = quota.ManualBlockedUntil > now;
        if (key.Status == KeyStatus.Limited
            && quota.FiveHourResetAt <= now
            && !manualBlocked)
        {
            return key with
            {
                Status = KeyStatus.Available,
                QuotaState = quota with { FiveHourResetAt = null, FiveHourResetEstimated = false }
            };
        }

        if (key.Status == KeyStatus.WeeklyLimited
            && !quota.WeeklyResetUnknown
            && quota.WeeklyResetAt <= now
            && !manualBlocked)
        {
            return key with { Status = KeyStatus.Available, QuotaState = quota with { WeeklyResetAt = null } };
        }

        return key;
    }

    private static IOrderedEnumerable<ApiKeyRecord> OrderByLru(IEnumerable<ApiKeyRecord> keys) => keys
        .OrderBy(key => key.LastUsedAt.HasValue)
        .ThenBy(key => key.LastUsedAt)
        .ThenBy(key => key.AddedOrder)
        .ThenBy(key => key.Id);

    private static IOrderedEnumerable<ApiKeyRecord> OrderByPriority(IEnumerable<ApiKeyRecord> keys) => keys
        .OrderBy(key => key.Priority)
        .ThenBy(key => key.AddedOrder)
        .ThenBy(key => key.Id);

    private static IOrderedEnumerable<ApiKeyRecord> OrderByPriorityThenLru(IEnumerable<ApiKeyRecord> keys) => keys
        .OrderBy(key => key.Priority)
        .ThenBy(key => key.LastUsedAt.HasValue)
        .ThenBy(key => key.LastUsedAt)
        .ThenBy(key => key.AddedOrder)
        .ThenBy(key => key.Id);

    private static bool IsEligible(ApiKeyRecord key, DateTimeOffset now) =>
        key.Enabled
        && (key.QuotaState.ManualBlockedUntil is null || key.QuotaState.ManualBlockedUntil <= now)
        && key.Status is KeyStatus.Available or KeyStatus.Active or KeyStatus.Unknown;
}
