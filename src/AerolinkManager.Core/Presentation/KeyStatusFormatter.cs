using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Presentation;

public static class KeyStatusFormatter
{
    public static string Status(KeyStatus status) => status switch
    {
        KeyStatus.FiveHourLimited => "5h limited",
        KeyStatus.WeeklyLimited => "weekly limited",
        _ => status.ToString().ToLowerInvariant()
    };

    public static string Reset(ApiKeyRecord key, DateTimeOffset now)
    {
        if (key.Status == KeyStatus.FiveHourLimited)
        {
            if (key.FiveHourResetAt is null) return "reset unknown";
            var duration = Duration(key.FiveHourResetAt.Value - now);
            return key.FiveHourResetEstimated ? $"estimated reset in {duration}" : $"resets in {duration}";
        }
        if (key.Status == KeyStatus.WeeklyLimited)
        {
            return key.WeeklyBlockedUnknown || key.WeeklyBlockedUntil is null
                ? "weekly limit, reset unknown"
                : $"weekly limit, resets {key.WeeklyBlockedUntil.Value.LocalDateTime:g}";
        }
        return "—";
    }

    public static string Mask(string key)
    {
        if (key.Length <= 8) return "••••" + key[^Math.Min(4, key.Length)..];
        var separator = key.IndexOf('-', StringComparison.Ordinal);
        var prefixLength = separator >= 0 ? Math.Min(separator + 1, 7) : 4;
        return $"{key[..prefixLength]}...{key[^4..]}";
    }

    private static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{Math.Max(1, value.Minutes)}m";
    }
}
