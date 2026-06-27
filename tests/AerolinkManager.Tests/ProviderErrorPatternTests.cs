using AerolinkManager.Core.Models;
using AerolinkManager.Core.Quota;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class ProviderErrorPatternTests
{
    [TestMethod]
    public void Classifier_UsesProviderSpecificPatternAndParsesNaturalDuration()
    {
        var patterns = new ErrorPatternSetRecord { Id = "custom", Name = "Custom", LimitSignals = ["capacity pause"] };

        var result = new QuotaErrorClassifier().Classify("Capacity pause; try again in 22 minutes", patterns);

        Assert.AreEqual(QuotaLimitType.FiveHourLimit, result.Type);
        Assert.AreEqual(TimeSpan.FromMinutes(22), result.ResetAfter);
    }

    [TestMethod]
    public void Classifier_UnknownProviderErrorDoesNotBlockKey()
    {
        var result = new QuotaErrorClassifier().Classify("temporary upstream socket failure");

        Assert.AreEqual(QuotaLimitType.None, result.Type);
    }
}
