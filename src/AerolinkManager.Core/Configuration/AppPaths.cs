namespace AerolinkManager.Core.Configuration;

public sealed record AppPaths(string RootDirectory, string? LegacyRootDirectory = null)
{
    public static AppPaths Default
    {
        get
        {
            var overrideRoot = Environment.GetEnvironmentVariable("CLAUDE_MANAGER_HOME")
                ?? Environment.GetEnvironmentVariable("AEROLINK_MANAGER_HOME");
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return new AppPaths(overrideRoot);
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new AppPaths(
                Path.Combine(appData, "ClaudeManager"),
                Path.Combine(appData, "AerolinkManager"));
        }
    }

    public string ConfigFile => Path.Combine(RootDirectory, "config.json");
    public string StateFile => Path.Combine(RootDirectory, "state.json");
    public string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public string BinDirectory => Path.Combine(RootDirectory, "bin");
    public string ManagedCommandFile => Path.Combine(BinDirectory, "claude.cmd");
    public string UsageDatabaseFile => Path.Combine(RootDirectory, "usage.db");
}
