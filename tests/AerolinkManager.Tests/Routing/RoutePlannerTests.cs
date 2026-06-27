using AerolinkManager.Core.Models;
using AerolinkManager.Core.Routing;
using AerolinkManager.Core.Simulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Routing;

[TestClass]
public class RoutePlannerTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void SimulatorTrace_MatchesLivePlannerTrace_ForSameSnapshot()
    {
        var key = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = "p1", Name = "k1", ApiKeyEncrypted = "enc", Priority = 1, AddedOrder = 1 };
        
        var snapshot = new RoutingSnapshot
        {
            Now = Now,
            Providers = [ new ProviderRecord { Id = "p1", Name = "P1", GatewayEnabled = true } ],
            Keys = [ new KeyRuntime { Key = key } ],
            Chains = [
                new RoutingChain
                {
                    Id = "chain1",
                    Name = "Chain 1",
                    Steps = [ new RoutingChainStep { Order = 1, ProviderIds = ["p1"] } ]
                }
            ],
            Profiles = [
                new LaunchProfile { Id = "prof", Name = "prof", Enabled = true, RoutingChainId = "chain1" }
            ],
            Models = [ new ModelRecord { Id = "m1", ProviderId = "p1", ModelValue = "m1", DisplayName = "m1", Enabled = true } ]
        };

        var request = new RouteRequest { RequestId = "1", ProfileId = "prof", RequestedModel = "m1" };

        var planner = new RoutePlanner();
        var simulator = new RouteSimulator(planner);

        var livePlan = planner.Plan(snapshot, request);
        var simTrace = simulator.Simulate(snapshot, request);

        // Assert simulator correctly exported all key properties
        Assert.AreEqual(livePlan.Outcome, simTrace.Outcome);
        Assert.AreEqual(livePlan.SelectedProviderId, simTrace.SelectedProviderId);
        Assert.AreEqual(livePlan.SelectedKeyId, simTrace.SelectedKeyId);
        Assert.AreEqual(livePlan.SelectedModel, simTrace.SelectedModel);
        Assert.AreEqual(livePlan.DecisionReason, simTrace.DecisionReason);
        Assert.AreEqual(livePlan.EstimatedCost, simTrace.EstimatedCost);
        Assert.AreEqual(livePlan.Wait?.Reason, simTrace.Wait?.Reason);
        Assert.AreEqual(livePlan.Warnings.Count, simTrace.Warnings.Count);
        
        Assert.AreEqual(livePlan.SkippedCandidates.Count, simTrace.SkippedCandidates.Count);
        for(int i = 0; i < livePlan.SkippedCandidates.Count; i++)
        {
            Assert.AreEqual(livePlan.SkippedCandidates[i].ProviderId, simTrace.SkippedCandidates[i].ProviderId);
            Assert.AreEqual(livePlan.SkippedCandidates[i].KeyId, simTrace.SkippedCandidates[i].KeyId);
            Assert.AreEqual(livePlan.SkippedCandidates[i].Reason, simTrace.SkippedCandidates[i].Reason);
        }

        Assert.AreEqual(livePlan.Steps.Count, simTrace.FallbackAttempts.Count);
        for(int i = 0; i < livePlan.Steps.Count; i++)
        {
            Assert.AreEqual(livePlan.Steps[i].Order, simTrace.FallbackAttempts[i].Order);
            Assert.AreEqual(livePlan.Steps[i].Selected, simTrace.FallbackAttempts[i].Selected);
            Assert.AreEqual(livePlan.Steps[i].Strategy, simTrace.FallbackAttempts[i].Strategy);
            Assert.AreEqual(livePlan.Steps[i].Note, simTrace.FallbackAttempts[i].Note);
        }
    }

    [TestMethod]
    public void DisplayNameRequest_ResolvesToModelValue()
    {
        var key = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = "p1", Name = "k1", ApiKeyEncrypted = "enc", Priority = 1, AddedOrder = 1 };
        var snapshot = new RoutingSnapshot
        {
            Now = Now,
            Providers = [new ProviderRecord { Id = "p1", Name = "P1", GatewayEnabled = true }],
            Keys = [new KeyRuntime { Key = key }],
            Chains =
            [
                new RoutingChain
                {
                    Id = "chain1",
                    Name = "Chain 1",
                    Steps = [new RoutingChainStep { Order = 1, ProviderIds = ["p1"] }]
                }
            ],
            Profiles = [new LaunchProfile { Id = "prof", Name = "prof", Enabled = true, RoutingChainId = "chain1" }],
            Models =
            [
                new ModelRecord
                {
                    Id = "slot-sonnet",
                    ProviderId = "p1",
                    ModelValue = "upstream-sonnet-id",
                    DisplayName = "Pretty Sonnet",
                    Enabled = true
                }
            ]
        };

        var plan = new RoutePlanner().Plan(snapshot, new RouteRequest { RequestId = "1", ProfileId = "prof", RequestedModel = "Pretty Sonnet" });

        Assert.AreEqual(RouteOutcome.Selected, plan.Outcome);
        Assert.AreEqual("Pretty Sonnet", plan.RequestedModel);
        Assert.AreEqual("upstream-sonnet-id", plan.SelectedModel);
    }

    [TestMethod]
    public void EmptyProfileWithoutChain_UsesWholeEnabledProviderPool()
    {
        var key1 = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = "p1", Name = "k1", ApiKeyEncrypted = "enc", Priority = 100, AddedOrder = 1 };
        var key2 = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = "p2", Name = "k2", ApiKeyEncrypted = "enc", Priority = 100, AddedOrder = 2 };
        var snapshot = new RoutingSnapshot
        {
            Now = Now,
            Providers =
            [
                new ProviderRecord { Id = "p1", Name = "P1", GatewayEnabled = true },
                new ProviderRecord { Id = "p2", Name = "P2", GatewayEnabled = true }
            ],
            Keys =
            [
                new KeyRuntime { Key = key1 },
                new KeyRuntime { Key = key2 }
            ],
            Chains = [],
            Profiles = [new LaunchProfile { Id = "default", Name = "Default", Enabled = true, IsDefault = true, ProviderIds = [] }],
            Models =
            [
                new ModelRecord { Id = "m1", ProviderId = "p1", ModelValue = "m1", DisplayName = "m1", Enabled = true },
                new ModelRecord { Id = "m2", ProviderId = "p2", ModelValue = "m2", DisplayName = "m2", Enabled = true }
            ]
        };

        var plan = new RoutePlanner().Plan(snapshot, new RouteRequest { RequestId = "1", ProfileId = "default", RequestedModel = "m1" });

        Assert.AreEqual(RouteOutcome.Selected, plan.Outcome);
        Assert.IsTrue(plan.SelectedProviderId is "p1" or "p2");
        Assert.IsNotNull(plan.SelectedKeyId);
    }
}
