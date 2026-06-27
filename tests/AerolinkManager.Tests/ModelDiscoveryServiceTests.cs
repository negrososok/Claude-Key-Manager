using System.Net;
using System.Text;
using AerolinkManager.Core.Managed;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class ModelDiscoveryServiceTests
{
    [TestMethod]
    public async Task Discover_ParsesModelsAndSendsAnthropicHeaders()
    {
        var handler = new StubHandler(request =>
        {
            Assert.AreEqual("secret", request.Headers.GetValues("x-api-key").Single());
            Assert.AreEqual("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"model-a","display_name":"Model A"},{"id":"model-b"}]}""", Encoding.UTF8, "application/json") };
        });

        var result = await new ModelDiscoveryService(new HttpClient(handler)).DiscoverAsync("https://provider.example/", "secret");

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("Model A", result[0].DisplayName);
        Assert.AreEqual("model-b", result[1].DisplayName);
    }

    [TestMethod]
    public async Task Discover_RedactsSecretFromProviderError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("rejected secret") });

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new ModelDiscoveryService(new HttpClient(handler)).DiscoverAsync("https://provider.example/", "secret"));

        Assert.IsFalse(error.Message.Contains("secret", StringComparison.Ordinal));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
