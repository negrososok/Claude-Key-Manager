using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace AerolinkManager.Tests.Gateway;

/// <summary>
/// A scripted upstream double that can return a REAL incremental SSE stream (backed by
/// a <see cref="Pipe"/> so the gateway reads bytes as they are produced, not buffered).
/// Supports per-attempt responses (e.g. attempt 1 = 503, attempt 2 = streaming 200),
/// per-chunk delays, an injected mid-stream failure, and observing cancellation.
/// </summary>
public sealed class StreamingMockHandler : HttpMessageHandler
{
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public bool CancellationObserved { get; private set; }

    /// <summary>1-based attempt index → the response to produce for that attempt.</summary>
    public Func<int, MockUpstreamResponse> ResponseForAttempt { get; set; } =
        _ => MockUpstreamResponse.Stream(["data: {\"type\":\"message_stop\"}\n\n"]);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _requestCount);
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var spec = ResponseForAttempt(attempt);

        if (!spec.IsStreaming)
        {
            var error = new HttpResponseMessage(spec.StatusCode);
            error.Content = new StringContent(spec.Body ?? string.Empty, Encoding.UTF8, "application/json");
            foreach (var header in spec.Headers)
            {
                error.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return error;
        }

        var pipe = new Pipe();
        _ = Task.Run(() => ProduceAsync(pipe.Writer, spec, cancellationToken), CancellationToken.None);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(pipe.Reader.AsStream())
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private async Task ProduceAsync(PipeWriter writer, MockUpstreamResponse spec, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < spec.Chunks!.Count; i++)
            {
                if (spec.PerChunkDelay > TimeSpan.Zero)
                {
                    await Task.Delay(spec.PerChunkDelay, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(spec.Chunks[i], cancellationToken);
                var flush = await writer.FlushAsync(cancellationToken);

                // The reader (gateway) completed — i.e. the client disconnected/cancelled
                // and the gateway stopped consuming. Treat as observed cancellation.
                if (flush.IsCompleted)
                {
                    CancellationObserved = true;
                    await writer.CompleteAsync();
                    return;
                }

                if (spec.ThrowAfterChunk is { } n && i + 1 == n)
                {
                    // Let the consumer drain the already-flushed chunks before failing, so
                    // the gateway forwards them and only then sees the mid-stream error.
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                    throw new IOException($"Injected mid-stream failure after chunk {n}.");
                }
            }

            await writer.CompleteAsync();
        }
        catch (OperationCanceledException)
        {
            CancellationObserved = true;
            await writer.CompleteAsync(new IOException("Upstream stream cancelled."));
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex);
        }
    }
}

public sealed class MockUpstreamResponse
{
    public bool IsStreaming { get; private init; }
    public HttpStatusCode StatusCode { get; private init; } = HttpStatusCode.OK;
    public string? Body { get; private init; }
    public IReadOnlyList<byte[]>? Chunks { get; private init; }
    public TimeSpan PerChunkDelay { get; private init; }
    public int? ThrowAfterChunk { get; private init; }
    public Dictionary<string, string> Headers { get; } = new();

    public static MockUpstreamResponse Stream(IEnumerable<string> chunks, TimeSpan? perChunkDelay = null, int? throwAfterChunk = null) => new()
    {
        IsStreaming = true,
        Chunks = chunks.Select(c => Encoding.UTF8.GetBytes(c)).ToList(),
        PerChunkDelay = perChunkDelay ?? TimeSpan.Zero,
        ThrowAfterChunk = throwAfterChunk
    };

    public static MockUpstreamResponse Error(HttpStatusCode statusCode, string? body = null) => new()
    {
        IsStreaming = false,
        StatusCode = statusCode,
        Body = body
    };
}
