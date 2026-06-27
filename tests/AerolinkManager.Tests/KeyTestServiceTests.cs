using System.Net;
using AerolinkManager.Core.Managed;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class KeyTestServiceTests
{
    [TestMethod]
    public async Task TestAsync_SendsKeyInHeaderAndNeverReturnsItInFailure()
    {
        const string secret = "test-secret-value";
        var handler = new StubHandler(request =>
        {
            Assert.AreEqual(secret, request.Headers.GetValues("x-api-key").Single());
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent($"quota for {secret}")
            };
        });

        var result = await new KeyTestService(new HttpClient(handler)).TestAsync("https://example.test/", secret);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.Message.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(result.Message, "[REDACTED]");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
