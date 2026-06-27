using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Routing;

public sealed record CircuitBreakerOptions
{
    /// <summary>Consecutive failures that trip a closed circuit open.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>Cooldown applied on the first open; doubles on each subsequent re-open.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(15);

    public static CircuitBreakerOptions Default { get; } = new();
}

/// <summary>
/// Pure provider-scoped circuit breaker. Evaluation never mutates; the
/// <c>On*</c> methods return the next immutable state for the state store to
/// persist atomically. A model lockout or key cooldown never reaches this code:
/// the circuit is the whole-provider scope only.
/// </summary>
public sealed class CircuitBreakerPolicy
{
    private readonly CircuitBreakerOptions _options;

    public CircuitBreakerPolicy(CircuitBreakerOptions? options = null) => _options = options ?? CircuitBreakerOptions.Default;

    /// <summary>State used for routing right now (an expired open window becomes a half-open probe slot).</summary>
    public CircuitState EffectiveState(ProviderCircuitRecord? record, DateTimeOffset now)
    {
        if (record is null)
        {
            return CircuitState.Closed;
        }

        return record.State switch
        {
            CircuitState.Open => record.OpenedUntil is { } until && now >= until ? CircuitState.HalfOpen : CircuitState.Open,
            _ => record.State
        };
    }

    /// <summary>Closed and half-open allow a request; only a still-open window blocks the whole provider.</summary>
    public bool IsSelectable(ProviderCircuitRecord? record, DateTimeOffset now) =>
        EffectiveState(record, now) != CircuitState.Open;

    public ProviderCircuitRecord OnFailure(ProviderCircuitRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        var failureCount = record.FailureCount + 1;
        if (failureCount < _options.FailureThreshold)
        {
            return record with { State = CircuitState.Closed, FailureCount = failureCount, LastFailureAt = now };
        }

        var exponent = failureCount - _options.FailureThreshold; // 0 on first trip, grows on half-open failures
        return record with
        {
            State = CircuitState.Open,
            FailureCount = failureCount,
            LastFailureAt = now,
            OpenedUntil = now + Backoff(exponent)
        };
    }

    public ProviderCircuitRecord OnSuccess(ProviderCircuitRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record with
        {
            State = CircuitState.Closed,
            FailureCount = 0,
            OpenedUntil = null,
            LastSuccessAt = now
        };
    }

    private TimeSpan Backoff(int exponent)
    {
        // Cap the exponent so the shift cannot overflow before Min clamps it.
        var safeExponent = Math.Min(exponent, 30);
        var scaled = _options.BaseBackoff.Ticks * (1L << safeExponent);
        var ticks = Math.Min(scaled, _options.MaxBackoff.Ticks);
        return TimeSpan.FromTicks(ticks);
    }
}
