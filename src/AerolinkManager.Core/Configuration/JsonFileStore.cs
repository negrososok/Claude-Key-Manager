using System.Text.Json;
using System.Text.Json.Serialization;

namespace AerolinkManager.Core.Configuration;

public sealed class JsonFileStore
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public JsonFileStore(AppPaths paths)
    {
        _paths = paths;
        LegacyAppDataMigrator.CopyIfNeeded(paths);
    }

    public ManagerConfig LoadConfig() => WithLock(LoadConfigUnlocked);

    public ManagerState LoadState() => WithLock(() => Read(_paths.StateFile, new ManagerState()));

    public void SaveConfig(ManagerConfig config) => WithLock(() => Write(_paths.ConfigFile, config));

    public void SaveState(ManagerState state) => WithLock(() => Write(_paths.StateFile, state));

    public ManagerConfig UpdateConfig(Func<ManagerConfig, ManagerConfig> update) => WithLock(() =>
    {
        var updated = update(LoadConfigUnlocked());
        Write(_paths.ConfigFile, updated);
        return updated;
    });

    private ManagerConfig LoadConfigUnlocked()
    {
        if (!File.Exists(_paths.ConfigFile))
        {
            return new ManagerConfig();
        }

        try
        {
            var needsMigration = NeedsMigration(_paths.ConfigFile);
            var config = new ConfigurationMigrator(_jsonOptions).ReadAndMigrate(_paths.ConfigFile);
            if (needsMigration)
            {
                config = config with { SchemaVersion = ManagerConfig.CurrentSchemaVersion };
                Write(_paths.ConfigFile, config);
            }
            return config;
        }
        catch (JsonException)
        {
            var backup = _paths.ConfigFile + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_paths.ConfigFile, backup, overwrite: false);
            return new ManagerConfig();
        }
    }

    private static bool NeedsMigration(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return !document.RootElement.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() < ManagerConfig.CurrentSchemaVersion;
    }

    private T Read<T>(string path, T fallback)
    {
        if (!File.Exists(path))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _jsonOptions) ?? fallback;
        }
        catch (JsonException exception)
        {
            var backup = path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Copy(path, backup, overwrite: false);
            _ = exception;
            return fallback;
        }
    }

    private void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, _jsonOptions));
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 5 && exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(attempt * 25);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, @"Local\ClaudeManager.ConfigStore");
        var entered = false;
        try
        {
            try
            {
                entered = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                entered = true;
            }

            if (!entered)
            {
                throw new TimeoutException("Claude Manager configuration is busy.");
            }

            return action();
        }
        finally
        {
            if (entered)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static void WithLock(Action action) => WithLock(() =>
    {
        action();
        return true;
    });
}
