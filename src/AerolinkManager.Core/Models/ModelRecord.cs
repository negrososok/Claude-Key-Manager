namespace AerolinkManager.Core.Models;

public enum ModelSource
{
    Manual,
    Preset,
    Discovered
}

public sealed record ModelRecord
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public required string ModelValue { get; init; }
    public bool Enabled { get; init; } = true;
    public ModelSource Source { get; init; } = ModelSource.Manual;
}
