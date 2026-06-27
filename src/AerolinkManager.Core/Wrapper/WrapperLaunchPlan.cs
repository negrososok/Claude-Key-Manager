namespace AerolinkManager.Core.Wrapper;

public sealed record WrapperLaunchPlan(string RealClaudePath, string WorkingDirectory, IReadOnlyList<string> Arguments)
{
    public static WrapperLaunchPlan Create(
        string realClaudePath,
        string managedWrapperPath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var real = Path.GetFullPath(realClaudePath);
        var managed = Path.GetFullPath(managedWrapperPath);
        if (string.Equals(real, managed, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The real Claude path resolves to the managed wrapper.");
        }

        if (!Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("The working directory must be an absolute path.", nameof(workingDirectory));
        }

        return new WrapperLaunchPlan(real, Path.GetFullPath(workingDirectory), arguments.ToArray());
    }
}
