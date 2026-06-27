using AerolinkManager.Core.Models;
using AerolinkManager.Core.Routing;

namespace AerolinkManager.Core.Simulator;

public sealed record SimulatorTraceDto
{
    public RouteOutcome Outcome { get; init; }
    public string? SelectedProviderId { get; init; }
    public Guid? SelectedKeyId { get; init; }
    public string? SelectedModel { get; init; }
    public string DecisionReason { get; init; } = "";
    
    public IReadOnlyList<SimulatorStepDto> FallbackAttempts { get; init; } = [];
    public IReadOnlyList<SimulatorSkippedDto> SkippedCandidates { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    
    public WaitRecommendation? Wait { get; init; }
    public decimal? EstimatedCost { get; init; }
    public string? Currency { get; init; }
    public bool AffinityHonored { get; init; }
}

public sealed record SimulatorStepDto
{
    public int Order { get; init; }
    public string Strategy { get; init; } = "";
    public bool Selected { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<SimulatorSkippedDto> StepSkipped { get; init; } = [];
}

public sealed record SimulatorSkippedDto
{
    public string ProviderId { get; init; } = "";
    public Guid? KeyId { get; init; }
    public string? Model { get; init; }
    public string Reason { get; init; } = "";
}
