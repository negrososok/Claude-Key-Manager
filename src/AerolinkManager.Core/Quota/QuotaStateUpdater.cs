using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Quota;

public sealed class QuotaStateUpdater
{
    public ApiKeyRecord Apply(ApiKeyRecord key, QuotaClassification classification, DateTimeOffset now, string? sanitizedError)
    {
        var common = key with
        {
            LastErrorAt = classification.Type == QuotaLimitType.None ? key.LastErrorAt : now,
            LastErrorText = classification.Type == QuotaLimitType.None ? key.LastErrorText : sanitizedError
        };

        return classification.Type switch
        {
            QuotaLimitType.None => common,
            QuotaLimitType.FiveHourLimit => common with
            {
                Status = KeyStatus.FiveHourLimited,
                FiveHourResetAt = now + (classification.ResetAfter ?? TimeSpan.FromHours(5)),
                FiveHourResetEstimated = classification.ResetAfter is null
            },
            QuotaLimitType.WeeklyLimit => common with
            {
                Status = KeyStatus.WeeklyLimited,
                WeeklyBlockedUntil = classification.ResetAt ?? (classification.ResetAfter is null ? null : now + classification.ResetAfter),
                WeeklyBlockedUnknown = classification.ResetAt is null && classification.ResetAfter is null
            },
            _ => common with { Status = KeyStatus.Unknown }
        };
    }
}
