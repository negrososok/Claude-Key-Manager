using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Routing;

/// <summary>
/// Pure budget-window math. A budget can be scoped to a key, provider, profile
/// or model; this type only decides whether a given policy is exhausted for its
/// active window, so the planner can skip the matching candidates.
/// </summary>
public sealed class BudgetEvaluator
{
    /// <summary>Start of the policy's active window at <paramref name="now"/> (UTC-aligned).</summary>
    public DateTimeOffset WindowStart(BudgetPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var utc = now.ToUniversalTime();
        return policy.Window switch
        {
            BudgetWindow.Daily => new DateTimeOffset(utc.Date, TimeSpan.Zero),
            BudgetWindow.Weekly => new DateTimeOffset(StartOfIsoWeek(utc.Date), TimeSpan.Zero),
            BudgetWindow.Monthly => new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero),
            BudgetWindow.Manual => policy.ManualResetAt?.ToUniversalTime() ?? DateTimeOffset.MinValue,
            _ => DateTimeOffset.MinValue
        };
    }

    /// <summary>
    /// True when the policy's limit is reached for the current window. Usage from
    /// a previous window counts as zero (the window has rolled over).
    /// </summary>
    public bool IsExhausted(BudgetPolicy policy, BudgetUsage? usage, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled || policy.Type == BudgetType.None || policy.Limit <= 0)
        {
            return false;
        }

        var used = UsedForCurrentWindow(policy, usage, now);
        return used >= policy.Limit;
    }

    /// <summary>Remaining allowance for the current window (never negative); null when the policy does not limit.</summary>
    public decimal? Remaining(BudgetPolicy policy, BudgetUsage? usage, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled || policy.Type == BudgetType.None || policy.Limit <= 0)
        {
            return null;
        }

        return Math.Max(0m, policy.Limit - UsedForCurrentWindow(policy, usage, now));
    }

    private decimal UsedForCurrentWindow(BudgetPolicy policy, BudgetUsage? usage, DateTimeOffset now)
    {
        if (usage is null)
        {
            return 0m;
        }

        var windowStart = WindowStart(policy, now);
        if (usage.WindowStart.ToUniversalTime() < windowStart)
        {
            return 0m;
        }

        return policy.Type switch
        {
            BudgetType.Tokens => usage.UsedTokens,
            BudgetType.Cost => usage.UsedCost,
            BudgetType.Requests => usage.UsedRequests,
            _ => 0m
        };
    }

    private static DateTime StartOfIsoWeek(DateTime date)
    {
        // Monday = start of week. DayOfWeek puts Sunday at 0, so map it to 7.
        var dayOfWeek = (int)date.DayOfWeek;
        var isoDay = dayOfWeek == 0 ? 7 : dayOfWeek;
        return date.AddDays(1 - isoDay);
    }
}
