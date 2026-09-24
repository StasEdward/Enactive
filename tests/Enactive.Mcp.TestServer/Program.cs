using System.Text.Json;
using System.Text;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var writing = new SemaphoreSlim(1, 1);

async Task Send(string reply)
{
    await writing.WaitAsync();
    try
    {
        await Console.Out.WriteLineAsync(reply);
        await Console.Out.FlushAsync();
    }
    finally { writing.Release(); }
}

while (await Console.In.ReadLineAsync() is { } line)
{
    using var doc = JsonDocument.Parse(line);
    var root = doc.RootElement;
    if (!root.TryGetProperty("id", out var id)) continue;
    var method = root.GetProperty("method").GetString();

    if (method == "tools/call" && root.GetProperty("params").GetProperty("name").GetString() == "slow")
    {
        // "slow" is the call whose answer never arrives in time. It RECORDS each call the moment it is
        // received - the side effect a real operation would have - when given a file to record in,
        // and answers in the background, so a second call sent while the first is still pending is
        // read and recorded too. It used to sleep in this loop, and a retry would have gone unread
        // until long after any test had stopped looking (PROVIDERS_AGENTS_TOOLS_TESTS_REVIEW #5).
        if (root.GetProperty("params").TryGetProperty("arguments", out var arguments)
            && arguments.TryGetProperty("value", out var record) && record.GetString() is { Length: > 0 } path)
            File.AppendAllText(path, "tools/call slow\n");

        var pending = root.Clone();
        _ = Task.Run(async () =>
        {
            await Task.Delay(10000);
            await Send(Enactive.Mcp.TestServer.Responses.For(pending));
        });
        continue;
    }

    await Send(Enactive.Mcp.TestServer.Responses.For(root));
}

namespace Enactive.Mcp.TestServer
{
    public static class Responses
    {
        public static string For(JsonElement request)
        {
            var id = request.GetProperty("id").Clone();
            var method = request.GetProperty("method").GetString();
            object? result = method switch
            {
                "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "enactive-test", version = "1" } },
                "ping" => new { },
                "tools/list" => List(request),
                "tools/call" => Call(request.GetProperty("params")),
                _ => null
            };
            return result is null
                ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unknown method" } })
                : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        }
        private static object List(JsonElement request)
        {
            var second = request.TryGetProperty("params", out var p) && p.TryGetProperty("cursor", out _);
            var names = second ? new[] { "slow", "pid" } : new[] { "echo", "fail" };
            return new { tools = names.Select(name => new { name, description = name, inputSchema = new { type = "object", properties = new { value = new { type = "string" } } } }).ToArray(), nextCursor = second ? null : "page2" };
        }
        private static object Call(JsonElement p)
        {
            var name = p.GetProperty("name").GetString();
            var text = name switch
            {
                "pid" => Environment.ProcessId.ToString(),
                "fail" => "server rejected the operation",
                _ => p.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}"
            };
            return new { content = new[] { new { type = "text", text } }, isError = name == "fail" };
        }
    }
}
