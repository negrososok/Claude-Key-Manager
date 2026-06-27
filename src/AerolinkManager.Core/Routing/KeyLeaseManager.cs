using System.Collections.Concurrent;

namespace AerolinkManager.Core.Routing;

/// <summary>Supplies bounded retry jitter. Injectable so tests stay deterministic.</summary>
public interface IJitterProvider
{
    TimeSpan Next(TimeSpan max);
}

/// <summary>Zero jitter — used by the simulator and by tests for reproducibility.</summary>
public sealed class NoJitterProvider : IJitterProvider
{
    public static NoJitterProvider Instance { get; } = new();
    public TimeSpan Next(TimeSpan max) => TimeSpan.Zero;
}

/// <summary>Uniform jitter in <c>[0, max)</c> for live retry spreading.</summary>
public sealed class RandomJitterProvider : IJitterProvider
{
    private readonly Random _random;

    public RandomJitterProvider(Random? random = null) => _random = random ?? Random.Shared;

    public TimeSpan Next(TimeSpan max) =>
        max <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks((long)(_random.NextDouble() * max.Ticks));
}

/// <summary>
/// Anti-thundering-herd guard. When several Claude Code sessions retry at once
/// and one key just hit a limit, the next key must not be hammered by all of
/// them simultaneously. Each key gets its own async lock so unrelated keys are
/// never serialized, and the lease applies bounded jitter inside the critical
/// section. Pure-async, no timers — safe to use from the gateway request path.
/// </summary>
public sealed class KeyLeaseManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly IJitterProvider _jitter;
    private readonly TimeSpan _maxJitter;

    public KeyLeaseManager(IJitterProvider? jitter = null, TimeSpan? maxJitter = null)
    {
        _jitter = jitter ?? new RandomJitterProvider();
        _maxJitter = maxJitter ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>
    /// Runs <paramref name="action"/> while holding the per-key lock. A bounded
    /// jitter delay is applied before the action so concurrent retries on the
    /// same key fan out in time. <paramref name="delay"/> lets tests observe the
    /// jitter without real waiting (defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).
    /// </summary>
    public async Task<T> LeaseAsync<T>(
        Guid keyId,
        Func<Task<T>> action,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var gate = _locks.GetOrAdd(keyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jitter = _jitter.Next(_maxJitter);
            if (jitter > TimeSpan.Zero)
            {
                var sleep = delay ?? Task.Delay;
                await sleep(jitter, cancellationToken).ConfigureAwait(false);
            }

            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>True while another caller holds the lease for <paramref name="keyId"/>.</summary>
    public bool IsHeld(Guid keyId) => _locks.TryGetValue(keyId, out var gate) && gate.CurrentCount == 0;

    public void Dispose()
    {
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }

        _locks.Clear();
    }
}
