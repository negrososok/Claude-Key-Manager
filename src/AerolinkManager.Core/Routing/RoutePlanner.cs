using AerolinkManager.Core.Models;
using AerolinkManager.Core.Selection;

namespace AerolinkManager.Core.Routing;

/// <summary>
/// The single deterministic route planner. The live gateway and the dry-run
/// simulator both call <see cref="Plan"/>; given the same <see cref="RoutingSnapshot"/>
/// and <see cref="RouteRequest"/> they receive byte-for-byte identical plans
/// (no clock reads, no randomness, no I/O). Provider circuit, key cooldown and
/// provider+model lockout are evaluated in independent scopes so a limitation in
/// one never leaks into another.
/// </summary>
public sealed class RoutePlanner
{
    private readonly CircuitBreakerPolicy _circuit;
    private readonly BudgetEvaluator _budgets = new();

    public RoutePlanner(CircuitBreakerPolicy? circuit = null) => _circuit = circuit ?? new CircuitBreakerPolicy();

    public RoutePlan Plan(RoutingSnapshot snapshot, RouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        var profile = ResolveProfile(snapshot, request.ProfileId);
        if (profile is null)
        {
            return Failed(request, null, null, RouteReasons.NoProfile, "No enabled launch profile is configured.");
        }

        var chain = ResolveChain(snapshot, profile);
        if (chain is null)
        {
            return Failed(request, profile.Id, null, RouteReasons.NoChain, $"Profile '{profile.Name}' references no routing chain.");
        }

        if (!chain.Enabled)
        {
            return Failed(request, profile.Id, chain.Id, RouteReasons.ChainDisabled, $"Routing chain '{chain.Name}' is disabled.");
        }

        var skippedAll = new List<SkippedCandidate>();
        var stepTraces = new List<RouteStepTrace>();
        var warnings = new List<string>();
        var affinity = ActiveAffinity(snapshot, profile, request);

        var orderedSteps = chain.Steps.OrderBy(step => step.Order).ToList();
        var stepBudget = Math.Max(1, chain.MaxFallbackSteps);
        var stepsEvaluated = 0;

        foreach (var step in orderedSteps)
        {
            if (stepsEvaluated >= stepBudget)
            {
                warnings.Add($"{RouteReasons.MaxFallbackStepsReached}: stopped after {stepBudget} step(s).");
                break;
            }

            stepsEvaluated++;
            var stepSkipped = new List<SkippedCandidate>();
            var candidates = BuildStepCandidates(snapshot, profile, chain, step, request, stepSkipped);
            skippedAll.AddRange(stepSkipped);

            if (candidates.Count == 0)
            {
                stepTraces.Add(new RouteStepTrace
                {
                    Order = step.Order,
                    Strategy = RouteStrategies.Name(step.Strategy),
                    Skipped = stepSkipped,
                    Selected = false,
                    Note = "no candidate in step"
                });
                continue;
            }

            // Session affinity: if this step can still serve the session's prior
            // route, reuse it before applying the strategy ordering.
            RouteCandidate? chosen = null;
            var affinityHonored = false;
            if (affinity is not null)
            {
                chosen = candidates.FirstOrDefault(c => c.ProviderId == affinity.ProviderId && c.KeyId == affinity.KeyId);
                affinityHonored = chosen is not null;
            }

            var cursor = RoundRobinCursor(snapshot, chain, step);
            chosen ??= RouteStrategies
                .Order(step.Strategy, candidates, cursor, profile.ManualKeyId)
                .FirstOrDefault();

            if (chosen is null)
            {
                // Manual strategy with no matching key leaves candidates unselectable.
                foreach (var candidate in candidates)
                {
                    var skip = new SkippedCandidate(candidate.ProviderId, candidate.KeyId, candidate.Model, RouteReasons.ManualKeyUnavailable);
                    stepSkipped.Add(skip);
                    skippedAll.Add(skip);
                }

                stepTraces.Add(new RouteStepTrace
                {
                    Order = step.Order,
                    Strategy = RouteStrategies.Name(step.Strategy),
                    Skipped = stepSkipped,
                    Selected = false,
                    Note = "manual key not available"
                });
                continue;
            }

            // Record the non-chosen candidates of the winning step as skipped-for-trace.
            foreach (var other in candidates.Where(c => !(c.ProviderId == chosen.ProviderId && c.KeyId == chosen.KeyId)))
            {
                stepSkipped.Add(new SkippedCandidate(other.ProviderId, other.KeyId, other.Model, "not_preferred_by_strategy"));
            }

            stepTraces.Add(new RouteStepTrace
            {
                Order = step.Order,
                Strategy = RouteStrategies.Name(step.Strategy),
                Skipped = stepSkipped,
                Selected = true
            });

            return Select(request, profile, chain, step, chosen, skippedAll, stepTraces, warnings, affinityHonored);
        }

        return NoCandidate(snapshot, request, profile, chain, skippedAll, stepTraces, warnings);
    }

    private RoutePlan Select(
        RouteRequest request,
        LaunchProfile profile,
        RoutingChain chain,
        RoutingChainStep step,
        RouteCandidate chosen,
        IReadOnlyList<SkippedCandidate> skippedAll,
        IReadOnlyList<RouteStepTrace> steps,
        IReadOnlyList<string> warnings,
        bool affinityHonored)
    {
        var strategyName = RouteStrategies.Name(step.Strategy);
        var reason = affinityHonored
            ? $"{RouteReasons.SessionAffinity}: reused {chosen.ProviderId}/{chosen.Key.Name} for session"
            : $"{strategyName}: {chosen.ProviderId}/{chosen.Key.Name} selected (priority={chosen.Key.Priority})";

        var cost = PricingCalculator.EstimateCost(
            chosen.Pricing,
            request.EstimatedInputTokens ?? 0,
            request.EstimatedOutputTokens ?? 0,
            request.EstimatedCacheReadTokens ?? 0,
            request.EstimatedCacheWriteTokens ?? 0);

        var planWarnings = warnings.ToList();
        if (chosen.Model is not null && chosen.Pricing is null && (request.EstimatedInputTokens is not null || request.EstimatedOutputTokens is not null))
        {
            planWarnings.Add($"cost_unknown: no pricing for {chosen.ProviderId}/{chosen.Model}");
        }

        return new RoutePlan
        {
            Outcome = RouteOutcome.Selected,
            RequestId = request.RequestId,
            SessionId = request.SessionId,
            ProfileId = profile.Id,
            ChainId = chain.Id,
            RequestedModel = request.RequestedModel,
            SelectedStepOrder = step.Order,
            SelectedProviderId = chosen.ProviderId,
            SelectedKeyId = chosen.KeyId,
            SelectedModel = chosen.Model,
            DecisionReason = reason,
            SkippedCandidates = skippedAll,
            Steps = steps,
            EstimatedCost = cost,
            Currency = chosen.Pricing?.Currency,
            Warnings = planWarnings,
            AffinityHonored = affinityHonored
        };
    }

    private RoutePlan NoCandidate(
        RoutingSnapshot snapshot,
        RouteRequest request,
        LaunchProfile profile,
        RoutingChain chain,
        IReadOnlyList<SkippedCandidate> skippedAll,
        IReadOnlyList<RouteStepTrace> steps,
        IReadOnlyList<string> warnings)
    {
        var nearest = NearestReset(snapshot, profile, chain);
        WaitRecommendation? wait = null;
        var outcome = RouteOutcome.NoCandidate;

        if (profile.WaitForCooldownIfNearestUnderMinutes > 0 && nearest is { } reset && reset > snapshot.Now)
        {
            var maxWait = TimeSpan.FromMinutes(profile.WaitForCooldownIfNearestUnderMinutes);
            var delta = reset - snapshot.Now;
            if (delta <= maxWait)
            {
                wait = new WaitRecommendation(reset, delta, $"Waiting for nearest cooldown: {Math.Ceiling(delta.TotalSeconds)}s.");
                outcome = RouteOutcome.WaitRecommended;
            }
        }

        return new RoutePlan
        {
            Outcome = outcome,
            RequestId = request.RequestId,
            SessionId = request.SessionId,
            ProfileId = profile.Id,
            ChainId = chain.Id,
            RequestedModel = request.RequestedModel,
            DecisionReason = outcome == RouteOutcome.WaitRecommended
                ? wait!.Reason
                : "No available provider/key/model candidate in any chain step.",
            SkippedCandidates = skippedAll,
            Steps = steps,
            Warnings = warnings,
            Wait = wait
        };
    }

    private static RoutePlan Failed(RouteRequest request, string? profileId, string? chainId, string reason, string detail) => new()
    {
        Outcome = RouteOutcome.NoCandidate,
        RequestId = request.RequestId,
        SessionId = request.SessionId,
        ProfileId = profileId,
        ChainId = chainId,
        RequestedModel = request.RequestedModel,
        DecisionReason = $"{reason}: {detail}"
    };

    private List<RouteCandidate> BuildStepCandidates(
        RoutingSnapshot snapshot,
        LaunchProfile profile,
        RoutingChain chain,
        RoutingChainStep step,
        RouteRequest request,
        List<SkippedCandidate> skipped)
    {
        var candidates = new List<RouteCandidate>();
        var providerIds = ProviderOrder(snapshot, profile, step);
        var allowedKeyIds = step.AllowedKeyIds.Count > 0 ? step.AllowedKeyIds : profile.AllowedKeyIds;

        foreach (var providerId in providerIds)
        {
            var provider = snapshot.Providers.FirstOrDefault(p => p.Id == providerId);
            if (provider is null)
            {
                skipped.Add(new SkippedCandidate(providerId, null, null, RouteReasons.ProviderUnknown));
                continue;
            }

            if (!provider.Enabled)
            {
                skipped.Add(new SkippedCandidate(providerId, null, null, RouteReasons.ProviderDisabled));
                continue;
            }

            if (!ProviderCompatibility.IsGatewayCompatible(provider))
            {
                skipped.Add(new SkippedCandidate(providerId, null, null, RouteReasons.ProviderGatewayDisabled));
                continue;
            }

            // Provider circuit: whole-provider scope.
            var circuit = snapshot.Circuits.FirstOrDefault(c => c.ProviderId == providerId);
            if (!_circuit.IsSelectable(circuit, snapshot.Now))
            {
                skipped.Add(new SkippedCandidate(providerId, null, null,
                    WithUntil(RouteReasons.ProviderCircuitOpen, circuit?.OpenedUntil)));
                continue;
            }

            var model = ResolveModel(snapshot, profile, step, provider, request);

            // Model lockout: provider+model scope only. Other models on the same
            // provider stay selectable, so we skip this provider's candidates for
            // this model rather than the whole provider.
            var lockout = model is null
                ? null
                : snapshot.ModelLockouts.FirstOrDefault(l => l.ProviderId == providerId
                    && string.Equals(l.ModelValue, model, StringComparison.OrdinalIgnoreCase));
            if (lockout is not null && (lockout.LockedUntil is null || lockout.LockedUntil > snapshot.Now))
            {
                skipped.Add(new SkippedCandidate(providerId, null, model,
                    WithUntil(RouteReasons.ModelLocked, lockout.LockedUntil)));
                continue;
            }

            var providerKeys = snapshot.Keys
                .Where(k => string.Equals(k.Key.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (providerKeys.Count == 0)
            {
                skipped.Add(new SkippedCandidate(providerId, null, model, RouteReasons.NoKeysForProvider));
                continue;
            }

            var pricing = PricingCalculator.Find(snapshot.Pricing, providerId, model);

            foreach (var runtime in providerKeys)
            {
                if (allowedKeyIds.Count > 0 && !allowedKeyIds.Contains(runtime.Key.Id))
                {
                    skipped.Add(new SkippedCandidate(providerId, runtime.Key.Id, model, RouteReasons.KeyNotAllowedInStep));
                    continue;
                }

                var normalized = runtime with { Key = KeySelector.NormalizeExpiredLimit(runtime.Key, snapshot.Now) };
                var keySkip = KeySkipReason(normalized, snapshot.Now);
                if (keySkip is not null)
                {
                    skipped.Add(new SkippedCandidate(providerId, normalized.Key.Id, model, keySkip));
                    continue;
                }

                var budgetSkip = BudgetSkipReason(snapshot, profile, step, provider, normalized.Key, model);
                if (budgetSkip is not null)
                {
                    skipped.Add(new SkippedCandidate(providerId, normalized.Key.Id, model, budgetSkip));
                    continue;
                }

                candidates.Add(new RouteCandidate
                {
                    Provider = provider,
                    KeyRuntime = normalized,
                    Model = model,
                    Pricing = pricing
                });
            }
        }

        return candidates;
    }

    private static string? KeySkipReason(KeyRuntime runtime, DateTimeOffset now)
    {
        var key = runtime.Key;
        if (!key.Enabled || key.Status == KeyStatus.Disabled)
        {
            return RouteReasons.KeyDisabled;
        }

        if (key.QuotaState.ManualBlockedUntil is { } manual && manual > now)
        {
            return WithUntil(RouteReasons.KeyManualBlocked, manual);
        }

        if (key.Status == KeyStatus.WeeklyLimited)
        {
            return WithUntil(RouteReasons.KeyWeeklyLimited, key.QuotaState.WeeklyResetAt);
        }

        if (key.Status == KeyStatus.Limited)
        {
            return WithUntil(RouteReasons.KeyFiveHourLimited, key.QuotaState.FiveHourResetAt);
        }

        if (runtime.CooldownUntil is { } cooldown && cooldown > now)
        {
            return WithUntil(RouteReasons.KeyCooldown, cooldown);
        }

        if (key.Status is not (KeyStatus.Available or KeyStatus.Active or KeyStatus.Unknown))
        {
            return RouteReasons.KeyNotEligible;
        }

        return null;
    }

    private string? BudgetSkipReason(
        RoutingSnapshot snapshot,
        LaunchProfile profile,
        RoutingChainStep step,
        ProviderRecord provider,
        ApiKeyRecord key,
        string? model)
    {
        foreach (var policy in RelevantBudgets(snapshot, profile, step, provider, key, model))
        {
            var usage = snapshot.BudgetUsages.FirstOrDefault(u => u.PolicyId == policy.Id);
            if (_budgets.IsExhausted(policy, usage, snapshot.Now))
            {
                return $"{RouteReasons.BudgetExhausted}:{policy.Id}";
            }
        }

        return null;
    }

    private static IEnumerable<BudgetPolicy> RelevantBudgets(
        RoutingSnapshot snapshot,
        LaunchProfile profile,
        RoutingChainStep step,
        ProviderRecord provider,
        ApiKeyRecord key,
        string? model)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var policy in snapshot.BudgetPolicies.Where(p => p.Enabled))
        {
            var explicitlyReferenced = policy.Id == profile.BudgetPolicyId || policy.Id == step.BudgetPolicyId;
            var scopeMatch = policy.Scope switch
            {
                BudgetScope.Profile => string.Equals(policy.ScopeId, profile.Id, StringComparison.OrdinalIgnoreCase),
                BudgetScope.Provider => string.Equals(policy.ScopeId, provider.Id, StringComparison.OrdinalIgnoreCase),
                BudgetScope.Key => string.Equals(policy.ScopeId, key.Id.ToString(), StringComparison.OrdinalIgnoreCase),
                BudgetScope.Model => model is not null && string.Equals(policy.ScopeId, model, StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            if ((explicitlyReferenced || scopeMatch) && seen.Add(policy.Id))
            {
                yield return policy;
            }
        }
    }

    private static List<string> ProviderOrder(RoutingSnapshot snapshot, LaunchProfile profile, RoutingChainStep step)
    {
        if (step.ProviderIds.Count > 0)
        {
            return step.ProviderIds.ToList();
        }

        if (profile.ProviderIds.Count > 0)
        {
            return profile.ProviderIds.ToList();
        }

        return snapshot.Providers.Where(p => p.Enabled).Select(p => p.Id).ToList();
    }

    /// <summary>
    /// Resolves the model for a candidate honoring the profile's model mode.
    /// step override &gt; profile override &gt; provider default form the "profile"
    /// model; the request body model is the "user" model.
    /// </summary>
    private static string? ResolveModel(
        RoutingSnapshot snapshot,
        LaunchProfile profile,
        RoutingChainStep step,
        ProviderRecord provider,
        RouteRequest request)
    {
        var profileModel = step.ModelOverride ?? profile.ModelOverride ?? provider.DefaultModelId;
        var userModel = request.RequestedModel;

        var chosen = profile.ModelMode switch
        {
            ModelMode.ForceProfile => profileModel ?? userModel,
            ModelMode.PreferProfile => profileModel ?? userModel,
            ModelMode.RespectUser => userModel ?? profileModel,
            _ => userModel ?? profileModel
        };

        return ResolveModelValue(snapshot, provider, chosen);
    }

    private static string? ResolveModelValue(RoutingSnapshot snapshot, ProviderRecord provider, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        var model = snapshot.Models.FirstOrDefault(m => m.Enabled
            && m.ProviderId == provider.Id
            && (m.Id == requested || m.ModelValue == requested || m.DisplayName == requested));
        return model?.ModelValue ?? requested;
    }

    private SessionAffinityRecord? ActiveAffinity(RoutingSnapshot snapshot, LaunchProfile profile, RouteRequest request)
    {
        if (!profile.SessionAffinity || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return null;
        }

        var record = snapshot.Sessions.FirstOrDefault(s => s.SessionId == request.SessionId);
        return record?.KeyId is null ? null : record;
    }

    private static int RoundRobinCursor(RoutingSnapshot snapshot, RoutingChain chain, RoutingChainStep step)
    {
        var scopeKey = $"{chain.Id}:{step.Order}";
        return snapshot.RoundRobinCursors.TryGetValue(scopeKey, out var cursor) ? cursor : 0;
    }

    private DateTimeOffset? NearestReset(RoutingSnapshot snapshot, LaunchProfile profile, RoutingChain chain)
    {
        var providerIds = chain.Steps
            .SelectMany(step => ProviderOrder(snapshot, profile, step))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resets = new List<DateTimeOffset>();

        foreach (var runtime in snapshot.Keys.Where(k => providerIds.Contains(k.Key.ProviderId)))
        {
            AddIfFuture(resets, runtime.Key.QuotaState.FiveHourResetAt, snapshot.Now);
            AddIfFuture(resets, runtime.Key.QuotaState.WeeklyResetAt, snapshot.Now);
            AddIfFuture(resets, runtime.Key.QuotaState.ManualBlockedUntil, snapshot.Now);
            AddIfFuture(resets, runtime.CooldownUntil, snapshot.Now);
        }

        foreach (var circuit in snapshot.Circuits.Where(c => providerIds.Contains(c.ProviderId)))
        {
            AddIfFuture(resets, circuit.OpenedUntil, snapshot.Now);
        }

        foreach (var lockout in snapshot.ModelLockouts.Where(l => providerIds.Contains(l.ProviderId)))
        {
            AddIfFuture(resets, lockout.LockedUntil, snapshot.Now);
        }

        return resets.Count == 0 ? null : resets.Min();
    }

    private static void AddIfFuture(List<DateTimeOffset> resets, DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is { } instant && instant > now)
        {
            resets.Add(instant);
        }
    }

    private static LaunchProfile? ResolveProfile(RoutingSnapshot snapshot, string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId)
            ? snapshot.Profiles.FirstOrDefault(p => p.Id == profileId && p.Enabled)
            : snapshot.Profiles.FirstOrDefault(p => p.IsDefault && p.Enabled)
                ?? snapshot.Profiles.FirstOrDefault(p => p.Enabled);

    /// <summary>
    /// Resolves the chain referenced by the profile. A profile with no chain (a
    /// Launcher-style profile) is given a synthetic single-step chain so it can
    /// still be planned without a regression.
    /// </summary>
    private static RoutingChain? ResolveChain(RoutingSnapshot snapshot, LaunchProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.RoutingChainId))
        {
            var chain = snapshot.Chains.FirstOrDefault(c => c.Id == profile.RoutingChainId);
            if (chain is not null)
            {
                return chain;
            }
        }

        return new RoutingChain
        {
            Id = $"synthetic:{profile.Id}",
            Name = $"Synthetic chain for {profile.Name}",
            Steps =
            [
                new RoutingChainStep
                {
                    Order = 1,
                    ProviderIds = profile.ProviderIds.ToList(),
                    AllowedKeyIds = profile.AllowedKeyIds.ToList(),
                    ModelOverride = profile.ModelOverride,
                    Strategy = profile.Strategy
                }
            ]
        };
    }

    private static string WithUntil(string reason, DateTimeOffset? until) =>
        until is { } value ? $"{reason} until {value.UtcDateTime:O}" : reason;
}
