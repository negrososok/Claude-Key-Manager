using System.Text;
using AerolinkManager.Core.Configuration;

namespace AerolinkManager.Core.Diagnostics;

public sealed class AppLogger
{
    private readonly AppPaths _paths;
    private readonly SecretSanitizer _sanitizer = new();

    public AppLogger(AppPaths paths)
    {
        _paths = paths;
    }

    public void Write(string eventName, string message, IEnumerable<string>? secrets = null)
    {
        var safe = _sanitizer.Sanitize(message.ReplaceLineEndings(" "), secrets ?? []);
        if (safe.Length > 4000)
        {
            safe = safe[..4000];
        }

        var line = $"{DateTimeOffset.Now:O}\t{eventName}\t{safe}{Environment.NewLine}";
        TryAppend(Path.Combine(_paths.LogsDirectory, $"aerolink-{DateTime.Now:yyyyMMdd}.log"), line);
    }

    private static void TryAppend(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(line);
            return;
        }
        catch
        {
            // Logging must never crash the desktop app or wrapper. Locked files,
            // antivirus hooks and broken ACLs are expected on user machines.
        }

        try
        {
            var fallback = Path.Combine(Path.GetTempPath(), "ClaudeManager", "logs", Path.GetFileName(path));
            Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
            using var stream = new FileStream(fallback, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(line);
        }
        catch
        {
            // Last resort: drop the log line.
        }
    }
}
