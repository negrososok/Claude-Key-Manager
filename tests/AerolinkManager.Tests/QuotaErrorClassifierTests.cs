using AerolinkManager.Core.Quota;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class QuotaErrorClassifierTests
{
    private readonly QuotaErrorClassifier _classifier = new();

    [TestMethod]
    [DataRow("3h 32m", 3, 32)]
    [DataRow("3 hours 32 minutes", 3, 32)]
    [DataRow("32m", 0, 32)]
    [DataRow("reset in 3h", 3, 0)]
    [DataRow("resets in 3h 32m", 3, 32)]
    public void ParseResetDuration_ParsesSupportedFormats(string text, int hours, int minutes)
    {
        Assert.AreEqual(new TimeSpan(hours, minutes, 0), _classifier.ParseResetDuration(text));
    }

    [TestMethod]
    public void Classify_RecognizesFiveHourLimitAndDuration()
    {
        var result = _classifier.Classify("Usage limit reached; resets in 3h 32m");

        Assert.AreEqual(QuotaLimitType.FiveHourLimit, result.Type);
        Assert.AreEqual(new TimeSpan(3, 32, 0), result.ResetAfter);
    }

    [TestMethod]
    public void Classify_WeeklySignalTakesPriority()
    {
        var result = _classifier.Classify("Weekly usage limit reached");

        Assert.AreEqual(QuotaLimitType.WeeklyLimit, result.Type);
    }

    [TestMethod]
    public void Classify_DoesNotTreatGenericFailureAsQuota()
    {
        Assert.AreEqual(QuotaLimitType.None, _classifier.Classify("Connection refused").Type);
    }
}
