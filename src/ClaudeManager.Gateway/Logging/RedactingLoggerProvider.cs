using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ClaudeManager.Gateway.Logging;

public class SecretRegistry
{
    private readonly ConcurrentDictionary<string, byte> _secrets = new();

    public void RegisterSecret(string secret)
    {
        if (!string.IsNullOrWhiteSpace(secret) && secret.Length > 5)
        {
            _secrets.TryAdd(secret, 0);
        }
    }

    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        foreach (var secret in _secrets.Keys)
        {
            if (input.Contains(secret))
            {
                input = input.Replace(secret, "***REDACTED***");
            }
        }
        return input;
    }
}

public class RedactingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _innerProvider;
    private readonly SecretRegistry _secretRegistry;

    public RedactingLoggerProvider(ILoggerProvider innerProvider, SecretRegistry secretRegistry)
    {
        _innerProvider = innerProvider;
        _secretRegistry = secretRegistry;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new RedactingLogger(_innerProvider.CreateLogger(categoryName), _secretRegistry);
    }

    public void Dispose()
    {
        _innerProvider.Dispose();
    }
}

public class RedactingLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly SecretRegistry _secretRegistry;

    public RedactingLogger(ILogger inner, SecretRegistry secretRegistry)
    {
        _inner = inner;
        _secretRegistry = secretRegistry;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return _inner.IsEnabled(logLevel);
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        
        // We evaluate the log message string to redact it before passing to the inner logger
        // Note: For structured logging, we are technically flattening it to a string here if we use a simple wrapper.
        // A full structured redaction would wrap TState, but for the requirement of intercepting strings, this is the basic layer.
        
        try
        {
            _inner.Log(logLevel, eventId, state, exception, (s, e) =>
            {
                var message = formatter(s, e);
                return _secretRegistry.Redact(message);
            });
        }
        catch
        {
            // Logging must never bring down the local gateway. This protects users on
            // locked-down Windows installations where a provider such as EventLog can
            // throw after startup despite being removed from normal wiring.
        }
    }
}
