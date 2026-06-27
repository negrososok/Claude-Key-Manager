using System.Text;
using System.Text.Json.Nodes;

namespace AerolinkManager.Core.Usage;

/// <summary>
/// Incremental, fault-tolerant parser for Anthropic Messages SSE streams. It is fed
/// the same bytes the gateway forwards to the client (a copy — it never mutates the
/// stream) and accumulates token usage as events arrive. Frames may be split across
/// <see cref="Feed"/> calls; unknown event types and malformed JSON are ignored and
/// never throw. Input/cache tokens come from <c>message_start</c>; the cumulative
/// output-token count is taken from the latest <c>message_delta</c>.
/// </summary>
public sealed class SseUsageParser
{
    private const int MaxBufferBytes = 1024 * 1024;
    private readonly List<byte> _pending = new();

    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CacheCreationInputTokens { get; private set; }
    public long CacheReadInputTokens { get; private set; }
    public long ServerToolUse { get; private set; }
    public string? Model { get; private set; }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            _pending.Add(b);
        }

        int newline;
        while ((newline = _pending.IndexOf((byte)'\n')) >= 0)
        {
            var line = _pending.GetRange(0, newline);
            _pending.RemoveRange(0, newline + 1);
            ProcessLine(line);
        }

        // Bound memory if a single line never terminates (malformed upstream).
        if (_pending.Count > MaxBufferBytes)
        {
            _pending.Clear();
        }
    }

    private void ProcessLine(List<byte> lineBytes)
    {
        if (lineBytes.Count == 0)
        {
            return;
        }

        var line = Encoding.UTF8.GetString(lineBytes.ToArray()).TrimEnd('\r');
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }

        var payload = line["data:".Length..].Trim();
        if (payload.Length == 0 || payload == "[DONE]")
        {
            return;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch
        {
            return;
        }

        if (node is null)
        {
            return;
        }

        var type = TryGetString(node, "type");
        switch (type)
        {
            case "message_start":
            {
                var usage = node["message"]?["usage"];
                if (usage is not null)
                {
                    InputTokens = ReadLong(usage, "input_tokens") ?? InputTokens;
                    CacheCreationInputTokens = ReadLong(usage, "cache_creation_input_tokens") ?? CacheCreationInputTokens;
                    CacheReadInputTokens = ReadLong(usage, "cache_read_input_tokens") ?? CacheReadInputTokens;
                    var initialOutput = ReadLong(usage, "output_tokens");
                    if (initialOutput is not null)
                    {
                        OutputTokens = initialOutput.Value;
                    }
                }

                Model = TryGetString(node["message"], "model") ?? Model;
                break;
            }

            case "message_delta":
            {
                var usage = node["usage"];
                if (usage is not null)
                {
                    var output = ReadLong(usage, "output_tokens");
                    if (output is not null)
                    {
                        OutputTokens = output.Value;
                    }

                    ServerToolUse = ReadLong(usage, "server_tool_use") ?? ServerToolUse;
                }

                break;
            }
        }
    }

    private static long? ReadLong(JsonNode node, string property)
    {
        try
        {
            return node[property]?.GetValue<long>();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonNode? node, string property)
    {
        try
        {
            return node?[property]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}
