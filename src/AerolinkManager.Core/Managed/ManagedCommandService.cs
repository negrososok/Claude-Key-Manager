using AerolinkManager.Core.Configuration;

namespace AerolinkManager.Core.Managed;

public sealed class ManagedCommandService
{
    private readonly AppPaths _paths;
    private readonly IUserPathStore _userPath;

    public ManagedCommandService(AppPaths paths, IUserPathStore? userPath = null)
    {
        _paths = paths;
        _userPath = userPath ?? new RegistryUserPathStore();
    }

    public void Enable(string wrapperExecutable)
    {
        Directory.CreateDirectory(_paths.BinDirectory);
        var command = $"@echo off{Environment.NewLine}\"{Path.GetFullPath(wrapperExecutable)}\" %*{Environment.NewLine}exit /b %ERRORLEVEL%{Environment.NewLine}";
        File.WriteAllText(_paths.ManagedCommandFile, command);

        var entries = UserPathEntries();
        entries.RemoveAll(entry => string.Equals(Normalize(entry), Normalize(_paths.BinDirectory), StringComparison.OrdinalIgnoreCase));
        entries.Insert(0, _paths.BinDirectory);
        _userPath.Set(string.Join(';', entries));
    }

    public void Disable()
    {
        var entries = UserPathEntries();
        entries.RemoveAll(entry => string.Equals(Normalize(entry), Normalize(_paths.BinDirectory), StringComparison.OrdinalIgnoreCase));
        _userPath.Set(string.Join(';', entries));
    }

    public static string? FindSiblingWrapper(string appExecutable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(appExecutable))!;
        var candidate = Path.Combine(directory, "ClaudeManager.Wrapper.exe");
        if (File.Exists(candidate)) return candidate;
        var legacy = Path.Combine(directory, "AerolinkManager.Wrapper.exe");
        return File.Exists(legacy) ? legacy : null;
    }

    private List<string> UserPathEntries() =>
        (_userPath.Get() ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
}

public interface IUserPathStore
{
    string? Get();
    void Set(string value);
}

public sealed class RegistryUserPathStore : IUserPathStore
{
    public string? Get() => Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
    public void Set(string value) => Environment.SetEnvironmentVariable("Path", value, EnvironmentVariableTarget.User);
}
