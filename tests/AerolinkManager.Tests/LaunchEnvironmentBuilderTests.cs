using AerolinkManager.Core.Models;
using AerolinkManager.Core.Selection;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class LaunchEnvironmentBuilderTests
{
    [TestMethod]
    public void CompatibleProvider_AddsBaseModelDiscoveryAndCustomEnvironment()
    {
        var provider = ProviderPresets.Aerolink() with
        {
            ModelDiscoveryEnabled = true,
            Env = new Dictionary<string, string> { ["CUSTOM"] = "kept" }
        };
        var environment = new LaunchEnvironmentBuilder().Build(Decision(provider, "claude-model", false), "secret");

        Assert.AreEqual("secret", environment["ANTHROPIC_API_KEY"]);
        Assert.AreEqual("https://capi.aerolink.lat/", environment["ANTHROPIC_BASE_URL"]);
        Assert.AreEqual("claude-model", environment["ANTHROPIC_MODEL"]);
        Assert.AreEqual("1", environment["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"]);
        Assert.AreEqual("1", environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.AreEqual("kept", environment["CUSTOM"]);
    }

    [TestMethod]
    public void OfficialProvider_RemovesBaseAndCliModelPreventsModelEnvironment()
    {
        var provider = ProviderPresets.AnthropicOfficial() with
        {
            Env = new Dictionary<string, string>
            {
                ["ANTHROPIC_BASE_URL"] = "must-not-leak",
                ["ANTHROPIC_MODEL"] = "must-not-leak",
                ["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "must-not-leak"
            }
        };
        var environment = new LaunchEnvironmentBuilder().Build(Decision(provider, null, true), "secret");

        Assert.IsFalse(environment.ContainsKey("ANTHROPIC_BASE_URL"));
        Assert.IsFalse(environment.ContainsKey("ANTHROPIC_MODEL"));
        Assert.IsFalse(environment.ContainsKey("CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"));
    }

    [TestMethod]
    public void Build_RejectsNonAnthropicProviderProtocols()
    {
        var provider = new ProviderRecord
        {
            Id = "openai",
            Name = "OpenAI",
            Type = ProviderType.OpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1"
        };

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            new LaunchEnvironmentBuilder().Build(Decision(provider, "gpt-4.1", false), "secret"));

        StringAssert.Contains(ex.Message, "not Launcher-compatible");
    }

    [TestMethod]
    public void ProviderCompatibility_ReturnsHumanLabelResourceKeys()
    {
        var openAi = new ProviderRecord { Id = "openai", Name = "OpenAI", Type = ProviderType.OpenAiCompatible };
        var custom = ProviderPresets.Custom();

        Assert.AreEqual("ProviderProtocolOpenAi", ProviderCompatibility.ProtocolResourceKey(openAi));
        Assert.AreEqual("ProviderSupportNotSupportedYet", ProviderCompatibility.SupportResourceKey(openAi));
        Assert.AreEqual("ProviderProtocolAnthropic", ProviderCompatibility.ProtocolResourceKey(custom));
        Assert.AreEqual("ProviderSupportExperimental", ProviderCompatibility.SupportResourceKey(custom));
    }

    private static LaunchDecision Decision(ProviderRecord provider, string? model, bool cliModel)
    {
        var key = new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = provider.Id, Name = "key", ApiKeyEncrypted = "encrypted" };
        var profile = new LaunchProfile { Id = "default", Name = "Default", ProviderIds = [provider.Id], IsDefault = true };
        return new LaunchDecision(profile, provider, key, model, cliModel, [key], null);
    }
}
