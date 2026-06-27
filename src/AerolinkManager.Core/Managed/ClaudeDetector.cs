using System.Diagnostics;
using AerolinkManager.Core.Configuration;

namespace AerolinkManager.Core.Managed;

public sealed class ClaudeDetector
{
    public IReadOnlyList<string> FindCandidates(AppPaths paths)
    {
        var start = new ProcessStartInfo
        {
            FileName = "where.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("claude");
        using var process = Process.Start(start);
        if (process is null)
        {
            return [];
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        if (process.ExitCode != 0)
        {
            return [];
        }

        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Where(path => !IsManaged(path, paths))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(LaunchRank)
            .ToArray();
    }

    public static bool IsManaged(string path, AppPaths paths) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(paths.BinDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // `where claude` can list an extension-less npm shim before the real Windows
    // executable. Such a shim cannot be started with UseShellExecute=false
    // (Win32 error 193), so prefer directly launchable executables. Ordering is
    // stable, so the original `where` order is preserved within each rank.
    private static int LaunchRank(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".exe" or ".com" => 0,
        ".cmd" or ".bat" => 1,
        _ => 2
    };
}
