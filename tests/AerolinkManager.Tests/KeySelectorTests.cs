using AerolinkManager.Core.Models;
using AerolinkManager.Core.Selection;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class KeySelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Select_UsesLeastRecentlyUsedEligibleKey()
    {
        var older = Key("older", lastUsedAt: Now.AddHours(-2), addedOrder: 2);
        var newer = Key("newer", lastUsedAt: Now.AddHours(-1), addedOrder: 1);

        var result = new KeySelector().Select([newer, older], Now);

        Assert.AreEqual(older.Id, result.Key?.Id);
    }

    [TestMethod]
    public void Select_PrefersNeverUsedThenAdditionOrder()
    {
        var first = Key("first", addedOrder: 1);
        var second = Key("second", addedOrder: 2);

        var result = new KeySelector().Select([second, first], Now);

        Assert.AreEqual(first.Id, result.Key?.Id);
    }

    [TestMethod]
    public void Select_ReleasesExpiredFiveHourLimit()
    {
        var limited = Key("limited") with
        {
            Status = KeyStatus.FiveHourLimited,
            FiveHourResetAt = Now.AddMinutes(-1),
            FiveHourResetEstimated = true
        };

        var result = new KeySelector().Select([limited], Now);

        Assert.IsNotNull(result.Key);
        Assert.AreEqual(KeyStatus.Available, result.NormalizedKeys[0].Status);
        Assert.IsNull(result.NormalizedKeys[0].FiveHourResetAt);
    }

    [TestMethod]
    public void Select_BlocksWeeklyLimitWithUnknownReset()
    {
        var limited = Key("limited") with
        {
            Status = KeyStatus.WeeklyLimited,
            WeeklyBlockedUnknown = true
        };

        var result = new KeySelector().Select([limited], Now);

        Assert.IsFalse(result.HasKey);
        Assert.IsNull(result.NearestReset);
    }

    [TestMethod]
    public void Select_WhenNoKeyReturnsNearestKnownReset()
    {
        var first = Key("first") with { Status = KeyStatus.FiveHourLimited, FiveHourResetAt = Now.AddMinutes(22) };
        var second = Key("second") with { Status = KeyStatus.FiveHourLimited, FiveHourResetAt = Now.AddHours(1) };

        var result = new KeySelector().Select([second, first], Now);

        Assert.IsFalse(result.HasKey);
        Assert.AreEqual(Now.AddMinutes(22), result.NearestReset);
    }

    private static ApiKeyRecord Key(string name, DateTimeOffset? lastUsedAt = null, long addedOrder = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ApiKeyEncrypted = "encrypted-test-value",
        LastUsedAt = lastUsedAt,
        AddedOrder = addedOrder
    };
}
