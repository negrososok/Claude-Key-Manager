using AerolinkManager.Core.Models;
using AerolinkManager.Core.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Routing;

[TestClass]
public class ScopeIsolationTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static RoutingSnapshot BuildSnapshot(
        CircuitState circuit = CircuitState.Closed,
        DateTimeOffset? cooldown1 = null,
        DateTimeOffset? cooldown2 = null,
        DateTimeOffset? modelLockout = null,
        BudgetUsage? usage = null)
    {
        var key1 = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = "p1", Name = "k1", ApiKeyEncrypted = "enc", Enabled = true, Status = KeyStatus.Active, QuotaState = new KeyQuotaState(), AddedOrder = 1 };
        var key2 = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = "p1", Name = "k2", ApiKeyEncrypted = "enc", Enabled = true, Status = KeyStatus.Active, QuotaState = new KeyQuotaState(), AddedOrder = 2 };
        var key3 = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = "p2", Name = "k3", ApiKeyEncrypted = "enc", Enabled = true, Status = KeyStatus.Active, QuotaState = new KeyQuotaState(), AddedOrder = 3 };

        return new RoutingSnapshot
        {
            Now = Now,
            Providers =
            [
                new ProviderRecord { Id = "p1", Name = "P1", Enabled = true, GatewayEnabled = true },
                new ProviderRecord { Id = "p2", Name = "P2", Enabled = true, GatewayEnabled = true }
            ],
            Chains = [],
            Keys =
            [
                new KeyRuntime { Key = key1, CooldownUntil = cooldown1 },
                new KeyRuntime { Key = key2, CooldownUntil = cooldown2 },
                new KeyRuntime { Key = key3 }
            ],
            Models =
            [
                new ModelRecord { Id = "m1", ProviderId = "p1", ModelValue = "m1", DisplayName = "m1", Enabled = true },
                new ModelRecord { Id = "m2", ProviderId = "p1", ModelValue = "m2", DisplayName = "m2", Enabled = true },
                new ModelRecord { Id = "m1", ProviderId = "p2", ModelValue = "m1", DisplayName = "m1", Enabled = true }
            ],
            Profiles =
            [
                new LaunchProfile
                {
                    Id = "prof",
                    Name = "prof",
                    Enabled = true,
                    ProviderIds = ["p1", "p2"],
                    ModelOverride = "m1"
                }
            ],
            Circuits =
            [
                new ProviderCircuitRecord
                {
                    ProviderId = "p1",
                    State = circuit,
                    OpenedUntil = circuit == CircuitState.Open ? Now.AddMinutes(5) : null
                }
            ],
            ModelLockouts = modelLockout is null ? [] :
            [
                new ModelLockoutRecord
                {
                    ProviderId = "p1",
                    ModelValue = "m1",
                    LockedUntil = modelLockout,
                    Reason = "locked",
                    Estimated = true
                }
            ],
            BudgetPolicies =
            [
                new BudgetPolicy { Id = "budget1", Name = "budget1", Enabled = true, Limit = 100, Type = BudgetType.Requests, Scope = BudgetScope.Key, ScopeId = key1.Id.ToString() }
            ],
            BudgetUsages = usage is null ? [] : [usage]
        };
    }

    [TestMethod]
    public void ProviderCircuit_BlocksWholeProvider_ButNotOtherProviders()
    {
        var snapshot = BuildSnapshot(circuit: CircuitState.Open);
        var planner = new RoutePlanner();
        var request = new RouteRequest { RequestId = "1", ProfileId = "prof" };

        var plan = planner.Plan(snapshot, request);

        Assert.AreEqual("p2", plan.SelectedProviderId);
        
        var p1Skips = plan.SkippedCandidates.Where(s => s.ProviderId == "p1").ToList();
        Assert.IsTrue(p1Skips.Count > 0);
        foreach(var skip in p1Skips) { Assert.IsTrue(skip.Reason.Contains(RouteReasons.ProviderCircuitOpen)); }
    }

    [TestMethod]
    public void KeyCooldown_BlocksOnlyOneKey_NotWholeProvider()
    {
        var snapshot = BuildSnapshot(cooldown1: Now.AddMinutes(5));
        var planner = new RoutePlanner();
        var request = new RouteRequest { RequestId = "1", ProfileId = "prof" };

        var plan = planner.Plan(snapshot, request);

        Assert.AreEqual("p1", plan.SelectedProviderId);
        var selectedKey = snapshot.Keys.FirstOrDefault(k => k.Key.Id == plan.SelectedKeyId)?.Key.Name;
        Assert.AreEqual("k2", selectedKey);

        var k1Skip = plan.SkippedCandidates.FirstOrDefault(s => s.KeyId.HasValue && snapshot.Keys.First(k => k.Key.Id == s.KeyId.Value).Key.Name == "k1");
        Assert.IsNotNull(k1Skip);
        Assert.IsTrue(k1Skip.Reason.Contains(RouteReasons.KeyCooldown));
    }

    [TestMethod]
    public void ModelLockout_BlocksOnlyProviderAndModel_NotWholeProvider()
    {
        var snapshot = BuildSnapshot(modelLockout: Now.AddMinutes(5));
        var planner = new RoutePlanner();
        
        var requestM1 = new RouteRequest { RequestId = "1", ProfileId = "prof", RequestedModel = "m1" };
        var planM1 = planner.Plan(snapshot, requestM1);

        Assert.AreEqual("p2", planM1.SelectedProviderId);
        var p1Skips = planM1.SkippedCandidates.Where(s => s.ProviderId == "p1").ToList();
        foreach(var skip in p1Skips) { Assert.IsTrue(skip.Reason.Contains(RouteReasons.ModelLocked)); }

        var newProfile = snapshot.Profiles[0] with { ModelMode = ModelMode.RespectUser, ModelOverride = null };
        snapshot = snapshot with { Profiles = [newProfile] };
        var requestM2 = new RouteRequest { RequestId = "2", ProfileId = "prof", RequestedModel = "m2" };
        var planM2 = planner.Plan(snapshot, requestM2);

        Assert.AreEqual("p1", planM2.SelectedProviderId);
        Assert.AreEqual("m2", planM2.SelectedModel);
    }

    [TestMethod]
    public void BudgetScope_BlocksOnlyIntendedScope()
    {
        var usage = new BudgetUsage { PolicyId = "budget1", UsedRequests = 100, WindowStart = Now.AddHours(-1) };
        var snapshot = BuildSnapshot(usage: usage);
        var planner = new RoutePlanner();
        var request = new RouteRequest { RequestId = "1", ProfileId = "prof" };

        var plan = planner.Plan(snapshot, request);

        Assert.AreEqual("p1", plan.SelectedProviderId);
        var selectedKey = snapshot.Keys.FirstOrDefault(k => k.Key.Id == plan.SelectedKeyId)?.Key.Name;
        Assert.AreEqual("k2", selectedKey);

        var k1Skip = plan.SkippedCandidates.FirstOrDefault(s => s.KeyId.HasValue && snapshot.Keys.First(k => k.Key.Id == s.KeyId.Value).Key.Name == "k1");
        Assert.IsNotNull(k1Skip);
        Assert.IsTrue(k1Skip.Reason.Contains(RouteReasons.BudgetExhausted));
    }
}
