namespace AerolinkManager.Core.Diagnostics;

public sealed class SecretSanitizer
{
    public string Sanitize(string? text, IEnumerable<string> secrets)
    {
        var sanitized = text ?? string.Empty;
        foreach (var secret in secrets.Where(secret => !string.IsNullOrWhiteSpace(secret)).Distinct(StringComparer.Ordinal))
        {
            sanitized = sanitized.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return sanitized;
    }
}
