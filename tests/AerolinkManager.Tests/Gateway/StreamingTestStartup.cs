using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AerolinkManager.Tests.Gateway;

/// <summary>
/// Mirrors the production gateway pipeline (loopback host) but wires the streaming-capable
/// <see cref="StreamingMockHandler"/> as the upstream so SSE behavior can be exercised over
/// a real loopback socket. Kept separate from <c>TestStartup</c> so the existing
/// non-streaming tests stay on the simple buffered mock.
/// </summary>
public class StreamingTestStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddRouting();
        services.AddSingleton<StreamingMockHandler>();
        services.AddHttpClient("Upstream").ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<StreamingMockHandler>());
        services.AddSingleton<AerolinkManager.Core.Configuration.JsonFileStore>();
        services.AddSingleton<AerolinkManager.Core.Routing.RoutePlanner>();
        services.AddSingleton<AerolinkManager.Core.Security.ISecretProtector, FakeSecretProtector>();
        var usageStore = new RecordingUsageStore();
        services.AddSingleton<AerolinkManager.Core.Storage.IUsageStore>(usageStore);
        services.AddSingleton(usageStore);

        services.AddSingleton<ClaudeManager.Gateway.Logging.SecretRegistry>(sp =>
        {
            var store = sp.GetRequiredService<AerolinkManager.Core.Configuration.JsonFileStore>();
            var protector = sp.GetRequiredService<AerolinkManager.Core.Security.ISecretProtector>();
            var config = store.LoadConfig();
            var registry = new ClaudeManager.Gateway.Logging.SecretRegistry();
            var stored = config.Gateway.LocalAuthTokenEncrypted;
            if (!string.IsNullOrEmpty(stored))
            {
                registry.RegisterSecret(stored);
                try { registry.RegisterSecret(protector.Unprotect(stored)); } catch { }
            }

            return registry;
        });
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseMiddleware<ClaudeManager.Gateway.Logging.SanitizationMiddleware>();
        app.UseMiddleware<ClaudeManager.Gateway.Security.LocalAuthMiddleware>();

        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
            ClaudeManager.Gateway.Endpoints.GatewayEndpoints.MapClaudeEndpoints(endpoints);
        });
    }
}
