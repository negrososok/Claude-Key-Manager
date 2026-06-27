using System.Text.Json;

namespace AerolinkManager.Core.Managed;

public sealed record DiscoveredModel(string Value, string DisplayName);

public sealed class ModelDiscoveryService
{
    private readonly HttpClient _client;
    public ModelDiscoveryService(HttpClient client) => _client = client;

    public async Task<IReadOnlyList<DiscoveredModel>> DiscoverAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), "v1/models"));
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var safe = body.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
            throw new InvalidOperationException($"Model discovery returned {(int)response.StatusCode}: {safe[..Math.Min(200, safe.Length)]}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Model discovery response does not contain a data array.");
        return data.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
            .Select(item =>
            {
                var id = item.GetProperty("id").GetString()!;
                var display = item.TryGetProperty("display_name", out var name) ? name.GetString() : null;
                return new DiscoveredModel(id, string.IsNullOrWhiteSpace(display) ? id : display);
            })
            .DistinctBy(model => model.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
