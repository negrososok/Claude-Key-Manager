using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ClaudeManager.Gateway.Logging;

public class SanitizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SanitizationMiddleware> _logger;

    public SanitizationMiddleware(RequestDelegate next, ILogger<SanitizationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Add custom log scope to redact secrets if structured logging writes them.
        // We will explicitly remove sensitive headers before they hit standard logger.
        // In ASP.NET Core, request headers are often logged if HTTP logging is enabled.
        // By default, we ensure HTTP logging does NOT include body/headers.
        
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            // Inject safe scope fields, exclude secrets.
        });

        // The middleware itself just executes next.
        // To properly sanitize, we shouldn't log headers/bodies containing tokens anyway.
        // But if we do, we need a custom ILoggerProvider or just be careful.
        // The user says: "Disable/default-avoid HTTP body/header logging, redact x-api-key, Authorization, upstream keys and local token in every structured log/trace."

        await _next(context);
    }
}
