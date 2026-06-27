using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Selection;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class Stage2SelectionAndLaunchPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PriorityThenLru_UsesLowestPriorityBeforeLeastRecentlyUsed()
    {
        var highPriority = Key("high", priority: 1, lastUsed: Now.AddMinutes(-1));
        var lowPriority = Key("low", priority: 10, lastUsed: Now.AddDays(-1));

        var result = new KeySelector().Select([lowPriority, highPriority], Now, SelectionStrategy.PriorityThenLru);

        Assert.AreEqual(highPriority.Id, result.Key?.Id);
    }

    [TestMethod]
    public void Strategies_SelectExpectedKey()
    {
        var first = Key("first", priority: 20, lastUsed: Now.AddHours(-4), order: 1);
        var second = Key("second", priority: 1, lastUsed: Now.AddHours(-1), order: 2);
        var selector = new KeySelector();

        Assert.AreEqual(first.Id, selector.Select([second, first], Now, SelectionStrategy.LeastRecentlyUsed).Key?.Id);
        Assert.AreEqual(second.Id, selector.Select([first, second], Now, SelectionStrategy.PriorityOrder).Key?.Id);
        Assert.AreEqual(first.Id, selector.Select([first, second], Now, SelectionStrategy.ManualKey, first.Id).Key?.Id);
        Assert.IsNotNull(selector.Select([first, second], Now, SelectionStrategy.Random, random: new Random(7)).Key);
    }

    [TestMethod]
    public void ManualBlock_ExpiresAndMakesKeyEligible()
    {
        var blocked = Key("blocked") with { QuotaState = new KeyQuotaState { ManualBlockedUntil = Now.AddMinutes(-1) } };

        Assert.IsTrue(new KeySelector().Select([blocked], Now, SelectionStrategy.PriorityThenLru).HasKey);
    }

    [TestMethod]
    public void Planner_FallsBackToNextProviderAndResolvesProviderDefaultModel()
    {
        var officialKey = Key("official", provider: "anthropic-official");
        var config = new ManagerConfig
        {
            Providers =
            [
                ProviderPresets.Aerolink(),
                ProviderPresets.AnthropicOfficial() with { DefaultModelId = "sonnet" }
            ],
            Keys = [officialKey],
            Models = [new ModelRecord { Id = "sonnet", ProviderId = "anthropic-official", DisplayName = "Sonnet", ModelValue = "claude-sonnet-4", Source = ModelSource.Preset }],
            LaunchProfiles = [new LaunchProfile { Id = "default", Name = "Default", IsDefault = true, ProviderIds = ["aerolink", "anthropic-official"], Strategy = SelectionStrategy.ProviderFallback }]
        };

        var result = new LaunchPlanner().Plan(config, [], Now);

        Assert.IsNotNull(result);
        Assert.AreEqual("anthropic-official", result.Provider.Id);
        Assert.AreEqual(officialKey.Id, result.Key.Id);
        Assert.AreEqual("claude-sonnet-4", result.ResolvedModel);
    }

    [TestMethod]
    public void Planner_ProfileModelOverridesProviderDefaultButCliModelWins()
    {
        var config = ConfigWithSingleKey(profileModel: "profile-model", providerModel: "provider-model");

        var profile = new LaunchPlanner().Plan(config, [], Now);
        var cli = new LaunchPlanner().Plan(config, ["--model", "cli-model"], Now);

        Assert.AreEqual("profile-model", profile?.ResolvedModel);
        Assert.IsTrue(cli?.UserSuppliedModel);
        Assert.IsNull(cli?.ResolvedModel);
    }

    [TestMethod]
    public void Planner_MapsDisplayNameToModelValue_NotDisplayNameUpstream()
    {
        var config = new ManagerConfig
        {
            Providers = [ProviderPresets.Aerolink() with { DefaultModelId = "Friendly Sonnet" }],
            Keys = [Key("key")],
            Models =
            [
                new ModelRecord
                {
                    Id = "sonnet-slot",
                    ProviderId = "aerolink",
                    DisplayName = "Friendly Sonnet",
                    ModelValue = "claude-sonnet-real-id",
                    Source = ModelSource.Manual
                }
            ],
            LaunchProfiles = [new LaunchProfile { Id = "default", Name = "Default", IsDefault = true, ProviderIds = ["aerolink"] }]
        };

        var result = new LaunchPlanner().Plan(config, [], Now);

        Assert.AreEqual("claude-sonnet-real-id", result?.ResolvedModel);
        Assert.AreNotEqual("Friendly Sonnet", result?.ResolvedModel);
    }

    [TestMethod]
    public void Planner_SkipsOpenAiCompatibleProviderInLauncherMode()
    {
        var config = new ManagerConfig
        {
            Providers =
            [
                new ProviderRecord
                {
                    Id = "openai",
                    Name = "OpenAI",
                    Type = ProviderType.OpenAiCompatible,
                    BaseUrl = "https://api.openai.com/v1"
                }
            ],
            Keys = [Key("openai-key", provider: "openai")],
            LaunchProfiles = [new LaunchProfile { Id = "default", Name = "Default", IsDefault = true, ProviderIds = ["openai"] }]
        };

        var result = new LaunchPlanner().Plan(config, [], Now);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Planner_UsesFallbackProfileWhenPrimaryHasNoEligibleKey()
    {
        var key = Key("official", provider: "anthropic-official");
        var config = new ManagerConfig
        {
            Providers = [ProviderPresets.Aerolink(), ProviderPresets.AnthropicOfficial()],
            Keys = [key],
            LaunchProfiles =
            [
                new LaunchProfile { Id = "primary", Name = "Primary", IsDefault = true, ProviderIds = ["aerolink"], FallbackProfileId = "fallback" },
                new LaunchProfile { Id = "fallback", Name = "Fallback", ProviderIds = ["anthropic-official"] }
            ]
        };

        var result = new LaunchPlanner().Plan(config, [], Now);

        Assert.AreEqual("fallback", result?.Profile.Id);
        Assert.AreEqual(key.Id, result?.Key.Id);
    }

    private static ManagerConfig ConfigWithSingleKey(string? profileModel, string? providerModel) => new()
    {
        Providers = [ProviderPresets.Aerolink() with { DefaultModelId = providerModel }],
        Keys = [Key("key")],
        LaunchProfiles = [new LaunchProfile { Id = "default", Name = "Default", IsDefault = true, ProviderIds = ["aerolink"], ModelOverride = profileModel }]
    };

    private static ApiKeyRecord Key(string name, int priority = 100, DateTimeOffset? lastUsed = null, long order = 0, string provider = "aerolink") => new()
    {
        Id = Guid.NewGuid(),
        ProviderId = provider,
        Name = name,
        ApiKeyEncrypted = "encrypted",
        Priority = priority,
        LastUsedAt = lastUsed,
        AddedOrder = order
    };
}
