using System.Net;
using ClaudeManager.Gateway.Security;
using ClaudeManager.Gateway.Logging;
using ClaudeManager.Gateway.Endpoints;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Routing;
using AerolinkManager.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeManager.Gateway;

public class Program
{
    public static void Main(string[] args)
    {
        var app = Build(args);
        // Ensure the usage database schema exists before accepting requests.
        app.Services.GetRequiredService<AerolinkManager.Core.Storage.IUsageStore>().InitializeAsync().GetAwaiter().GetResult();
        app.Run();
    }

    /// <summary>
    /// Builds the production gateway pipeline. Exposed so tests can exercise the
    /// real loopback binding, local-auth, redacting-logger and endpoint wiring
    /// (the <paramref name="configure"/> hook lets a test override AppPaths and the
    /// upstream HttpClient without forking this configuration into a test startup).
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // This gateway is a local desktop helper, not a Windows service. The default
        // Windows EventLog provider can throw "Access denied" on locked-down user
        // machines and crash the gateway during startup/shutdown logging. Keep logs
        // process-local and wrap them with the redactor below.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        // Bind to 127.0.0.1 (Loopback). Use 17844 in production, 0 in tests.
        var port = builder.Configuration.GetValue<int?>("Gateway:Port") ?? 17844;
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        // Add services
        builder.Services.AddRouting();
        builder.Services.AddHttpClient("Upstream");
        builder.Services.AddSingleton<AppPaths>(AppPaths.Default);
        builder.Services.AddSingleton<JsonFileStore>();
        builder.Services.AddSingleton<RoutePlanner>();
        builder.Services.AddSingleton<ISecretProtector, WindowsDpapiSecretProtector>();
        builder.Services.AddSingleton<AerolinkManager.Core.Storage.IUsageStore>(sp =>
            new ClaudeManager.Storage.SqliteUsageStore(sp.GetRequiredService<AppPaths>().UsageDatabaseFile));

        builder.Services.AddSingleton<SecretRegistry>(sp =>
        {
            var store = sp.GetRequiredService<JsonFileStore>();
            var protector = sp.GetRequiredService<ISecretProtector>();
            var config = store.LoadConfig();
            var registry = new SecretRegistry();
            var stored = config.Gateway.LocalAuthTokenEncrypted;
            if (!string.IsNullOrEmpty(stored))
            {
                // Redact both the stored ciphertext and the unprotected plaintext.
                registry.RegisterSecret(stored);
                try { registry.RegisterSecret(protector.Unprotect(stored)); }
                catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception) { }
            }

            // Provider custom header values may carry secrets (signing tokens, extra
            // API credentials). Register them so they are redacted from gateway logs.
            foreach (var provider in config.Providers)
            {
                foreach (var value in provider.CustomHeaders.Values)
                {
                    registry.RegisterSecret(value);
                }
            }
            return registry;
        });

        // Test hook: override AppPaths / Upstream HttpClient before the logger wrap.
        configure?.Invoke(builder);

        // Wrap all ILoggerProviders with RedactingLoggerProvider
        var providerDescriptors = builder.Services.Where(d => d.ServiceType == typeof(ILoggerProvider)).ToList();
        foreach (var descriptor in providerDescriptors)
        {
            builder.Services.Remove(descriptor);
            builder.Services.Add(new ServiceDescriptor(typeof(ILoggerProvider), sp =>
            {
                var registry = sp.GetRequiredService<SecretRegistry>();
                ILoggerProvider inner;
                if (descriptor.ImplementationInstance != null)
                    inner = (ILoggerProvider)descriptor.ImplementationInstance;
                else if (descriptor.ImplementationFactory != null)
                    inner = (ILoggerProvider)descriptor.ImplementationFactory(sp);
                else
                    inner = (ILoggerProvider)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);

                return new RedactingLoggerProvider(inner, registry);
            }, descriptor.Lifetime));
        }

        var app = builder.Build();

        app.UseMiddleware<SanitizationMiddleware>();
        app.UseMiddleware<LocalAuthMiddleware>();

        app.MapGet("/health", (AerolinkManager.Core.Configuration.JsonFileStore store, AerolinkManager.Core.Storage.IUsageStore usageStore) =>
        {
            try
            {
                var config = store.LoadConfig();
                var availableKeys = config.Keys.Count(k => k.Enabled && k.Status is AerolinkManager.Core.Models.KeyStatus.Available or AerolinkManager.Core.Models.KeyStatus.Active);
                var limitedKeys = config.Keys.Count(k => k.Enabled && k.Status is AerolinkManager.Core.Models.KeyStatus.Limited or AerolinkManager.Core.Models.KeyStatus.WeeklyLimited);
                return Results.Ok(new
                {
                    status = "healthy",
                    version = "1.0.0",
                    routing_mode = config.Gateway.RoutingMode.ToString(),
                    database = "reachable",
                    usage_store = "reachable",
                    provider_count = config.Providers.Count(p => p.Enabled),
                    key_count = config.Keys.Count,
                    available_keys = availableKeys,
                    limited_keys = limitedKeys
                });
            }
            catch
            {
                return Results.Ok(new { status = "healthy", version = "1.0.0", database = "degraded" });
            }
        });

        app.MapClaudeEndpoints();

        return app;
    }
}
