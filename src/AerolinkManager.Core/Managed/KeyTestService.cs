using System.Net.Http.Headers;
using System.Text;

namespace AerolinkManager.Core.Managed;

public sealed record KeyTestResult(bool Success, string Message);

public sealed class KeyTestService
{
    private readonly HttpClient _httpClient;

    public KeyTestService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<KeyTestResult> TestAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default, string providerName = "Provider")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(EnsureSlash(baseUrl)), "v1/messages"));
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent("""{"model":"claude-3-5-haiku-latest","max_tokens":1,"messages":[{"role":"user","content":"Hi"}]}""", Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return new KeyTestResult(true, $"Key is accepted by {providerName}.");
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var safeDetail = detail.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
        if (safeDetail.Length > 200)
        {
            safeDetail = safeDetail[..200];
        }
        return new KeyTestResult(false, $"{providerName} returned {(int)response.StatusCode}: {safeDetail}");
    }

    private static string EnsureSlash(string value) => value.EndsWith('/') ? value : value + "/";
}
