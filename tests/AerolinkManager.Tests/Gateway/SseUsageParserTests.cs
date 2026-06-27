using System.Text;
using AerolinkManager.Core.Usage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AerolinkManager.Tests.Gateway;

[TestClass]
public sealed class SseUsageParserTests
{
    private static void Feed(SseUsageParser parser, string text) =>
        parser.Feed(Encoding.UTF8.GetBytes(text));

    [TestMethod]
    public void ParsesInputAndCacheTokensAndModelFromMessageStart()
    {
        var parser = new SseUsageParser();
        Feed(parser, "event: message_start\n");
        Feed(parser, "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-opus-4-8\",\"usage\":{\"input_tokens\":100,\"cache_creation_input_tokens\":7,\"cache_read_input_tokens\":3,\"output_tokens\":1}}}\n");
        Feed(parser, "\n");

        Assert.AreEqual(100, parser.InputTokens);
        Assert.AreEqual(7, parser.CacheCreationInputTokens);
        Assert.AreEqual(3, parser.CacheReadInputTokens);
        Assert.AreEqual(1, parser.OutputTokens);
        Assert.AreEqual("claude-opus-4-8", parser.Model);
    }

    [TestMethod]
    public void TracksCumulativeOutputTokensFromMessageDelta()
    {
        var parser = new SseUsageParser();
        Feed(parser, "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");
        Feed(parser, "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":25}}\n\n");
        Feed(parser, "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":50}}\n\n");

        Assert.AreEqual(10, parser.InputTokens);
        Assert.AreEqual(50, parser.OutputTokens, "output_tokens is cumulative — latest delta wins.");
    }

    [TestMethod]
    public void HandlesFrameSplitAcrossFeedBuffers()
    {
        var parser = new SseUsageParser();
        const string line = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":42,\"output_tokens\":0}}}\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        // Feed one byte at a time — the parser must reassemble the line.
        foreach (var b in bytes)
        {
            parser.Feed(new[] { b });
        }

        Assert.AreEqual(42, parser.InputTokens);
    }

    [TestMethod]
    public void IgnoresUnknownEventsCommentsAndMalformedJson()
    {
        var parser = new SseUsageParser();
        Feed(parser, ": this is a comment / ping\n\n");
        Feed(parser, "event: some_future_event\n");
        Feed(parser, "data: {\"type\":\"some_future_event\",\"foo\":\"bar\"}\n\n");
        Feed(parser, "data: {not valid json\n\n");
        Feed(parser, "data: [DONE]\n\n");
        Feed(parser, "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":9}}\n\n");

        Assert.AreEqual(0, parser.InputTokens);
        Assert.AreEqual(9, parser.OutputTokens);
        Assert.IsNull(parser.Model);
    }
}
