using System.Globalization;
using System.Text;
using AerolinkManager.Core.Storage;

namespace AerolinkManager.Core.Usage;

/// <summary>
/// RFC-4180 CSV export for usage data. Exports metadata, token counts, cost and
/// status ONLY — never prompts, response bodies, API keys, local tokens or raw
/// headers. Pure (no I/O), so it is unit-testable without SQLite.
/// </summary>
public static class UsageCsvExporter
{
    public static string ExportRequests(IReadOnlyList<RequestUsageRecord> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', new[]
        {
            "request_id", "timestamp_utc", "session_id", "agent_id", "parent_agent_id",
            "profile_id", "provider_id", "key_id", "model", "streaming", "status_code",
            "error_type", "duration_ms", "input_tokens", "output_tokens",
            "cache_creation_input_tokens", "cache_read_input_tokens", "server_tool_use",
            "estimated_cost_micros", "currency"
        }));

        foreach (var r in requests)
        {
            sb.AppendLine(string.Join(',', new[]
            {
                Escape(r.RequestId),
                Escape(r.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                Escape(r.SessionId),
                Escape(r.AgentId),
                Escape(r.ParentAgentId),
                Escape(r.ProfileId),
                Escape(r.ProviderId),
                Escape(r.KeyId?.ToString()),
                Escape(r.Model),
                r.Streaming ? "1" : "0",
                r.StatusCode.ToString(CultureInfo.InvariantCulture),
                Escape(r.ErrorType),
                r.DurationMs.ToString(CultureInfo.InvariantCulture),
                r.InputTokens.ToString(CultureInfo.InvariantCulture),
                r.OutputTokens.ToString(CultureInfo.InvariantCulture),
                r.CacheCreationInputTokens.ToString(CultureInfo.InvariantCulture),
                r.CacheReadInputTokens.ToString(CultureInfo.InvariantCulture),
                r.ServerToolUse.ToString(CultureInfo.InvariantCulture),
                r.EstimatedCostMicros?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(r.Currency)
            }));
        }

        return sb.ToString();
    }

    public static string ExportSessions(IReadOnlyList<SessionSummaryRow> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', new[]
        {
            "session_id", "request_count", "total_input_tokens", "total_output_tokens",
            "total_cache_creation_input_tokens", "total_cache_read_input_tokens",
            "total_cost_micros", "currency", "success_count", "error_count", "partial_count",
            "first_timestamp_utc", "last_timestamp_utc"
        }));

        foreach (var s in sessions)
        {
            sb.AppendLine(string.Join(',', new[]
            {
                Escape(s.SessionId),
                s.RequestCount.ToString(CultureInfo.InvariantCulture),
                s.TotalInputTokens.ToString(CultureInfo.InvariantCulture),
                s.TotalOutputTokens.ToString(CultureInfo.InvariantCulture),
                s.TotalCacheCreationInputTokens.ToString(CultureInfo.InvariantCulture),
                s.TotalCacheReadInputTokens.ToString(CultureInfo.InvariantCulture),
                s.TotalCostMicros?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(s.Currency),
                s.SuccessCount.ToString(CultureInfo.InvariantCulture),
                s.ErrorCount.ToString(CultureInfo.InvariantCulture),
                s.PartialCount.ToString(CultureInfo.InvariantCulture),
                Escape(s.FirstTimestamp?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                Escape(s.LastTimestamp?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            }));
        }

        return sb.ToString();
    }

    public static string ExportGroupedUsage(IReadOnlyList<UsageGroupRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', new[]
        {
            "group_key", "request_count", "total_input_tokens", "total_output_tokens",
            "total_cache_creation_input_tokens", "total_cache_read_input_tokens",
            "total_server_tool_use", "total_cost_micros", "currency", "success_rate",
            "p50_duration_ms", "p95_duration_ms", "first_timestamp_utc", "last_timestamp_utc"
        }));

        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',', new[]
            {
                Escape(r.GroupKey),
                r.RequestCount.ToString(CultureInfo.InvariantCulture),
                r.TotalInputTokens.ToString(CultureInfo.InvariantCulture),
                r.TotalOutputTokens.ToString(CultureInfo.InvariantCulture),
                r.TotalCacheCreationInputTokens.ToString(CultureInfo.InvariantCulture),
                r.TotalCacheReadInputTokens.ToString(CultureInfo.InvariantCulture),
                r.TotalServerToolUse.ToString(CultureInfo.InvariantCulture),
                r.TotalCostMicros?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(r.Currency),
                r.SuccessRate.ToString("F4", CultureInfo.InvariantCulture),
                r.P50DurationMs.ToString(CultureInfo.InvariantCulture),
                r.P95DurationMs.ToString(CultureInfo.InvariantCulture),
                Escape(r.FirstTimestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                Escape(r.LastTimestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            }));
        }

        return sb.ToString();
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
