using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Routing;
using AerolinkManager.Core.Security;
using AerolinkManager.Core.Usage;
using AerolinkManager.Core.Storage;
using ClaudeManager.Gateway.Logging;

namespace ClaudeManager.Gateway.Endpoints;

public static class GatewayEndpoints
{
    // Claude Code session attribution headers (used for affinity + future usage grouping).
    private const string SessionHeader = "X-Claude-Code-Session-Id";
    private static readonly JsonSerializerOptions RouteTraceJsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapClaudeEndpoints(this IEndpointRouteBuilder app)
    {
        // /v1/models is filtered to the providers reachable from the active profile's
        // routing chain — models from providers outside the active profile are not exposed.
        app.MapGet("/v1/models", (JsonFileStore store) =>
        {
            var config = store.LoadConfig();
            var state = store.LoadState();
            var providerIds = ActiveProfileProviderIds(config, state);

            var response = new
            {
                type = "list",
                data = config.Models
                    .Where(m => m.Enabled && m.ModelValue is not null && providerIds.Contains(m.ProviderId))
                    .Select(m => new
                    {
                        type = "model",
                        id = m.ModelValue,
                        display_name = m.DisplayName,
                        created_at = "2024-01-01T00:00:00Z"
                    })
            };

            return Results.Ok(response);
        });

        app.MapPost("/v1/messages/count_tokens", (HttpContext context, JsonFileStore store, RoutePlanner planner, IHttpClientFactory clientFactory, SecretRegistry secretRegistry, ISecretProtector protector, ILoggerFactory loggerFactory) =>
        {
            var usageStore = context.RequestServices.GetRequiredService<IUsageStore>();
            return ForwardToUpstream(context, store, planner, clientFactory, "/v1/messages/count_tokens", secretRegistry, protector, loggerFactory, usageStore, isMessages: false);
        });

        app.MapPost("/v1/messages", (HttpContext context, JsonFileStore store, RoutePlanner planner, IHttpClientFactory clientFactory, SecretRegistry secretRegistry, ISecretProtector protector, ILoggerFactory loggerFactory) =>
        {
            var usageStore = context.RequestServices.GetRequiredService<IUsageStore>();
            return ForwardToUpstream(context, store, planner, clientFactory, "/v1/messages", secretRegistry, protector, loggerFactory, usageStore, isMessages: true);
        });
    }

    private static async Task<IResult> ForwardToUpstream(
        HttpContext context,
        JsonFileStore store,
        RoutePlanner planner,
        IHttpClientFactory clientFactory,
        string upstreamPath,
        SecretRegistry secretRegistry,
        ISecretProtector protector,
        ILoggerFactory loggerFactory,
        IUsageStore usageStore,
        bool isMessages)
    {
        var config = store.LoadConfig();

        // Request-body limit comes from GatewaySettings (default 64 MB), applied to
        // both /v1/messages and /v1/messages/count_tokens. We also raise/lower the
        // Kestrel per-request cap to match so the configured value is authoritative.
        var maxBytes = (long)Math.Max(1, config.Gateway.MaxRequestBodyMb) * 1024 * 1024;
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        if (context.Request.ContentLength is { } declared && declared > maxBytes)
        {
            return TooLarge(config.Gateway.MaxRequestBodyMb);
        }

        var bodyBytes = await ReadCappedAsync(context.Request.Body, maxBytes, context.RequestAborted);
        if (bodyBytes is null)
        {
            return TooLarge(config.Gateway.MaxRequestBodyMb);
        }

        JsonNode? requestNode = null;
        if (bodyBytes.Length > 0)
        {
            try
            {
                requestNode = JsonNode.Parse(bodyBytes);
            }
            catch
            {
                return Results.BadRequest(new { type = "error", error = new { type = "invalid_request_error", message = "Invalid JSON body" } });
            }
        }

        var streaming = isMessages && requestNode?["stream"]?.GetValue<bool>() == true;

        var requestedModel = TryGetString(requestNode, "model");
        var sessionId = context.Request.Headers.TryGetValue(SessionHeader, out var sessionValues) ? sessionValues.FirstOrDefault() : null;

        var routeLogger = loggerFactory.CreateLogger("ClaudeManager.Gateway.RouteTrace");
        var upstreamLogger = loggerFactory.CreateLogger("ClaudeManager.Gateway.Upstream");

        // Keep the route-attempt budget stable for this request. A failed 401/403
        // disables the selected key before the next iteration; if the loop condition
        // re-counts enabled keys after that mutation, a request with two keys can stop
        // after the first bad key instead of trying the second one.
        var routeAttemptBudget = Math.Max(1, config.Keys.Count(key => key.Enabled));
        IResult? exhaustedFallbackResult = null;
        for (var routeAttempt = 0; routeAttempt < routeAttemptBudget; routeAttempt++)
        {
            config = store.LoadConfig();
            var state = store.LoadState();
            var snapshot = BuildSnapshot(config);
            var profileId = state.CurrentProfileId ?? config.LaunchProfiles.FirstOrDefault(p => p.IsDefault)?.Id;

            var request = new RouteRequest
            {
                RequestId = context.TraceIdentifier,
                ProfileId = profileId,
                RequestedModel = requestedModel,
                SessionId = sessionId
            };

            var plan = planner.Plan(snapshot, request);
            var maxAttempts = Math.Max(1, config.LaunchProfiles.FirstOrDefault(p => p.Id == plan.ProfileId)?.MaxRetries ?? 3);
            await PersistRouteDecisionAsync(usageStore, plan, streaming, maxAttempts, routeLogger, context.RequestAborted);
            if (!plan.HasSelection || plan.SelectedKeyId is null || plan.SelectedProviderId is null)
            {
                return Results.Json(new { type = "error", error = new { type = "routing_error", message = plan.DecisionReason } }, statusCode: 502);
            }

            var selectedKey = config.Keys.FirstOrDefault(k => k.Id == plan.SelectedKeyId);
            var selectedProvider = config.Providers.FirstOrDefault(p => p.Id == plan.SelectedProviderId);
            if (selectedKey is null || selectedProvider is null)
            {
                return Results.Json(new { type = "error", error = new { type = "routing_error", message = "Selected key or provider not found" } }, statusCode: 502);
            }

            // ModelMode is resolved inside RoutePlanner (respect_user/prefer_profile/
            // force_profile/default). Here we only apply the planner's decision: if the
            // resolved model differs from what the body carried, rewrite the body's model.
            var finalBody = bodyBytes;
            if (!string.IsNullOrEmpty(plan.SelectedModel) && bodyBytes.Length > 0
                && !string.Equals(plan.SelectedModel, requestedModel, StringComparison.Ordinal))
            {
                var attemptNode = JsonNode.Parse(bodyBytes);
                if (attemptNode is not null)
                {
                    attemptNode["model"] = plan.SelectedModel;
                    finalBody = Encoding.UTF8.GetBytes(attemptNode.ToJsonString());
                }
            }

            // Decrypt the stored ciphertext to the real upstream key. The ciphertext and
            // the plaintext are both registered for redaction; neither must reach logs.
            string upstreamKey;
            try
            {
                upstreamKey = protector.Unprotect(selectedKey.ApiKeyEncrypted);
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
            {
                return Results.Json(new { type = "error", error = new { type = "credential_error", message = "Stored API key could not be decrypted." } }, statusCode: 502);
            }

            secretRegistry.RegisterSecret(selectedKey.ApiKeyEncrypted);
            secretRegistry.RegisterSecret(upstreamKey);

            var client = clientFactory.CreateClient("Upstream");
            var targetUrl = $"{selectedProvider.BaseUrl?.TrimEnd('/')}{upstreamPath}";

            var forward = new ForwardContext(
                context,
                client,
                store,
                upstreamLogger,
                selectedKey.Id,
                selectedProvider,
                upstreamKey,
                finalBody,
                targetUrl,
                maxAttempts,
                sessionId,
                plan.ProfileId,
                selectedProvider.Id,
                plan.SelectedModel);

            var result = streaming
                ? await ForwardStreamingAsync(forward, usageStore)
                : await ForwardBufferedAsync(forward, usageStore);
            if (result.TryNextRoute && !context.Response.HasStarted)
            {
                exhaustedFallbackResult = result.ExhaustedResult ?? exhaustedFallbackResult;
                continue;
            }

            return result.Result;
        }

        return exhaustedFallbackResult
            ?? Results.Json(new { type = "error", error = new { type = "routing_error", message = "No alternative provider/key remained after upstream failures." } }, statusCode: 502);
    }

    /// <summary>Inputs shared by the buffered and streaming forwarders.</summary>
    private sealed record ForwardContext(
        HttpContext Http,
        HttpClient Client,
        JsonFileStore Store,
        ILogger Logger,
        Guid KeyId,
        ProviderRecord Provider,
        string UpstreamKey,
        byte[] Body,
        string TargetUrl,
        int MaxAttempts,
        string? SessionId,
        string? ProfileId,
        string? ProviderId,
        string? SelectedModel);

    private sealed record ForwardResult(IResult Result, bool TryNextRoute = false, IResult? ExhaustedResult = null);

    private sealed class UpstreamErrorResult : IResult
    {
        private readonly int _statusCode;
        private readonly byte[] _body;
        private readonly IReadOnlyList<KeyValuePair<string, string[]>> _headers;
        private readonly IReadOnlyList<KeyValuePair<string, string[]>> _contentHeaders;

        private UpstreamErrorResult(
            int statusCode,
            byte[] body,
            IReadOnlyList<KeyValuePair<string, string[]>> headers,
            IReadOnlyList<KeyValuePair<string, string[]>> contentHeaders)
        {
            _statusCode = statusCode;
            _body = body;
            _headers = headers;
            _contentHeaders = contentHeaders;
        }

        public static UpstreamErrorResult From(HttpResponseMessage response, byte[] body) => new(
            (int)response.StatusCode,
            body,
            response.Headers.Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToList(),
            response.Content.Headers.Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToList());

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = _statusCode;
            foreach (var header in _headers)
            {
                httpContext.Response.Headers[header.Key] = header.Value;
            }

            foreach (var header in _contentHeaders)
            {
                httpContext.Response.Headers[header.Key] = header.Value;
            }

            if (_body.Length > 0)
            {
                await httpContext.Response.Body.WriteAsync(_body, httpContext.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    private static HttpRequestMessage BuildUpstreamRequest(ForwardContext f)
    {
        var request = new HttpRequestMessage(new HttpMethod(f.Http.Request.Method), f.TargetUrl)
        {
            Content = new ByteArrayContent(f.Body)
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", f.Http.Request.ContentType ?? "application/json");

        // Forward safe headers; strip hop-by-hop, host, content and the local gateway
        // credential headers (x-api-key / Authorization) so the local token is never
        // forwarded upstream.
        foreach (var header in f.Http.Request.Headers)
        {
            if (header.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        // Auth scheme is driven by the provider's configured AuthScheme, never inferred
        // from the provider type (that broke Aerolink/custom providers).
        ApplyAuth(request, f.Provider, f.UpstreamKey);

        // Forward provider-configured custom headers. The blocklist is enforced at
        // save/validation time; this is defense-in-depth so a header that could
        // override auth, content, host, or the local gateway token is never sent
        // even if a malformed config slipped through.
        foreach (var (name, value) in f.Provider.CustomHeaders)
        {
            if (ProviderHeaderRules.IsProtected(name)) continue;
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    /// <summary>
    /// Non-streaming path: fully buffer the upstream response, then write it once.
    /// Safe to retry on 5xx/network because nothing reaches the client until the end.
    /// Byte-for-byte equivalent to the M03 behavior.
    /// </summary>
    private static async Task<ForwardResult> ForwardBufferedAsync(ForwardContext f, IUsageStore usageStore)
    {
        var context = f.Http;
        var attempts = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var statusCode = 502;
        string? errorType = null;

        while (attempts < f.MaxAttempts)
        {
            attempts++;

            using var upstreamRequest = BuildUpstreamRequest(f);

            HttpResponseMessage response;
            byte[] responseBytes;
            try
            {
                response = await f.Client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                responseBytes = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempts < f.MaxAttempts && !context.Response.HasStarted)
                {
                    await Task.Delay(500, context.RequestAborted);
                    continue;
                }

                stopwatch.Stop();
                errorType = "network_failure";
                await PersistUsageAsync(usageStore, f, null, statusCode, errorType, stopwatch.ElapsedMilliseconds, streaming: false);
                return new ForwardResult(Results.Json(new { type = "error", error = new { type = "upstream_error", message = "Network failure" } }, statusCode: 502));
            }

            using (response)
            {
                statusCode = (int)response.StatusCode;

                if (!response.IsSuccessStatusCode && !context.Response.HasStarted)
                {
                    if (IsUpstreamAuthFailure(statusCode))
                    {
                        MarkKeyAuthFailed(f.Store, f.KeyId, statusCode, DateTimeOffset.UtcNow);
                        stopwatch.Stop();
                        await PersistUsageAsync(usageStore, f, null, statusCode, "auth_failed", stopwatch.ElapsedMilliseconds, streaming: false);
                        return new ForwardResult(Results.Empty, TryNextRoute: true, ExhaustedResult: UpstreamErrorResult.From(response, responseBytes));
                    }

                    if (statusCode is 429 or 402)
                    {
                        UpdateKeyStateOnLimit(f.Store, f.KeyId, statusCode, response, DateTimeOffset.UtcNow);
                        stopwatch.Stop();
                        var limitType = statusCode == 429 ? "rate_limit" : "billing";
                        await PersistUsageAsync(usageStore, f, null, statusCode, limitType, stopwatch.ElapsedMilliseconds, streaming: false);
                        await PersistLimitEventAsync(usageStore, f, statusCode, response, DateTimeOffset.UtcNow);
                        return new ForwardResult(Results.Empty, TryNextRoute: true, ExhaustedResult: UpstreamErrorResult.From(response, responseBytes));
                    }
                    else if (statusCode >= 500 && attempts < f.MaxAttempts)
                    {
                        await Task.Delay(1000, context.RequestAborted);
                        continue;
                    }
                }

                context.Response.StatusCode = statusCode;
                CopyResponseHeaders(response, context, includeContentHeaders: true);

                stopwatch.Stop();
                await PersistUsageAsync(usageStore, f, ParseUsageFromBuffer(responseBytes), statusCode, response.IsSuccessStatusCode ? null : $"http_{statusCode}", stopwatch.ElapsedMilliseconds, streaming: false);

                await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted);
                return new ForwardResult(Results.Empty);
            }
        }

        stopwatch.Stop();
        errorType = "max_retries_exceeded";
        await PersistUsageAsync(usageStore, f, null, 502, errorType, stopwatch.ElapsedMilliseconds, streaming: false);
        return new ForwardResult(Results.Json(new { type = "error", error = new { type = "upstream_error", message = "Max retries exceeded" } }, statusCode: 502));
    }

    /// <summary>
    /// Streaming SSE path. The <c>clientBodyStarted</c> flag is the SOLE retry gate:
    /// once a single byte has been forwarded to the client, no retry is possible and a
    /// later upstream error is passed through (logged), never replayed — so chunks are
    /// never duplicated. Safe pre-stream retry is allowed only for 5xx / pre-headers
    /// network failures before any byte is written. Bytes are forwarded verbatim and
    /// flushed per read; a copy is fed to a usage parser that never alters the stream.
    /// </summary>
    private static async Task<ForwardResult> ForwardStreamingAsync(ForwardContext f, IUsageStore usageStore)
    {
        var context = f.Http;
        var clientBodyStarted = false;
        var attempts = 0;
        var parser = new SseUsageParser();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (attempts < f.MaxAttempts)
        {
            attempts++;

            using var upstreamRequest = BuildUpstreamRequest(f);

            HttpResponseMessage response;
            try
            {
                response = await f.Client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                stopwatch.Stop();
                await PersistUsageAsync(usageStore, f, parser, 499, "client_cancelled", stopwatch.ElapsedMilliseconds, streaming: true);
                return new ForwardResult(Results.Empty);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempts < f.MaxAttempts)
                {
                    await Task.Delay(500, context.RequestAborted);
                    continue;
                }

                stopwatch.Stop();
                await PersistUsageAsync(usageStore, f, null, 502, "network_failure", stopwatch.ElapsedMilliseconds, streaming: true);
                return new ForwardResult(Results.Json(new { type = "error", error = new { type = "upstream_error", message = "Network failure" } }, statusCode: 502));
            }

            using (response)
            {
                var code = (int)response.StatusCode;

                if (!response.IsSuccessStatusCode)
                {
                    if (IsUpstreamAuthFailure(code))
                    {
                        MarkKeyAuthFailed(f.Store, f.KeyId, code, DateTimeOffset.UtcNow);
                        stopwatch.Stop();
                        await PersistUsageAsync(usageStore, f, null, code, "auth_failed", stopwatch.ElapsedMilliseconds, streaming: true);
                        var errorBody = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
                        return new ForwardResult(Results.Empty, TryNextRoute: true, ExhaustedResult: UpstreamErrorResult.From(response, errorBody));
                    }

                    if (code is 429 or 402)
                    {
                        UpdateKeyStateOnLimit(f.Store, f.KeyId, code, response, DateTimeOffset.UtcNow);
                        var limitType = code == 429 ? "rate_limit" : "billing";
                        await PersistLimitEventAsync(usageStore, f, code, response, DateTimeOffset.UtcNow);
                        stopwatch.Stop();
                        await PersistUsageAsync(usageStore, f, null, code, limitType, stopwatch.ElapsedMilliseconds, streaming: true);
                        var errorBody = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
                        return new ForwardResult(Results.Empty, TryNextRoute: true, ExhaustedResult: UpstreamErrorResult.From(response, errorBody));
                    }

                    if (code >= 500 && attempts < f.MaxAttempts)
                    {
                        await Task.Delay(1000, context.RequestAborted);
                        continue;
                    }

                    var finalErrorBody = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
                    context.Response.StatusCode = code;
                    CopyResponseHeaders(response, context, includeContentHeaders: true);
                    await context.Response.Body.WriteAsync(finalErrorBody, context.RequestAborted);
                    stopwatch.Stop();
                    await PersistUsageAsync(usageStore, f, null, code, $"http_{code}", stopwatch.ElapsedMilliseconds, streaming: true);
                    return new ForwardResult(Results.Empty);
                }

                // Success: stream the body through, preserving event order and boundaries.
                context.Response.StatusCode = code;
                CopyResponseHeaders(response, context, includeContentHeaders: false);
                CopyStreamingContentType(response, context);

                var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
                string? errorType = null;
                try
                {
                    await using var upstream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                    int read;
                    while ((read = await upstream.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        clientBodyStarted = true;
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        parser.Feed(buffer.AsSpan(0, read));
                    }
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    f.Logger.LogInformation("Streaming request cancelled by client after {Started} body start.", clientBodyStarted ? "after" : "before");
                    errorType = "client_cancelled";
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    f.Logger.LogWarning("Upstream stream ended early ({Error}); passing truncated stream through without retry.", ex.GetType().Name);
                    errorType = "upstream_stream_failure";
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                stopwatch.Stop();
                await PersistUsageAsync(usageStore, f, parser, code, errorType, stopwatch.ElapsedMilliseconds, streaming: true);
                return new ForwardResult(Results.Empty);
            }
        }

        stopwatch.Stop();
        await PersistUsageAsync(usageStore, f, null, 502, "max_retries_exceeded", stopwatch.ElapsedMilliseconds, streaming: true);
        return new ForwardResult(Results.Json(new { type = "error", error = new { type = "upstream_error", message = "Max retries exceeded" } }, statusCode: 502));
    }

    private static bool IsUpstreamAuthFailure(int statusCode) => statusCode is 401 or 403;

    private static void MarkKeyAuthFailed(JsonFileStore store, Guid keyId, int statusCode, DateTimeOffset now)
    {
        store.UpdateConfig(current => current with
        {
            Keys = current.Keys.Select(key => key.Id == keyId
                ? key with
                {
                    Enabled = false,
                    Status = KeyStatus.Disabled,
                    LastErrorAt = now,
                    LastErrorText = $"HTTP {statusCode}: upstream rejected this API key",
                    Usage = key.Usage with { FailedRuns = key.Usage.FailedRuns + 1 }
                }
                : key).ToList()
        });
    }

    private static void CopyResponseHeaders(HttpResponseMessage response, HttpContext context, bool includeContentHeaders)
    {
        foreach (var header in response.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        if (!includeContentHeaders)
        {
            return;
        }

        foreach (var header in response.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static void CopyStreamingContentType(HttpResponseMessage response, HttpContext context)
    {
        // Preserve the SSE content type; deliberately omit Content-Length so Kestrel
        // uses chunked transfer encoding for the streamed body.
        var contentType = response.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrEmpty(contentType))
        {
            context.Response.Headers.ContentType = contentType;
        }
    }

    private static void ApplyAuth(HttpRequestMessage request, ProviderRecord provider, string key)
    {
        switch (provider.AuthScheme)
        {
            case ProviderAuthScheme.Bearer:
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                break;
            case ProviderAuthScheme.Custom:
                request.Headers.TryAddWithoutValidation(
                    string.IsNullOrWhiteSpace(provider.CustomAuthHeader) ? "x-api-key" : provider.CustomAuthHeader,
                    key);
                break;
            case ProviderAuthScheme.XApiKey:
            default:
                request.Headers.TryAddWithoutValidation("x-api-key", key);
                break;
        }
    }

    /// <summary>
    /// Basic M03 routing-state update for an upstream limit. 429 marks the key
    /// five-hour-limited; 402 marks it limited (billing). Reset time prefers the
    /// upstream Retry-After / rate-limit reset headers over an estimated fallback.
    /// </summary>
    private static void UpdateKeyStateOnLimit(JsonFileStore store, Guid keyId, int code, HttpResponseMessage response, DateTimeOffset now)
    {
        var resetAt = ParseResetInstant(response, now);

        store.UpdateConfig(latest => latest with
        {
            Keys = latest.Keys.Select(key =>
            {
                if (key.Id != keyId)
                {
                    return key;
                }

                if (code == 429)
                {
                    return key with
                    {
                        Status = KeyStatus.Limited,
                        LastErrorAt = now,
                        LastErrorText = "Upstream 429 rate/quota limit",
                        FiveHourResetAt = resetAt ?? now + TimeSpan.FromHours(5),
                        FiveHourResetEstimated = resetAt is null
                    };
                }

                // 402 billing/credit: limit the key; only set a reset when the upstream gave one.
                return key with
                {
                    Status = KeyStatus.Limited,
                    LastErrorAt = now,
                    LastErrorText = "Upstream 402 billing/credit limit",
                    QuotaState = key.QuotaState with { ManualBlockedUntil = resetAt }
                };
            }).ToList()
        });
    }

    private static DateTimeOffset? ParseResetInstant(HttpResponseMessage response, DateTimeOffset now)
    {
        // Retry-After: prefer an explicit delta, then an absolute date.
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return now + delta;
            }

            if (retryAfter.Date is { } date)
            {
                return date;
            }
        }

        // Anthropic + generic rate-limit reset headers: unix seconds or ISO-8601.
        foreach (var name in new[] { "anthropic-ratelimit-unified-reset", "anthropic-ratelimit-tokens-reset", "anthropic-ratelimit-requests-reset", "x-ratelimit-reset" })
        {
            if (!response.Headers.TryGetValues(name, out var values))
            {
                continue;
            }

            var raw = values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                // Heuristic: treat very large values as epoch seconds, smaller ones as a delay.
                return unix > 100_000_000 ? DateTimeOffset.FromUnixTimeSeconds(unix) : now + TimeSpan.FromSeconds(unix);
            }

            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static RoutingSnapshot BuildSnapshot(ManagerConfig config) => new()
    {
        Now = DateTimeOffset.UtcNow,
        Providers = config.Providers,
        Keys = config.Keys.Select(k => new KeyRuntime { Key = k }).ToList(),
        Chains = config.RoutingChains,
        Profiles = config.LaunchProfiles,
        Models = config.Models,
        Pricing = config.ModelPricing,
        BudgetPolicies = config.BudgetPolicies
    };

    private static HashSet<string> ActiveProfileProviderIds(ManagerConfig config, ManagerState state)
    {
        var profileId = state.CurrentProfileId ?? config.LaunchProfiles.FirstOrDefault(p => p.IsDefault)?.Id;
        var profile = config.LaunchProfiles.FirstOrDefault(p => p.Id == profileId && p.Enabled)
            ?? config.LaunchProfiles.FirstOrDefault(p => p.IsDefault && p.Enabled)
            ?? config.LaunchProfiles.FirstOrDefault(p => p.Enabled);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profile is null)
        {
            // No usable profile: expose nothing rather than leaking unrelated providers.
            return ids;
        }

        ids.UnionWith(profile.ProviderIds);

        if (!string.IsNullOrWhiteSpace(profile.RoutingChainId))
        {
            var chain = config.RoutingChains.FirstOrDefault(c => c.Id == profile.RoutingChainId);
            if (chain is not null)
            {
                foreach (var step in chain.Steps)
                {
                    ids.UnionWith(step.ProviderIds);
                }
            }
        }

        var providers = ids.Count == 0
            ? config.Providers
            : config.Providers.Where(provider => ids.Contains(provider.Id));

        return providers
            .Where(ProviderCompatibility.IsGatewayCompatible)
            .Select(provider => provider.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonNode? node, string property)
    {
        try
        {
            return node?[property]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(Stream body, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static IResult TooLarge(int limitMb) =>
        Results.Json(
            new { type = "error", error = new { type = "invalid_request_error", message = $"Request body too large (limit {limitMb}MB)." } },
            statusCode: 413);

    // ── Phase B: usage/cost persistence ──

    private static async Task PersistRouteDecisionAsync(
        IUsageStore usageStore,
        RoutePlan plan,
        bool streaming,
        int maxAttempts,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var traceJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                requestId = plan.RequestId,
                sessionId = plan.SessionId,
                outcome = plan.Outcome.ToString(),
                decisionReason = plan.DecisionReason,
                selected = new
                {
                    providerId = plan.SelectedProviderId,
                    keyId = plan.SelectedKeyId,
                    model = plan.SelectedModel,
                    stepOrder = plan.SelectedStepOrder
                },
                modelMapping = new
                {
                    requestedModel = plan.RequestedModel,
                    resolvedModel = plan.SelectedModel,
                    upstreamModel = plan.SelectedModel
                },
                route = new
                {
                    profileId = plan.ProfileId,
                    chainId = plan.ChainId,
                    affinityHonored = plan.AffinityHonored,
                    warnings = plan.Warnings
                },
                retry = new
                {
                    maxAttempts,
                    streaming,
                    policy = streaming
                        ? "retry_before_response_body_only"
                        : "retry_before_client_response",
                    retryAfterClientBodyStarted = streaming
                        ? "blocked_to_avoid_duplicate_stream_chunks"
                        : "not_applicable"
                },
                wait = plan.Wait is null
                    ? null
                    : new
                    {
                        nearestResetUtc = plan.Wait.NearestReset.UtcDateTime.ToString("O"),
                        waitForMs = (long)plan.Wait.WaitFor.TotalMilliseconds,
                        plan.Wait.Reason
                    },
                skippedCandidates = plan.SkippedCandidates.Select(s => new
                {
                    s.ProviderId,
                    s.KeyId,
                    s.Model,
                    s.Reason
                }),
                steps = plan.Steps.Select(step => new
                {
                    step.Order,
                    step.Strategy,
                    step.Selected,
                    step.Note,
                    skipped = step.Skipped.Select(s => new
                    {
                        s.ProviderId,
                        s.KeyId,
                        s.Model,
                        s.Reason
                    })
                })
            }, RouteTraceJsonOptions);

            await usageStore.AddRouteDecisionAsync(new RouteDecisionRecord
            {
                RequestId = plan.RequestId ?? Guid.NewGuid().ToString("N"),
                Timestamp = timestamp,
                SessionId = plan.SessionId,
                ProfileId = plan.ProfileId,
                ChainId = plan.ChainId,
                SelectedProviderId = plan.SelectedProviderId,
                SelectedKeyId = plan.SelectedKeyId,
                SelectedModel = plan.SelectedModel,
                DecisionReason = plan.DecisionReason,
                TraceJson = traceJson,
                Story = BuildRouteStory(plan, timestamp)
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist route decision for request {RequestId}.", plan.RequestId);
        }
    }

    private static string BuildRouteStory(RoutePlan plan, DateTimeOffset timestamp)
    {
        var time = timestamp.LocalDateTime.ToString("g", CultureInfo.InvariantCulture);
        if (!plan.HasSelection)
        {
            return $"{time}: no route selected ({plan.DecisionReason}).";
        }

        var key = plan.SelectedKeyId is null ? "automatic key" : $"key {plan.SelectedKeyId.Value.ToString()[..8]}";
        var model = string.IsNullOrWhiteSpace(plan.SelectedModel) ? "default model" : plan.SelectedModel;
        return $"{time}: routed to {plan.SelectedProviderId} / {model} using {key} ({plan.DecisionReason}).";
    }

    private static async Task PersistUsageAsync(
        IUsageStore usageStore,
        ForwardContext f,
        SseUsageParser? parser,
        int statusCode,
        string? errorType,
        long durationMs,
        bool streaming)
    {
        try
        {
            var config = f.Store.LoadConfig();

            var inputTokens = parser?.InputTokens ?? 0L;
            var outputTokens = parser?.OutputTokens ?? 0L;
            var cacheCreation = parser?.CacheCreationInputTokens ?? 0L;
            var cacheRead = parser?.CacheReadInputTokens ?? 0L;
            var serverToolUse = parser?.ServerToolUse ?? 0L;

            // Cost: dollars from pricing → store as micros (rounded).
            var pricing = PricingCalculator.Find(config.ModelPricing, f.Provider.Id, f.SelectedModel);
            var dollars = PricingCalculator.EstimateCost(pricing, inputTokens, outputTokens, cacheRead, cacheCreation);
            long? costMicros = dollars is null ? null : (long)Math.Round(dollars.Value * 1_000_000m);
            var currency = pricing?.Currency;

            var record = new RequestUsageRecord
            {
                RequestId = f.Http.TraceIdentifier,
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = f.SessionId,
                AgentId = f.Http.Request.Headers.TryGetValue("X-Claude-Code-Agent-Id", out var agentValues) ? agentValues.FirstOrDefault() : null,
                ParentAgentId = f.Http.Request.Headers.TryGetValue("X-Claude-Code-Parent-Agent-Id", out var parentValues) ? parentValues.FirstOrDefault() : null,
                ProfileId = f.ProfileId,
                ProviderId = f.ProviderId,
                KeyId = f.KeyId,
                Model = f.SelectedModel,
                Streaming = streaming,
                StatusCode = statusCode,
                ErrorType = errorType,
                DurationMs = durationMs,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheCreationInputTokens = cacheCreation,
                CacheReadInputTokens = cacheRead,
                ServerToolUse = serverToolUse,
                EstimatedCostMicros = costMicros,
                Currency = currency
            };

            await usageStore.AddRequestAsync(record, CancellationToken.None);

            // Upsert session if a Claude Code session header was present.
            if (!string.IsNullOrEmpty(f.SessionId))
            {
                var totalTokens = inputTokens + outputTokens;
                await usageStore.UpsertSessionAsync(new SessionUpsert
                {
                    SessionId = f.SessionId,
                    Timestamp = record.Timestamp,
                    ProfileId = f.ProfileId,
                    ProviderId = f.ProviderId,
                    KeyId = f.KeyId,
                    Model = f.SelectedModel,
                    TokensDelta = totalTokens,
                    CostMicrosDelta = costMicros,
                    KeySwitched = false,
                    Status = errorType is null ? "active" : "error"
                }, CancellationToken.None);
            }

            // Write an initial usage event for the accumulated tokens.
            if (inputTokens > 0 || outputTokens > 0)
            {
                await usageStore.AddUsageEventAsync(new UsageEventRecord
                {
                    RequestId = record.RequestId,
                    EventType = parser is not null ? "stream_complete" : "response_parsed",
                    Timestamp = record.Timestamp,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CacheCreationInputTokens = cacheCreation,
                    CacheReadInputTokens = cacheRead,
                    ServerToolUse = serverToolUse
                }, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Persistence is best-effort: never break the proxy response.
            f.Logger.LogWarning(ex, "Failed to persist usage record for request {RequestId}.", f.Http.TraceIdentifier);
        }
    }

    /// <summary>Extract usage from a non-streaming (buffered) JSON response body.</summary>
    private static SseUsageParser? ParseUsageFromBuffer(byte[] responseBytes)
    {
        if (responseBytes.Length == 0) return null;

        try
        {
            var node = JsonNode.Parse(responseBytes);
            var usageNode = node?["usage"];
            if (usageNode is null) return null;

            var parser = new SseUsageParser();
            // Feed the usage alone as a message_start-like frame the parser can handle.
            // Build a minimal data: line so the parser sees the token counts.
            var wrapper = new
            {
                type = "message_start",
                message = new
                {
                    usage = new
                    {
                        input_tokens = ReadLong(usageNode, "input_tokens"),
                        output_tokens = ReadLong(usageNode, "output_tokens"),
                        cache_creation_input_tokens = ReadLong(usageNode, "cache_creation_input_tokens"),
                        cache_read_input_tokens = ReadLong(usageNode, "cache_read_input_tokens")
                    }
                }
            };
            var text = "data: " + JsonSerializer.Serialize(wrapper) + "\n\n";
            parser.Feed(Encoding.UTF8.GetBytes(text));
            return parser;
        }
        catch
        {
            return null;
        }
    }

    private static long? ReadLong(JsonNode node, string prop)
    {
        try { return node[prop]?.GetValue<long>(); }
        catch { return null; }
    }

    private static async Task PersistLimitEventAsync(
        IUsageStore usageStore,
        ForwardContext f,
        int statusCode,
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        try
        {
            var resetAt = ParseResetInstant(response, now);
            await usageStore.AddLimitEventAsync(new LimitEventRecord
            {
                RequestId = f.Http.TraceIdentifier,
                ProviderId = f.ProviderId,
                KeyId = f.KeyId,
                Model = f.SelectedModel,
                LimitType = statusCode == 429 ? "rate_limit" : "billing",
                ResetAtUtc = resetAt,
                Estimated = resetAt is null,
                Timestamp = now
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            f.Logger.LogWarning(ex, "Failed to persist limit event for request {RequestId}.", f.Http.TraceIdentifier);
        }
    }
}
