using System.Text.Json;
using System.Text.Json.Nodes;

namespace AerolinkManager.Core.Managed;

public sealed class ClaudeSettingsService
{
    public string SettingsPath { get; }

    public ClaudeSettingsService(string? userProfile = null)
    {
        SettingsPath = Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    }

    public void Apply()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        JsonObject root;
        if (File.Exists(SettingsPath))
        {
            File.Copy(SettingsPath, SettingsPath + ".bak", overwrite: true);
            root = JsonNode.Parse(File.ReadAllText(SettingsPath))?.AsObject()
                ?? throw new InvalidDataException("Claude settings.json must contain a JSON object.");
        }
        else
        {
            root = new JsonObject();
        }

        var environment = root["env"] as JsonObject ?? new JsonObject();
        root["env"] = environment;
        environment.Remove("ANTHROPIC_API_KEY");
        environment.Remove("ANTHROPIC_BASE_URL");
        environment.Remove("ANTHROPIC_MODEL");
        environment.Remove("CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY");
        environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
