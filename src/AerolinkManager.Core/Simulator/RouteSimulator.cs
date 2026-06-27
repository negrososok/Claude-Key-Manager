using AerolinkManager.Core.Models;
using AerolinkManager.Core.Routing;

namespace AerolinkManager.Core.Simulator;

public sealed class RouteSimulator
{
    private readonly RoutePlanner _planner;

    public RouteSimulator(RoutePlanner? planner = null)
    {
        _planner = planner ?? new RoutePlanner();
    }

    public SimulatorTraceDto Simulate(RoutingSnapshot snapshot, RouteRequest request)
    {
        var plan = _planner.Plan(snapshot, request);

        return new SimulatorTraceDto
        {
            Outcome = plan.Outcome,
            SelectedProviderId = plan.SelectedProviderId,
            SelectedKeyId = plan.SelectedKeyId,
            SelectedModel = plan.SelectedModel,
            DecisionReason = plan.DecisionReason,
            EstimatedCost = plan.EstimatedCost,
            Currency = plan.Currency,
            AffinityHonored = plan.AffinityHonored,
            Wait = plan.Wait,
            Warnings = plan.Warnings,
            SkippedCandidates = plan.SkippedCandidates.Select(s => new SimulatorSkippedDto
            {
                ProviderId = s.ProviderId,
                KeyId = s.KeyId,
                Model = s.Model,
                Reason = s.Reason
            }).ToList(),
            FallbackAttempts = plan.Steps.Select(step => new SimulatorStepDto
            {
                Order = step.Order,
                Strategy = step.Strategy,
                Selected = step.Selected,
                Note = step.Note,
                StepSkipped = step.Skipped.Select(s => new SimulatorSkippedDto
                {
                    ProviderId = s.ProviderId,
                    KeyId = s.KeyId,
                    Model = s.Model,
                    Reason = s.Reason
                }).ToList()
            }).ToList()
        };
    }
}
