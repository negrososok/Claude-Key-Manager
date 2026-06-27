using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace AerolinkManager.Tests.Gateway;

public class FakeUpstreamServer : IDisposable
{
    private IHost _host = null!;
    public int Port { get; private set; }
    public string LastReceivedAuthorization { get; private set; } = string.Empty;
    public string LastReceivedBody { get; private set; } = string.Empty;
    public int RequestCount { get; private set; }

    // Control how the fake server responds
    public int ResponseStatusCode { get; set; } = 200;
    public string ResponseBody { get; set; } = "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Fake response\"}],\"model\":\"claude-3-5-sonnet\",\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":20}}";
    public Dictionary<string, string> ResponseHeaders { get; set; } = new();

    public async Task StartAsync()
    {
        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, 0);
                });
                webBuilder.ConfigureServices(services => services.AddRouting());
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/v1/messages", async context =>
                        {
                            RequestCount++;
                            if (context.Request.Headers.TryGetValue("x-api-key", out var key))
                            {
                                LastReceivedAuthorization = key.ToString();
                            }

                            using var reader = new StreamReader(context.Request.Body);
                            LastReceivedBody = await reader.ReadToEndAsync();

                            context.Response.StatusCode = ResponseStatusCode;
                            foreach (var h in ResponseHeaders)
                            {
                                context.Response.Headers[h.Key] = h.Value;
                            }
                            await context.Response.WriteAsync(ResponseBody);
                        });
                    });
                });
            });

        _host = await hostBuilder.StartAsync();

        var server = _host.Services.GetService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer)) as Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature;
        var address = server?.Addresses.FirstOrDefault();
        if (address != null)
        {
            var uri = new Uri(address);
            Port = uri.Port;
        }
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
        }
    }

    public void Dispose()
    {
        _host?.Dispose();
    }
}
