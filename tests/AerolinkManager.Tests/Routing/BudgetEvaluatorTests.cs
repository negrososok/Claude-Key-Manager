using AerolinkManager.Core.Models;
using AerolinkManager.Core.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Routing;

[TestClass]
public class BudgetEvaluatorTests
{
    private readonly BudgetEvaluator _evaluator = new();

    [TestMethod]
    public void DisabledOrNoLimit_AlwaysNotExhausted()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new BudgetPolicy
        {
            Id = "p1", Name = "p1", Enabled = false, Limit = 100, Type = BudgetType.Cost
        };
        var usage = new BudgetUsage { PolicyId = "p1", UsedCost = 200, WindowStart = now.AddDays(-1) };

        Assert.IsFalse(_evaluator.IsExhausted(policy, usage, now));
        Assert.IsNull(_evaluator.Remaining(policy, usage, now));

        var policy2 = policy with { Enabled = true, Limit = 0 };
        Assert.IsFalse(_evaluator.IsExhausted(policy2, usage, now));
        Assert.IsNull(_evaluator.Remaining(policy2, usage, now));
    }

    [TestMethod]
    public void OldUsage_DoesNotCountTowardsCurrentWindow()
    {
        var now = new DateTimeOffset(2024, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var policy = new BudgetPolicy
        {
            Id = "p1", Name = "p1", Enabled = true, Limit = 100, Type = BudgetType.Cost, Window = BudgetWindow.Daily
        };

        var usage = new BudgetUsage
        {
            PolicyId = "p1",
            UsedCost = 150,
            WindowStart = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero) 
        };

        Assert.IsFalse(_evaluator.IsExhausted(policy, usage, now));
        Assert.AreEqual(100m, _evaluator.Remaining(policy, usage, now));
    }

    [TestMethod]
    public void CurrentUsage_ExceedsLimit_IsExhausted()
    {
        var now = new DateTimeOffset(2024, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var policy = new BudgetPolicy
        {
            Id = "p1", Name = "p1", Enabled = true, Limit = 100, Type = BudgetType.Requests, Window = BudgetWindow.Monthly
        };

        var usage = new BudgetUsage
        {
            PolicyId = "p1",
            UsedRequests = 101,
            WindowStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        Assert.IsTrue(_evaluator.IsExhausted(policy, usage, now));
        Assert.AreEqual(0m, _evaluator.Remaining(policy, usage, now));
    }

    [TestMethod]
    public void WindowStart_Weekly_ComputesCorrectIsoWeek()
    {
        var startOfWeek = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var policy = new BudgetPolicy { Id = "p", Name = "p", Window = BudgetWindow.Weekly };

        var mondayWindow = _evaluator.WindowStart(policy, new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var sundayWindow = _evaluator.WindowStart(policy, new DateTimeOffset(2024, 1, 7, 23, 59, 0, TimeSpan.Zero));
        var nextMondayWindow = _evaluator.WindowStart(policy, new DateTimeOffset(2024, 1, 8, 0, 0, 0, TimeSpan.Zero));

        Assert.AreEqual(startOfWeek, mondayWindow);
        Assert.AreEqual(startOfWeek, sundayWindow);
        Assert.AreEqual(startOfWeek.AddDays(7), nextMondayWindow);
    }
}
