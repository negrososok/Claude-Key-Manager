using AerolinkManager.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ClaudeManager.Storage;

public sealed class SqliteUsageStore : IUsageStore
{
    public const int CurrentSchemaVersion = 1;
    private readonly string _connectionString;

    public SqliteUsageStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var version = connection.CreateCommand();
        version.Transaction = (SqliteTransaction)transaction;
        version.CommandText = "INSERT INTO schema_info(id, version) VALUES(1, $version) ON CONFLICT(id) DO UPDATE SET version = excluded.version;";
        version.Parameters.AddWithValue("$version", CurrentSchemaVersion);
        await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddRequestAsync(RequestUsageRecord request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO requests (
                request_id, timestamp_utc, session_id, agent_id, parent_agent_id, profile_id,
                provider_id, key_id, model, streaming, status_code, error_type, duration_ms,
                input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens,
                server_tool_use, estimated_cost_micros, currency)
            VALUES ($requestId, $timestamp, $sessionId, $agentId, $parentAgentId, $profileId,
                $providerId, $keyId, $model, $streaming, $statusCode, $errorType, $durationMs,
                $inputTokens, $outputTokens, $cacheCreation, $cacheRead, $serverToolUse, $cost, $currency);
            """;
        command.Parameters.AddWithValue("$requestId", request.RequestId);
        command.Parameters.AddWithValue("$timestamp", request.Timestamp.UtcDateTime.ToString("O"));
        AddNullable(command, "$sessionId", request.SessionId);
        AddNullable(command, "$agentId", request.AgentId);
        AddNullable(command, "$parentAgentId", request.ParentAgentId);
        AddNullable(command, "$profileId", request.ProfileId);
        AddNullable(command, "$providerId", request.ProviderId);
        AddNullable(command, "$keyId", request.KeyId?.ToString());
        AddNullable(command, "$model", request.Model);
        command.Parameters.AddWithValue("$streaming", request.Streaming ? 1 : 0);
        command.Parameters.AddWithValue("$statusCode", request.StatusCode);
        AddNullable(command, "$errorType", request.ErrorType);
        command.Parameters.AddWithValue("$durationMs", request.DurationMs);
        command.Parameters.AddWithValue("$inputTokens", request.InputTokens);
        command.Parameters.AddWithValue("$outputTokens", request.OutputTokens);
        command.Parameters.AddWithValue("$cacheCreation", request.CacheCreationInputTokens);
        command.Parameters.AddWithValue("$cacheRead", request.CacheReadInputTokens);
        command.Parameters.AddWithValue("$serverToolUse", request.ServerToolUse);
        command.Parameters.AddWithValue("$cost", request.EstimatedCostMicros is null ? DBNull.Value : request.EstimatedCostMicros.Value);
        AddNullable(command, "$currency", request.Currency);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertSessionAsync(SessionUpsert session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (session_id, started_at_utc, last_activity_utc, profile_id,
                provider_id, key_id, model, request_count, token_count, cost_micros, key_switch_count, status)
            VALUES ($sessionId, $started, $lastActivity, $profileId, $providerId, $keyId, $model,
                1, $tokenDelta, $costDelta, $keySwitchDelta, $status)
            ON CONFLICT(session_id) DO UPDATE SET
                last_activity_utc = excluded.last_activity_utc,
                profile_id = COALESCE(excluded.profile_id, sessions.profile_id),
                provider_id = COALESCE(excluded.provider_id, sessions.provider_id),
                key_id = COALESCE(excluded.key_id, sessions.key_id),
                model = COALESCE(excluded.model, sessions.model),
                request_count = sessions.request_count + 1,
                token_count = sessions.token_count + excluded.token_count,
                cost_micros = sessions.cost_micros + COALESCE(excluded.cost_micros, 0),
                key_switch_count = sessions.key_switch_count + excluded.key_switch_count,
                status = excluded.status;
            """;
        command.Parameters.AddWithValue("$sessionId", session.SessionId);
        command.Parameters.AddWithValue("$started", session.Timestamp.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$lastActivity", session.Timestamp.UtcDateTime.ToString("O"));
        AddNullable(command, "$profileId", session.ProfileId);
        AddNullable(command, "$providerId", session.ProviderId);
        AddNullable(command, "$keyId", session.KeyId?.ToString());
        AddNullable(command, "$model", session.Model);
        command.Parameters.AddWithValue("$tokenDelta", session.TokensDelta);
        command.Parameters.AddWithValue("$costDelta", session.CostMicrosDelta is null ? DBNull.Value : session.CostMicrosDelta.Value);
        command.Parameters.AddWithValue("$keySwitchDelta", session.KeySwitched ? 1 : 0);
        command.Parameters.AddWithValue("$status", session.Status);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddUsageEventAsync(UsageEventRecord usageEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO usage_events (request_id, event_type, timestamp_utc, input_tokens,
                output_tokens, cache_creation_input_tokens, cache_read_input_tokens, server_tool_use)
            VALUES ($requestId, $eventType, $timestamp, $inputTokens, $outputTokens,
                $cacheCreation, $cacheRead, $serverToolUse);
            """;
        command.Parameters.AddWithValue("$requestId", usageEvent.RequestId);
        command.Parameters.AddWithValue("$eventType", usageEvent.EventType);
        command.Parameters.AddWithValue("$timestamp", usageEvent.Timestamp.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$inputTokens", usageEvent.InputTokens);
        command.Parameters.AddWithValue("$outputTokens", usageEvent.OutputTokens);
        command.Parameters.AddWithValue("$cacheCreation", usageEvent.CacheCreationInputTokens);
        command.Parameters.AddWithValue("$cacheRead", usageEvent.CacheReadInputTokens);
        command.Parameters.AddWithValue("$serverToolUse", usageEvent.ServerToolUse);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddLimitEventAsync(LimitEventRecord limitEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limitEvent);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO limit_events (request_id, provider_id, key_id, model, limit_type,
                reset_at_utc, estimated, timestamp_utc)
            VALUES ($requestId, $providerId, $keyId, $model, $limitType,
                $resetAt, $estimated, $timestamp);
            """;
        AddNullable(command, "$requestId", limitEvent.RequestId);
        AddNullable(command, "$providerId", limitEvent.ProviderId);
        AddNullable(command, "$keyId", limitEvent.KeyId?.ToString());
        AddNullable(command, "$model", limitEvent.Model);
        command.Parameters.AddWithValue("$limitType", limitEvent.LimitType);
        AddNullable(command, "$resetAt", limitEvent.ResetAtUtc?.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$estimated", limitEvent.Estimated ? 1 : 0);
        command.Parameters.AddWithValue("$timestamp", limitEvent.Timestamp.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddRouteDecisionAsync(RouteDecisionRecord routeDecision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeDecision);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO route_decisions (
                request_id, session_id, profile_id, chain_id, selected_provider_id,
                selected_key_id, selected_model, decision_reason, trace_json, timestamp_utc)
            VALUES ($requestId, $sessionId, $profileId, $chainId, $providerId,
                $keyId, $model, $reason, $traceJson, $timestamp)
            ON CONFLICT(request_id) DO UPDATE SET
                session_id = excluded.session_id,
                profile_id = excluded.profile_id,
                chain_id = excluded.chain_id,
                selected_provider_id = excluded.selected_provider_id,
                selected_key_id = excluded.selected_key_id,
                selected_model = excluded.selected_model,
                decision_reason = excluded.decision_reason,
                trace_json = excluded.trace_json,
                timestamp_utc = excluded.timestamp_utc;
            """;
        command.Parameters.AddWithValue("$requestId", routeDecision.RequestId);
        AddNullable(command, "$sessionId", routeDecision.SessionId);
        AddNullable(command, "$profileId", routeDecision.ProfileId);
        AddNullable(command, "$chainId", routeDecision.ChainId);
        AddNullable(command, "$providerId", routeDecision.SelectedProviderId);
        AddNullable(command, "$keyId", routeDecision.SelectedKeyId?.ToString());
        AddNullable(command, "$model", routeDecision.SelectedModel);
        command.Parameters.AddWithValue("$reason", routeDecision.DecisionReason);
        command.Parameters.AddWithValue("$traceJson", routeDecision.TraceJson);
        command.Parameters.AddWithValue("$timestamp", routeDecision.Timestamp.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddKeySwitchAsync(KeySwitchRecord keySwitch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keySwitch);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO key_switches (session_id, from_key_id, to_key_id, reason, timestamp_utc)
            VALUES ($sessionId, $fromKeyId, $toKeyId, $reason, $timestamp);
            """;
        AddNullable(command, "$sessionId", keySwitch.SessionId);
        AddNullable(command, "$fromKeyId", keySwitch.FromKeyId?.ToString());
        command.Parameters.AddWithValue("$toKeyId", keySwitch.ToKeyId.ToString());
        command.Parameters.AddWithValue("$reason", keySwitch.Reason);
        command.Parameters.AddWithValue("$timestamp", keySwitch.Timestamp.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RequestUsageRecord>> QueryRequestsAsync(RequestQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clauses = new List<string>();
        if (!string.IsNullOrEmpty(query.SessionId)) { clauses.Add("session_id = $sessionId"); }
        if (!string.IsNullOrEmpty(query.ProviderId)) { clauses.Add("provider_id = $providerId"); }
        if (!string.IsNullOrEmpty(query.Model)) { clauses.Add("model = $model"); }
        if (query.From is { } from) { clauses.Add("timestamp_utc >= $from"); }
        if (query.To is { } to) { clauses.Add("timestamp_utc <= $to"); }
        if (query.StatusCode is { } statusCode) { clauses.Add("status_code = $statusCode"); }
        var where = clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : string.Empty;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT request_id, timestamp_utc, session_id, agent_id, parent_agent_id, profile_id, provider_id, key_id, model, streaming, status_code, error_type, duration_ms, input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, server_tool_use, estimated_cost_micros, currency FROM requests{where} ORDER BY timestamp_utc;";
        if (!string.IsNullOrEmpty(query.SessionId)) command.Parameters.AddWithValue("$sessionId", query.SessionId);
        if (!string.IsNullOrEmpty(query.ProviderId)) command.Parameters.AddWithValue("$providerId", query.ProviderId);
        if (!string.IsNullOrEmpty(query.Model)) command.Parameters.AddWithValue("$model", query.Model);
        if (query.From is { } f) command.Parameters.AddWithValue("$from", f.UtcDateTime.ToString("O"));
        if (query.To is { } t) command.Parameters.AddWithValue("$to", t.UtcDateTime.ToString("O"));
        if (query.StatusCode is { } sc) command.Parameters.AddWithValue("$statusCode", sc);

        var results = new List<RequestUsageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RequestUsageRecord
            {
                RequestId = reader.GetString(0),
                Timestamp = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                SessionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                AgentId = reader.IsDBNull(3) ? null : reader.GetString(3),
                ParentAgentId = reader.IsDBNull(4) ? null : reader.GetString(4),
                ProfileId = reader.IsDBNull(5) ? null : reader.GetString(5),
                ProviderId = reader.IsDBNull(6) ? null : reader.GetString(6),
                KeyId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                Model = reader.IsDBNull(8) ? null : reader.GetString(8),
                Streaming = reader.GetInt32(9) == 1,
                StatusCode = reader.GetInt32(10),
                ErrorType = reader.IsDBNull(11) ? null : reader.GetString(11),
                DurationMs = reader.GetInt64(12),
                InputTokens = reader.GetInt64(13),
                OutputTokens = reader.GetInt64(14),
                CacheCreationInputTokens = reader.GetInt64(15),
                CacheReadInputTokens = reader.GetInt64(16),
                ServerToolUse = reader.GetInt64(17),
                EstimatedCostMicros = reader.IsDBNull(18) ? null : reader.GetInt64(18),
                Currency = reader.IsDBNull(19) ? null : reader.GetString(19)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<RouteDecisionRecord>> QueryRouteDecisionsAsync(RouteDecisionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clauses = new List<string>();
        if (!string.IsNullOrEmpty(query.SessionId)) clauses.Add("session_id = $sessionId");
        if (!string.IsNullOrEmpty(query.ProfileId)) clauses.Add("profile_id = $profileId");
        if (!string.IsNullOrEmpty(query.ProviderId)) clauses.Add("selected_provider_id = $providerId");
        if (query.From is not null) clauses.Add("timestamp_utc >= $from");
        if (query.To is not null) clauses.Add("timestamp_utc <= $to");
        var where = clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : string.Empty;
        var limit = Math.Clamp(query.Limit, 1, 500);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT request_id, session_id, profile_id, chain_id, selected_provider_id,
                selected_key_id, selected_model, decision_reason, trace_json, timestamp_utc
            FROM route_decisions{where}
            ORDER BY timestamp_utc DESC
            LIMIT $limit;
            """;
        if (!string.IsNullOrEmpty(query.SessionId)) command.Parameters.AddWithValue("$sessionId", query.SessionId);
        if (!string.IsNullOrEmpty(query.ProfileId)) command.Parameters.AddWithValue("$profileId", query.ProfileId);
        if (!string.IsNullOrEmpty(query.ProviderId)) command.Parameters.AddWithValue("$providerId", query.ProviderId);
        if (query.From is { } f) command.Parameters.AddWithValue("$from", f.UtcDateTime.ToString("O"));
        if (query.To is { } t) command.Parameters.AddWithValue("$to", t.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<RouteDecisionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var selectedProviderId = reader.IsDBNull(4) ? null : reader.GetString(4);
            Guid? selectedKeyId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5));
            var selectedModel = reader.IsDBNull(6) ? null : reader.GetString(6);
            var decisionReason = reader.GetString(7);
            var timestamp = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(new RouteDecisionRecord
            {
                RequestId = reader.GetString(0),
                SessionId = reader.IsDBNull(1) ? null : reader.GetString(1),
                ProfileId = reader.IsDBNull(2) ? null : reader.GetString(2),
                ChainId = reader.IsDBNull(3) ? null : reader.GetString(3),
                SelectedProviderId = selectedProviderId,
                SelectedKeyId = selectedKeyId,
                SelectedModel = selectedModel,
                DecisionReason = decisionReason,
                TraceJson = reader.GetString(8),
                Timestamp = timestamp,
                Story = BuildRouteStory(selectedProviderId, selectedKeyId, selectedModel, decisionReason, timestamp)
            });
        }

        return rows;
    }

    public async Task<long> CountRequestsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM requests;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tables.Add(reader.GetString(0));
        return tables;
    }

    // ── Phase C: aggregation, session summaries, provider health ──

    public async Task<IReadOnlyList<UsageGroupRow>> QueryGroupedUsageAsync(UsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var bucket = query.PeriodBucket ?? "day";
        var groupExpr = query.GroupBy switch
        {
            UsageGrouping.Period => bucket switch
            {
                "hour" => "substr(timestamp_utc, 1, 13)",
                "month" => "substr(timestamp_utc, 1, 7)",
                _ => "substr(timestamp_utc, 1, 10)"
            },
            UsageGrouping.Provider => "COALESCE(provider_id, '-')",
            UsageGrouping.Key => "COALESCE(key_id, '-')",
            UsageGrouping.Model => "COALESCE(model, '-')",
            UsageGrouping.Profile => "COALESCE(profile_id, '-')",
            UsageGrouping.Session => "COALESCE(session_id, '-')",
            _ => "substr(timestamp_utc, 1, 10)"
        };

        var filter = BuildUsageFilter(query);
        var where = filter.Count > 0 ? " WHERE " + string.Join(" AND ", filter) : string.Empty;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {groupExpr} AS gk, COUNT(*) AS rc,
                SUM(input_tokens), SUM(output_tokens), SUM(cache_creation_input_tokens),
                SUM(cache_read_input_tokens), SUM(server_tool_use), SUM(estimated_cost_micros),
                MAX(currency), AVG(CASE WHEN status_code < 400 THEN 1.0 ELSE 0.0 END),
                MIN(timestamp_utc), MAX(timestamp_utc)
            FROM requests{where} GROUP BY gk ORDER BY gk;
            """;
        BindUsageFilter(command, query);

        var rows = new List<UsageGroupRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new UsageGroupRow
                {
                    GroupKey = reader.GetString(0),
                    RequestCount = reader.GetInt64(1),
                    TotalInputTokens = reader.GetInt64(2),
                    TotalOutputTokens = reader.GetInt64(3),
                    TotalCacheCreationInputTokens = reader.GetInt64(4),
                    TotalCacheReadInputTokens = reader.GetInt64(5),
                    TotalServerToolUse = reader.GetInt64(6),
                    TotalCostMicros = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    Currency = reader.IsDBNull(8) ? null : reader.GetString(8),
                    SuccessRate = reader.GetDouble(9),
                    FirstTimestamp = DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture),
                    LastTimestamp = DateTimeOffset.Parse(reader.GetString(11), System.Globalization.CultureInfo.InvariantCulture)
                });
            }
        }

        // Percentiles computed per group key in a second pass (SQLite lacks percentile_cont).
        var enriched = new List<UsageGroupRow>(rows.Count);
        foreach (var row in rows)
        {
            var p50 = await QueryGroupPercentileAsync(connection, query, groupExpr, row.GroupKey, 0.50, cancellationToken).ConfigureAwait(false);
            var p95 = await QueryGroupPercentileAsync(connection, query, groupExpr, row.GroupKey, 0.95, cancellationToken).ConfigureAwait(false);
            enriched.Add(row with { P50DurationMs = p50, P95DurationMs = p95 });
        }

        return enriched;
    }

    private static async Task<long> QueryGroupPercentileAsync(SqliteConnection connection, UsageQuery query, string groupExpr, string groupKey, double fraction, CancellationToken ct)
    {
        var filter = BuildUsageFilter(query);
        filter.Add($"{groupExpr} = @gk");
        var where = " WHERE " + string.Join(" AND ", filter);
        var frac = fraction.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT duration_ms FROM requests{where} ORDER BY duration_ms LIMIT 1 OFFSET MAX(0, CAST((SELECT COUNT(*) FROM requests{where}) * {frac} AS INTEGER) - 1);";
        BindUsageFilter(cmd, query);
        cmd.Parameters.AddWithValue("@gk", groupKey);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long v ? v : 0L;
    }

    private static List<string> BuildUsageFilter(UsageQuery query)
    {
        var filter = new List<string>();
        if (query.From is not null) filter.Add("timestamp_utc >= @from");
        if (query.To is not null) filter.Add("timestamp_utc <= @to");
        if (!string.IsNullOrEmpty(query.ProviderId)) filter.Add("provider_id = @pid");
        if (!string.IsNullOrEmpty(query.ProfileId)) filter.Add("profile_id = @fid");
        if (!string.IsNullOrEmpty(query.Model)) filter.Add("model = @mdl");
        if (!string.IsNullOrEmpty(query.SessionId)) filter.Add("session_id = @sid");
        if (query.KeyId is not null) filter.Add("key_id = @kid");
        return filter;
    }

    private static void BindUsageFilter(SqliteCommand command, UsageQuery query)
    {
        if (query.From is { } f) command.Parameters.AddWithValue("@from", f.UtcDateTime.ToString("O"));
        if (query.To is { } t) command.Parameters.AddWithValue("@to", t.UtcDateTime.ToString("O"));
        if (!string.IsNullOrEmpty(query.ProviderId)) command.Parameters.AddWithValue("@pid", query.ProviderId);
        if (!string.IsNullOrEmpty(query.ProfileId)) command.Parameters.AddWithValue("@fid", query.ProfileId);
        if (!string.IsNullOrEmpty(query.Model)) command.Parameters.AddWithValue("@mdl", query.Model);
        if (!string.IsNullOrEmpty(query.SessionId)) command.Parameters.AddWithValue("@sid", query.SessionId);
        if (query.KeyId is { } kid) command.Parameters.AddWithValue("@kid", kid.ToString());
    }

    public async Task<IReadOnlyList<SessionSummaryRow>> QuerySessionSummariesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, COUNT(*), SUM(input_tokens), SUM(output_tokens),
                SUM(cache_creation_input_tokens), SUM(cache_read_input_tokens),
                SUM(estimated_cost_micros), MAX(currency),
                SUM(CASE WHEN status_code < 400 AND error_type IS NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN error_type IS NOT NULL AND error_type NOT IN ('client_cancelled','upstream_stream_failure') THEN 1 ELSE 0 END),
                SUM(CASE WHEN error_type IN ('client_cancelled','upstream_stream_failure') THEN 1 ELSE 0 END),
                MIN(timestamp_utc), MAX(timestamp_utc)
            FROM requests WHERE session_id IS NOT NULL
            GROUP BY session_id ORDER BY MAX(timestamp_utc) DESC;
            """;

        var sessions = new List<(string Id, SessionSummaryRow Row)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sessionId = reader.GetString(0);
                sessions.Add((sessionId, new SessionSummaryRow
                {
                    SessionId = sessionId,
                    RequestCount = reader.GetInt64(1),
                    TotalInputTokens = reader.GetInt64(2),
                    TotalOutputTokens = reader.GetInt64(3),
                    TotalCacheCreationInputTokens = reader.GetInt64(4),
                    TotalCacheReadInputTokens = reader.GetInt64(5),
                    TotalCostMicros = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    Currency = reader.IsDBNull(7) ? null : reader.GetString(7),
                    SuccessCount = reader.GetInt64(8),
                    ErrorCount = reader.GetInt64(9),
                    PartialCount = reader.GetInt64(10),
                    FirstTimestamp = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), System.Globalization.CultureInfo.InvariantCulture),
                    LastTimestamp = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), System.Globalization.CultureInfo.InvariantCulture)
                }));
            }
        }

        var rows = new List<SessionSummaryRow>(sessions.Count);
        foreach (var (id, row) in sessions)
        {
            rows.Add(row with { ProviderBreakdowns = await QuerySessionBreakdownsAsync(connection, id, cancellationToken).ConfigureAwait(false) });
        }

        return rows;
    }

    private static async Task<IReadOnlyList<SessionProviderBreakdown>> QuerySessionBreakdownsAsync(SqliteConnection connection, string sessionId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT provider_id, model, COUNT(*), SUM(input_tokens), SUM(output_tokens), SUM(estimated_cost_micros) FROM requests WHERE session_id = @sid GROUP BY provider_id, model ORDER BY provider_id, model;";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var list = new List<SessionProviderBreakdown>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new SessionProviderBreakdown
            {
                ProviderId = r.IsDBNull(0) ? "-" : r.GetString(0),
                Model = r.IsDBNull(1) ? null : r.GetString(1),
                RequestCount = r.GetInt64(2),
                TotalInputTokens = r.GetInt64(3),
                TotalOutputTokens = r.GetInt64(4),
                TotalCostMicros = r.IsDBNull(5) ? null : r.GetInt64(5)
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<ProviderHealthRow>> GetProviderHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT provider_id, circuit_state, failure_count, last_failure_at_utc, last_success_at_utc, p50_latency_ms, p95_latency_ms, success_rate FROM provider_health ORDER BY provider_id;";
        var rows = new List<ProviderHealthRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ProviderHealthRow
            {
                ProviderId = reader.GetString(0),
                CircuitState = reader.GetString(1),
                ErrorCount = reader.GetInt64(2),
                LastFailureAt = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                LastSuccessAt = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                P50LatencyMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                P95LatencyMs = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                SuccessRate = reader.IsDBNull(7) ? null : reader.GetDouble(7)
            });
        }

        return rows;
    }

    public async Task RecomputeProviderHealthAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var since = now.AddDays(-1).UtcDateTime.ToString("O");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO provider_health (provider_id, circuit_state, opened_until_utc, failure_count,
                last_failure_at_utc, last_success_at_utc, p50_latency_ms, p95_latency_ms, success_rate)
            SELECT COALESCE(provider_id, 'unknown'), 'closed', NULL,
                SUM(CASE WHEN status_code >= 400 THEN 1 ELSE 0 END),
                MAX(CASE WHEN status_code >= 400 THEN timestamp_utc END),
                MAX(CASE WHEN status_code < 400 THEN timestamp_utc END),
                NULL, NULL,
                AVG(CASE WHEN status_code < 400 THEN 1.0 ELSE 0.0 END)
            FROM requests WHERE timestamp_utc >= @since AND provider_id IS NOT NULL
            GROUP BY provider_id
            ON CONFLICT(provider_id) DO UPDATE SET
                failure_count = excluded.failure_count,
                success_rate = excluded.success_rate,
                last_success_at_utc = COALESCE(excluded.last_success_at_utc, provider_health.last_success_at_utc),
                last_failure_at_utc = COALESCE(excluded.last_failure_at_utc, provider_health.last_failure_at_utc);
            """;
        cmd.Parameters.AddWithValue("@since", since);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Fill p50/p95 latency per provider (separate pass; SQLite has no percentile_cont).
        await using var providerCmd = connection.CreateCommand();
        providerCmd.CommandText = "SELECT DISTINCT provider_id FROM requests WHERE timestamp_utc >= @since AND provider_id IS NOT NULL;";
        providerCmd.Parameters.AddWithValue("@since", since);
        var providers = new List<string>();
        await using (var pr = await providerCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await pr.ReadAsync(cancellationToken).ConfigureAwait(false)) providers.Add(pr.GetString(0));
        }

        foreach (var provider in providers)
        {
            var p50 = await ProviderLatencyAsync(connection, provider, since, 0.50, cancellationToken).ConfigureAwait(false);
            var p95 = await ProviderLatencyAsync(connection, provider, since, 0.95, cancellationToken).ConfigureAwait(false);
            await using var upd = connection.CreateCommand();
            upd.CommandText = "UPDATE provider_health SET p50_latency_ms = @p50, p95_latency_ms = @p95 WHERE provider_id = @pid;";
            upd.Parameters.AddWithValue("@p50", p50);
            upd.Parameters.AddWithValue("@p95", p95);
            upd.Parameters.AddWithValue("@pid", provider);
            await upd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> ProviderLatencyAsync(SqliteConnection connection, string provider, string since, double fraction, CancellationToken ct)
    {
        var frac = fraction.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT duration_ms FROM requests WHERE provider_id = @pid AND timestamp_utc >= @since ORDER BY duration_ms LIMIT 1 OFFSET MAX(0, CAST((SELECT COUNT(*) FROM requests WHERE provider_id = @pid AND timestamp_utc >= @since) * {frac} AS INTEGER) - 1);";
        cmd.Parameters.AddWithValue("@pid", provider);
        cmd.Parameters.AddWithValue("@since", since);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long v ? v : 0L;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);

    private static string BuildRouteStory(string? providerId, Guid? keyId, string? model, string reason, DateTimeOffset timestamp)
    {
        var time = timestamp.LocalDateTime.ToString("g", System.Globalization.CultureInfo.InvariantCulture);
        if (providerId is null)
        {
            return $"{time}: no route selected ({reason}).";
        }

        var key = keyId is null ? "automatic key" : $"key {keyId.Value.ToString()[..8]}";
        var modelPart = string.IsNullOrWhiteSpace(model) ? "default model" : model;
        return $"{time}: routed to {providerId} / {modelPart} using {key} ({reason}).";
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_info (id INTEGER PRIMARY KEY CHECK(id = 1), version INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS requests (
            request_id TEXT PRIMARY KEY, timestamp_utc TEXT NOT NULL, session_id TEXT, agent_id TEXT,
            parent_agent_id TEXT, profile_id TEXT, provider_id TEXT, key_id TEXT, model TEXT,
            streaming INTEGER NOT NULL CHECK(streaming IN (0,1)), status_code INTEGER NOT NULL,
            error_type TEXT, duration_ms INTEGER NOT NULL CHECK(duration_ms >= 0),
            input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
            cache_creation_input_tokens INTEGER NOT NULL DEFAULT 0, cache_read_input_tokens INTEGER NOT NULL DEFAULT 0,
            server_tool_use INTEGER NOT NULL DEFAULT 0, estimated_cost_micros INTEGER, currency TEXT);
        CREATE INDEX IF NOT EXISTS idx_requests_timestamp ON requests(timestamp_utc);
        CREATE INDEX IF NOT EXISTS idx_requests_session ON requests(session_id, timestamp_utc);
        CREATE INDEX IF NOT EXISTS idx_requests_provider_model ON requests(provider_id, model, timestamp_utc);
        CREATE TABLE IF NOT EXISTS sessions (
            session_id TEXT PRIMARY KEY, started_at_utc TEXT NOT NULL, last_activity_utc TEXT NOT NULL,
            profile_id TEXT, provider_id TEXT, key_id TEXT, model TEXT, request_count INTEGER NOT NULL DEFAULT 0,
            token_count INTEGER NOT NULL DEFAULT 0, cost_micros INTEGER, key_switch_count INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS usage_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT, request_id TEXT NOT NULL, event_type TEXT NOT NULL,
            timestamp_utc TEXT NOT NULL, input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
            cache_creation_input_tokens INTEGER NOT NULL DEFAULT 0, cache_read_input_tokens INTEGER NOT NULL DEFAULT 0,
            server_tool_use INTEGER NOT NULL DEFAULT 0, FOREIGN KEY(request_id) REFERENCES requests(request_id) ON DELETE CASCADE);
        CREATE INDEX IF NOT EXISTS idx_usage_events_request ON usage_events(request_id);
        CREATE TABLE IF NOT EXISTS key_switches (
            id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT, from_key_id TEXT, to_key_id TEXT NOT NULL,
            reason TEXT NOT NULL, timestamp_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS limit_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT, request_id TEXT, provider_id TEXT, key_id TEXT, model TEXT,
            limit_type TEXT NOT NULL, reset_at_utc TEXT, estimated INTEGER NOT NULL DEFAULT 0, timestamp_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS budgets (
            policy_id TEXT NOT NULL, window_start_utc TEXT NOT NULL, window_end_utc TEXT,
            used_tokens INTEGER NOT NULL DEFAULT 0, used_cost_micros INTEGER NOT NULL DEFAULT 0,
            used_requests INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(policy_id, window_start_utc));
        CREATE TABLE IF NOT EXISTS route_decisions (
            request_id TEXT PRIMARY KEY, session_id TEXT, profile_id TEXT, chain_id TEXT,
            selected_provider_id TEXT, selected_key_id TEXT, selected_model TEXT, decision_reason TEXT NOT NULL,
            trace_json TEXT NOT NULL, timestamp_utc TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS idx_route_decisions_timestamp ON route_decisions(timestamp_utc);
        CREATE INDEX IF NOT EXISTS idx_route_decisions_session ON route_decisions(session_id, timestamp_utc);
        CREATE INDEX IF NOT EXISTS idx_route_decisions_provider ON route_decisions(selected_provider_id, timestamp_utc);
        CREATE TABLE IF NOT EXISTS provider_health (
            provider_id TEXT PRIMARY KEY, circuit_state TEXT NOT NULL, opened_until_utc TEXT,
            failure_count INTEGER NOT NULL DEFAULT 0, last_failure_at_utc TEXT, last_success_at_utc TEXT,
            p50_latency_ms INTEGER, p95_latency_ms INTEGER, success_rate REAL);
        CREATE TABLE IF NOT EXISTS model_lockouts (
            provider_id TEXT NOT NULL, model_value TEXT NOT NULL, locked_until_utc TEXT,
            reason TEXT NOT NULL, estimated INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(provider_id, model_value));
        """;
}
