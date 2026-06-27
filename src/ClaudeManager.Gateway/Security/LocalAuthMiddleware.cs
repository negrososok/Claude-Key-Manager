using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClaudeManager.Gateway.Security;

public class LocalAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LocalAuthMiddleware> _logger;
    private readonly string _localToken;

    public LocalAuthMiddleware(RequestDelegate next, AerolinkManager.Core.Configuration.JsonFileStore store, AerolinkManager.Core.Security.ISecretProtector protector, ILogger<LocalAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        // The token is stored protected (DPAPI ciphertext). Unprotect once here and
        // compare the provided plaintext against it. The plaintext token is never
        // persisted, and neither plaintext nor ciphertext is logged.
        var stored = store.LoadConfig().Gateway.LocalAuthTokenEncrypted;
        if (string.IsNullOrEmpty(stored))
        {
            _localToken = string.Empty;
            _logger.LogWarning("Gateway local auth token is not configured. Gateway will reject all requests.");
            return;
        }

        try
        {
            _localToken = protector.Unprotect(stored);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        {
            _localToken = string.Empty;
            _logger.LogWarning("Gateway local auth token could not be unprotected. Gateway will reject all requests.");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. Loopback check
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp != null && !IPAddress.IsLoopback(remoteIp))
        {
            _logger.LogWarning("Rejected non-loopback request from {RemoteIp}", remoteIp);
            context.Response.StatusCode = 403;
            return;
        }

        // 2. Local Token Auth check
        string? providedToken = null;

        // Check x-api-key
        if (context.Request.Headers.TryGetValue("x-api-key", out var apiKeyValues))
        {
            providedToken = apiKeyValues.FirstOrDefault();
        }

        // Check Authorization: Bearer
        if (string.IsNullOrEmpty(providedToken) && context.Request.Headers.TryGetValue("Authorization", out var authValues))
        {
            var authHeader = authValues.FirstOrDefault();
            if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                providedToken = authHeader.Substring("Bearer ".Length).Trim();
            }
        }

        if (string.IsNullOrEmpty(providedToken))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized: Missing token.");
            return;
        }

        if (string.IsNullOrEmpty(_localToken))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized: Gateway not configured.");
            return;
        }

        // Constant-time comparison
        var providedBytes = Encoding.UTF8.GetBytes(providedToken);
        var expectedBytes = Encoding.UTF8.GetBytes(_localToken);

        if (providedBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized: Invalid token.");
            return;
        }

        await _next(context);
    }
}
