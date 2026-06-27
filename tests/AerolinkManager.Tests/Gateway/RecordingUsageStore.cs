using System.Collections.Concurrent;
using AerolinkManager.Core.Storage;

namespace AerolinkManager.Tests.Gateway;

/// <summary>
/// Thread-safe in-memory <see cref="IUsageStore"/> double that records every call for
/// synchronous test assertions. No SQLite dependency, no disk I/O. All collections are
/// concurrent so the test can fire concurrent requests without races.
/// </summary>
public sealed class RecordingUsageStore : IUsageStore
{
    private readonly ConcurrentBag<RequestUsageRecord> _requests = new();
    private readonly ConcurrentBag<SessionUpsert> _sessions = new();
    private readonly ConcurrentBag<UsageEventRecord> _usageEvents = new();
    private readonly ConcurrentBag<LimitEventRecord> _limitEvents = new();
    private readonly ConcurrentBag<RouteDecisionRecord> _routeDecisions = new();
    private readonly ConcurrentBag<KeySwitchRecord> _keySwitches = new();

    public IReadOnlyList<RequestUsageRecord> Requests => _requests.ToList().AsReadOnly();
    public IReadOnlyList<SessionUpsert> Sessions => _sessions.ToList().AsReadOnly();
    public IReadOnlyList<UsageEventRecord> UsageEvents => _usageEvents.ToList().AsReadOnly();
    public IReadOnlyList<LimitEventRecord> LimitEvents => _limitEvents.ToList().AsReadOnly();
    public IReadOnlyList<RouteDecisionRecord> RouteDecisions => _routeDecisions.ToList().AsReadOnly();
    public IReadOnlyList<KeySwitchRecord> KeySwitches => _keySwitches.ToList().AsReadOnly();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AddRequestAsync(RequestUsageRecord request, CancellationToken cancellationToken = default)
    {
        _requests.Add(request);
        return Task.CompletedTask;
    }

    public Task<long> CountRequestsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((long)_requests.Count);

    public Task UpsertSessionAsync(SessionUpsert session, CancellationToken cancellationToken = default)
    {
        _sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task AddUsageEventAsync(UsageEventRecord usageEvent, CancellationToken cancellationToken = default)
    {
        _usageEvents.Add(usageEvent);
        return Task.CompletedTask;
    }

    public Task AddLimitEventAsync(LimitEventRecord limitEvent, CancellationToken cancellationToken = default)
    {
        _limitEvents.Add(limitEvent);
        return Task.CompletedTask;
    }

    public Task AddRouteDecisionAsync(RouteDecisionRecord routeDecision, CancellationToken cancellationToken = default)
    {
        _routeDecisions.Add(routeDecision);
        return Task.CompletedTask;
    }

    public Task AddKeySwitchAsync(KeySwitchRecord keySwitch, CancellationToken cancellationToken = default)
    {
        _keySwitches.Add(keySwitch);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RequestUsageRecord>> QueryRequestsAsync(RequestQuery query, CancellationToken cancellationToken = default)
    {
        var filtered = _requests.AsEnumerable();
        if (query.SessionId is { } sid) filtered = filtered.Where(r => r.SessionId == sid);
        if (query.ProviderId is { } pid) filtered = filtered.Where(r => r.ProviderId == pid);
        if (query.Model is { } m) filtered = filtered.Where(r => r.Model == m);
        if (query.From is { } from) filtered = filtered.Where(r => r.Timestamp >= from);
        if (query.To is { } to) filtered = filtered.Where(r => r.Timestamp <= to);
        if (query.StatusCode is { } sc) filtered = filtered.Where(r => r.StatusCode == sc);
        return Task.FromResult<IReadOnlyList<RequestUsageRecord>>(filtered.ToList().AsReadOnly());
    }

    public Task<IReadOnlyList<RouteDecisionRecord>> QueryRouteDecisionsAsync(RouteDecisionQuery query, CancellationToken cancellationToken = default)
    {
        var filtered = _routeDecisions.AsEnumerable();
        if (query.SessionId is { } sid) filtered = filtered.Where(r => r.SessionId == sid);
        if (query.ProfileId is { } pid) filtered = filtered.Where(r => r.ProfileId == pid);
        if (query.ProviderId is { } providerId) filtered = filtered.Where(r => r.SelectedProviderId == providerId);
        if (query.From is { } from) filtered = filtered.Where(r => r.Timestamp >= from);
        if (query.To is { } to) filtered = filtered.Where(r => r.Timestamp <= to);
        return Task.FromResult<IReadOnlyList<RouteDecisionRecord>>(filtered
            .OrderByDescending(r => r.Timestamp)
            .Take(Math.Clamp(query.Limit, 1, 500))
            .ToList()
            .AsReadOnly());
    }

    public Task<IReadOnlyList<UsageGroupRow>> QueryGroupedUsageAsync(UsageQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UsageGroupRow>>(new List<UsageGroupRow>().AsReadOnly());

    public Task<IReadOnlyList<SessionSummaryRow>> QuerySessionSummariesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionSummaryRow>>(new List<SessionSummaryRow>().AsReadOnly());

    public Task<IReadOnlyList<ProviderHealthRow>> GetProviderHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderHealthRow>>(new List<ProviderHealthRow>().AsReadOnly());

    public Task RecomputeProviderHealthAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Clear()
    {
        _requests.Clear();
        _sessions.Clear();
        _usageEvents.Clear();
        _limitEvents.Clear();
        _routeDecisions.Clear();
        _keySwitches.Clear();
    }
}
