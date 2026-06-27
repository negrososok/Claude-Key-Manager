using System.Globalization;
using System.Text.RegularExpressions;
using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Quota;

public enum QuotaLimitType
{
    None,
    FiveHourLimit,
    WeeklyLimit,
    UnknownLimit
}

public sealed record QuotaClassification(QuotaLimitType Type, TimeSpan? ResetAfter = null, DateTimeOffset? ResetAt = null);

public sealed partial class QuotaErrorClassifier
{
    private static readonly string[] LimitSignals =
    [
        "quota exceeded", "rate limit", "usage limit", "try again later",
        "too many requests", "429", "limit resets", "resets in", "reset in"
    ];

    private static readonly string[] WeeklySignals =
    [
        "weekly", "billing", "insufficient credits", "credits exhausted", "credit limit", "monthly"
    ];

    public QuotaClassification Classify(string? text, ErrorPatternSetRecord? patterns = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new QuotaClassification(QuotaLimitType.None);
        }

        var normalized = text.ToLowerInvariant();
        var isWeekly = WeeklySignals.Concat(patterns?.WeeklySignals ?? []).Any(normalized.Contains);
        var isLimit = isWeekly || LimitSignals.Concat(patterns?.LimitSignals ?? []).Any(normalized.Contains);
        if (!isLimit)
        {
            return new QuotaClassification(QuotaLimitType.None);
        }

        var resetAfter = ParseResetDuration(normalized);
        return new QuotaClassification(
            isWeekly ? QuotaLimitType.WeeklyLimit : QuotaLimitType.FiveHourLimit,
            resetAfter);
    }

    public TimeSpan? ParseResetDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = DurationPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var hours = ParsePart(match.Groups["hours"].Value);
        var minutes = ParsePart(match.Groups["minutes"].Value);
        return hours == 0 && minutes == 0 ? null : TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
    }

    private static int ParsePart(string value) =>
        string.IsNullOrEmpty(value) ? 0 : int.Parse(value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?:(?<hours>\d+)\s*(?:h(?:ours?)?)(?:\s*(?<minutes>\d+)\s*(?:m(?:in(?:utes?)?)?))?|(?<minutes>\d+)\s*(?:m(?:in(?:utes?)?)?))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}
