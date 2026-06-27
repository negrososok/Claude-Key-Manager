using AerolinkManager.Core.Models;
using AerolinkManager.Core.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Routing;

[TestClass]
public class CircuitBreakerPolicyTests
{
    private readonly CircuitBreakerPolicy _circuit = new();
    private readonly DateTimeOffset _now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void NullRecord_IsSelectableAndEffectiveStateIsClosed()
    {
        Assert.IsTrue(_circuit.IsSelectable(null, _now));
        Assert.AreEqual(CircuitState.Closed, _circuit.EffectiveState(null, _now));
    }

    [TestMethod]
    public void OnFailure_BelowThreshold_RemainsClosed()
    {
        var record = new ProviderCircuitRecord { ProviderId = "test" };

        var tripped = _circuit.OnFailure(record, _now);

        Assert.AreEqual(CircuitState.Closed, tripped.State);
        Assert.AreEqual(1, tripped.FailureCount);
        Assert.AreEqual(_now, tripped.LastFailureAt);
        Assert.IsNull(tripped.OpenedUntil);
    }

    [TestMethod]
    public void OnFailure_AtThreshold_TripsOpenWithBaseBackoff()
    {
        var options = CircuitBreakerOptions.Default;
        var record = new ProviderCircuitRecord { ProviderId = "test", FailureCount = options.FailureThreshold - 1 };

        var tripped = _circuit.OnFailure(record, _now);

        Assert.AreEqual(CircuitState.Open, tripped.State);
        Assert.AreEqual(options.FailureThreshold, tripped.FailureCount);
        Assert.AreEqual(_now + options.BaseBackoff, tripped.OpenedUntil);
        Assert.IsFalse(_circuit.IsSelectable(tripped, _now));
    }

    [TestMethod]
    public void ExpiredOpen_BecomesHalfOpen_IsSelectable()
    {
        var record = new ProviderCircuitRecord
        {
            ProviderId = "test",
            State = CircuitState.Open,
            OpenedUntil = _now.AddMinutes(-1)
        };

        Assert.AreEqual(CircuitState.HalfOpen, _circuit.EffectiveState(record, _now));
        Assert.IsTrue(_circuit.IsSelectable(record, _now));
    }

    [TestMethod]
    public void OnFailure_WhileHalfOpen_TripsOpenWithExponentialBackoff()
    {
        var options = CircuitBreakerOptions.Default;
        var record = new ProviderCircuitRecord
        {
            ProviderId = "test",
            State = CircuitState.Open,
            FailureCount = options.FailureThreshold,
            OpenedUntil = _now.AddMinutes(-1)
        };

        var tripped = _circuit.OnFailure(record, _now);

        Assert.AreEqual(CircuitState.Open, tripped.State);
        Assert.AreEqual(options.FailureThreshold + 1, tripped.FailureCount);
        
        var expectedBackoff = TimeSpan.FromTicks(options.BaseBackoff.Ticks * 2);
        Assert.AreEqual(_now + expectedBackoff, tripped.OpenedUntil);
    }

    [TestMethod]
    public void OnSuccess_ClearsState_ReturnsToClosed()
    {
        var record = new ProviderCircuitRecord
        {
            ProviderId = "test",
            State = CircuitState.Open,
            FailureCount = 10,
            OpenedUntil = _now.AddMinutes(5)
        };

        var recovered = _circuit.OnSuccess(record, _now);

        Assert.AreEqual(CircuitState.Closed, recovered.State);
        Assert.AreEqual(0, recovered.FailureCount);
        Assert.IsNull(recovered.OpenedUntil);
        Assert.AreEqual(_now, recovered.LastSuccessAt);
    }
}
