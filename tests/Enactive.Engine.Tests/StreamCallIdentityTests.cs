namespace Enactive.Engine.Tests;

using System.Net;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

public sealed class StreamCallIdentityTests
{
    [Fact]
    public async Task Reused_wire_index_keeps_id_addressed_fragments_separate()
    {
        var calls = await Read("""
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"one","arguments":"{\"a\":"}},{"index":0,"id":"b","function":{"name":"two","arguments":"{\"b\":"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"arguments":"1}"}},{"index":0,"id":"b","function":{"arguments":"2}"}}]}}]}

            data: [DONE]

            """, false);
        Assert.Equal(new[] { 0, 1, 0, 1 }, calls.Select(c => c.Index));
        Assert.Equal("""{"a":1}""", string.Concat(calls.Where(c => c.Index == 0).Select(c => c.ArgumentsJson)));
        Assert.Equal("""{"b":2}""", string.Concat(calls.Where(c => c.Index == 1).Select(c => c.ArgumentsJson)));
    }

    [Fact]
    public async Task An_idless_fragment_after_reused_index_is_refused()
        => await Assert.ThrowsAsync<InvalidDataException>(() => Read("""
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"one","arguments":"{"}},{"index":0,"id":"b","function":{"name":"two","arguments":"{"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"}"}}]}}]}

            data: [DONE]

            """, false));

    [Fact]
    public void A_late_id_keeps_its_anonymous_index_and_index_only_streams_still_work()
    {
        var identity = new StreamCallIdentity();
        Assert.Equal(0, identity.Resolve(0, null, "one", "{"));
        Assert.Equal(0, identity.Resolve(0, "a", null, "\"x\":"));
        Assert.Equal(0, identity.Resolve(0, null, null, "1}"));
        Assert.Equal(1, identity.Resolve(1, null, "two", "{"));
        Assert.Equal(1, identity.Resolve(1, null, null, "}"));
    }

    [Fact]
    public async Task Ollama_complete_stringified_objects_are_decoded_and_remain_separate()
    {
        var calls = await Read("""
            {"message":{"tool_calls":[{"function":{"name":"one","arguments":"{\"x\":1}"}}]}}
            {"message":{"tool_calls":[{"function":{"name":"one","arguments":"{\"x\":2}"}}]}}
            {"done":true}
            """, true);
        Assert.Equal(new[] { 0, 1 }, calls.Select(c => c.Index));
        Assert.Equal(new[] { """{"x":1}""", """{"x":2}""" }, calls.Select(c => c.ArgumentsJson));
    }

    [Theory]
    [InlineData("\"{\"")]
    [InlineData("\"[]\"")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task Ollama_incomplete_or_non_object_arguments_are_never_accepted(string arguments)
        => await Assert.ThrowsAsync<InvalidDataException>(() => Read(
            "{\"message\":{\"tool_calls\":[{\"function\":{\"name\":\"one\",\"arguments\":" + arguments + "}}]}}\n{\"done\":true}\n", true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_calls_in_different_chunks_have_different_indices(bool ollama)
    {
        var body = ollama
            ? """
              {"message":{"tool_calls":[{"function":{"name":"one","arguments":{}}}]}}
              {"message":{"tool_calls":[{"function":{"name":"two","arguments":{}}}]}}
              {"done":true}
              """
            : """
              data: {"choices":[{"delta":{"tool_calls":[{"function":{"name":"one","arguments":"{}"}}]}}]}

              data: {"choices":[{"delta":{"tool_calls":[{"function":{"name":"two","arguments":"{}"}}]}}]}

              data: [DONE]

              """;
        var calls = await Read(body, ollama);
        Assert.Equal(new[] { 0, 1 }, calls.Select(c => c.Index));
        Assert.Equal(new[] { "one", "two" }, calls.Select(c => c.Name));
    }

    [Fact]
    public async Task Id_identifies_fragments_without_an_index()
    {
        var calls = await Read("""
            data: {"choices":[{"delta":{"tool_calls":[{"id":"a","function":{"name":"one","arguments":"{"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"id":"a","function":{"arguments":"}"}}]}}]}

            data: [DONE]

            """, false);
        Assert.Equal(new[] { 0, 0 }, calls.Select(c => c.Index));
        Assert.Equal("{}", string.Concat(calls.Select(c => c.ArgumentsJson)));
    }

    [Fact]
    public async Task Ambiguous_fragment_is_refused_instead_of_appended_to_call_zero()
        => await Assert.ThrowsAsync<InvalidDataException>(() => Read("""
            data: {"choices":[{"delta":{"tool_calls":[{"function":{"arguments":"}"}}]}}]}

            data: [DONE]

            """, false));

    private static async Task<List<ToolCallDelta>> Read(string body, bool ollama)
    {
        using var http = new HttpClient(new Reply(body));
        var descriptor = new ProviderDescriptor("test", "test", ProviderKind.OpenAiCompatible, "https://test.invalid", null, []);
        IChatProvider provider = ollama ? new OllamaNativeProvider(http, descriptor) : new OpenAiCompatibleProvider(http, descriptor);
        var calls = new List<ToolCallDelta>();
        await foreach (var ev in provider.StreamChatAsync(new("model", []), default))
            if (ev is ToolCallDelta call) calls.Add(call);
        return calls;
    }

    private sealed class Reply(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
