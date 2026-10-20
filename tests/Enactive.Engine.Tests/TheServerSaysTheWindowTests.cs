namespace Enactive.Engine.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Providers;
using Enactive.Settings;
using Xunit;

/// <summary>
/// A provider nobody typed a context window for asks its server for the window of the model a request goes to
/// (ServerWindow).
///
/// <para>The window was only what a person typed into the provider. The owner's Anthropic provider had none, so with
/// claude-haiku-5-5 reviewing, the reviewer's automatic evidence budget fell back to 6,000 characters - the setting that
/// made run 2eedd5 reject a correct step - and one typed number would have been shared by every model the provider
/// serves.</para>
///
/// <para>The answers are kept for the process, so each test names a provider of its own.</para>
/// </summary>
public sealed class TheServerSaysTheWindowTests
{
    // ── fakes ───────────────────────────────────────────────────────────────

    /// <summary>A server: answers by path, counts what it was asked, and never reaches the network.</summary>
    private sealed class Server(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Asked { get; } = new();

        public Server(Func<HttpRequestMessage, HttpResponseMessage> answer) : this(r => Task.FromResult(answer(r))) { }

        /// <summary>A server that takes its time over each answer.</summary>
        public static Server Slow(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) => new(answer);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Enqueue(request);
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Catalog(HttpRequestMessage request, params (string Model, int Window)[] models)
    {
        foreach (var (model, window) in models)
            if (request.RequestUri!.AbsolutePath == "/v1/models/" + model)
                return Json($$"""{"type":"model","id":"{{model}}","display_name":"x","max_input_tokens":{{window}},"max_tokens":64000}""");
        return Json("""{"type":"error","error":{"type":"not_found_error","message":"model not found"}}""", HttpStatusCode.NotFound);
    }

    private sealed class Recorder : ILogSink
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public void Log(LogEntry entry) => Entries.Enqueue(entry);
    }

    /// <summary>A provider id nobody else in the process uses: what the server said is kept per provider and model.</summary>
    private static string Unique(string name) => $"{name}-{Guid.NewGuid():N}";

    private static ProviderDescriptor Anthropic(string id, int? window = null, IReadOnlyDictionary<string, string>? headers = null)
        => new(id, id, ProviderKind.Anthropic, "https://api.anthropic.test", "sk-ant-test", [], headers, ContextWindowTokens: window);

    private static ProviderDescriptor LlamaCpp(string id, string baseUrl = "http://llama.test:8080/v1")
        => new(id, id, ProviderKind.OpenAiCompatible, baseUrl, null, []);

    private static ChatRequest To(string model) => new(model, [ChatMessage.User("hello")]);

    // ── Anthropic ───────────────────────────────────────────────────────────

    /// <summary>
    /// No typed window: the Models API's max_input_tokens for the model the request names - asked with the key, the API
    /// version and the provider's own headers, as the model list is.
    /// </summary>
    [Fact]
    public void An_anthropic_provider_with_no_window_answers_what_the_model_catalog_says()
    {
        var server = new Server(r => Catalog(r, ("claude-haiku-5-5", 1_000_000)));
        using var http = new HttpClient(server);
        var provider = new AnthropicProvider(http, Anthropic(Unique("anthropic"), headers: new Dictionary<string, string> { ["x-gateway"] = "g1" }));

        Assert.Equal(1_000_000, provider.ContextWindow(To("claude-haiku-5-5")));

        var asked = Assert.Single(server.Asked);
        Assert.Equal(HttpMethod.Get, asked.Method);
        Assert.Equal("https://api.anthropic.test/v1/models/claude-haiku-5-5", asked.RequestUri!.ToString());
        Assert.Equal("sk-ant-test", asked.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", asked.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("g1", asked.Headers.GetValues("x-gateway").Single());
    }

    /// <summary>A number a person typed is the window, and the server is not asked.</summary>
    [Fact]
    public void A_typed_window_wins_and_nothing_is_asked()
    {
        var server = new Server(r => Catalog(r, ("claude-haiku-5-5", 1_000_000)));
        using var http = new HttpClient(server);
        var provider = new AnthropicProvider(http, Anthropic(Unique("typed"), window: 150_000));

        Assert.Equal(150_000, provider.ContextWindow(To("claude-haiku-5-5")));
        Assert.Empty(server.Asked);
    }

    /// <summary>
    /// A server that fails, refuses, or says nothing about a window leaves the answer null - what it was before - and is
    /// not asked again on the next request.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"type":"error"}""")]
    [InlineData(HttpStatusCode.NotFound, """{"type":"error"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"type":"error"}""")]
    [InlineData(HttpStatusCode.OK, """{"type":"model","id":"m","display_name":"x"}""")]
    [InlineData(HttpStatusCode.OK, "<html>a gateway's own page</html>")]
    public void A_server_that_does_not_say_leaves_the_window_unknown(HttpStatusCode status, string body)
    {
        var server = new Server(_ => Json(body, status));
        using var http = new HttpClient(server);
        var provider = new AnthropicProvider(http, Anthropic(Unique("silent")));

        Assert.Null(provider.ContextWindow(To("claude-haiku-5-5")));
        Assert.Null(provider.ContextWindow(To("claude-haiku-5-5")));
        Assert.Single(server.Asked);
    }

    /// <summary>A server that cannot be reached at all is null too, and costs no exception to the caller.</summary>
    [Fact]
    public void An_unreachable_server_leaves_the_window_unknown()
    {
        Func<HttpRequestMessage, HttpResponseMessage> refused = _ => throw new HttpRequestException("connection refused");
        var server = new Server(refused);
        using var http = new HttpClient(server);

        Assert.Null(new AnthropicProvider(http, Anthropic(Unique("down"))).ContextWindow(To("claude-haiku-5-5")));
    }

    /// <summary>
    /// Asked once per model for the process, not on every request: the factory makes a new provider for every use, and
    /// ContextWindow is asked several times a turn.
    /// </summary>
    [Fact]
    public void The_server_is_asked_once_per_model_across_many_requests_and_providers()
    {
        var server = new Server(r => Catalog(r, ("claude-haiku-5-5", 1_000_000)));
        using var http = new HttpClient(server);
        var descriptor = Anthropic(Unique("once"));
        var factory = new ChatProviderFactory([descriptor], http, new Recorder());

        for (var i = 0; i < 40; i++)
            Assert.Equal(1_000_000, factory.Create(descriptor.Id).ContextWindow(To("claude-haiku-5-5")));

        Assert.Single(server.Asked);
    }

    /// <summary>One provider serving two models gives each its own window: the window is the model's, not the provider's.</summary>
    [Fact]
    public void Two_models_of_one_provider_get_their_own_windows()
    {
        var server = new Server(r => Catalog(r, ("claude-haiku-5-5", 1_000_000), ("claude-sonnet-5", 200_000)));
        using var http = new HttpClient(server);
        var provider = new AnthropicProvider(http, Anthropic(Unique("two")));

        Assert.Equal(1_000_000, provider.ContextWindow(To("claude-haiku-5-5")));
        Assert.Equal(200_000, provider.ContextWindow(To("claude-sonnet-5")));
        Assert.Equal(1_000_000, provider.ContextWindow(To("claude-haiku-5-5")));
        Assert.Equal(2, server.Asked.Count);
    }

    /// <summary>The log says the window it found once per provider and model, and where it came from.</summary>
    [Fact]
    public void The_found_window_is_said_once_in_the_log()
    {
        var server = new Server(r => Catalog(r, ("claude-haiku-5-5", 1_000_000)));
        using var http = new HttpClient(server);
        var log = new Recorder();
        var id = Unique("logged");
        var provider = new AnthropicProvider(http, Anthropic(id), log);

        for (var i = 0; i < 5; i++)
            provider.ContextWindow(To("claude-haiku-5-5"));

        var said = Assert.Single(log.Entries, e => e.Message.Contains("window 1,000,000 tokens"));
        Assert.Equal($"{id}/claude-haiku-5-5: window 1,000,000 tokens, from the server's model catalog.", said.Message);
        Assert.Equal(LogLevel.Info, said.Level);
    }

    // ── waiting for the first answer ────────────────────────────────────────

    /// <summary>
    /// The first question is waited for, briefly: a model used only to review is first asked by the run's first review,
    /// and an answer of null there would give that review the fixed 6,000 characters however quickly the server said.
    /// </summary>
    [Fact]
    public void The_first_request_gets_the_window_of_a_server_that_answers_quickly()
    {
        var server = Server.Slow(async r =>
        {
            await Task.Delay(300);
            return Catalog(r, ("claude-haiku-5-5", 1_000_000));
        });
        using var http = new HttpClient(server);

        Assert.Equal(1_000_000, new AnthropicProvider(http, Anthropic(Unique("quick"))).ContextWindow(To("claude-haiku-5-5")));
    }

    /// <summary>
    /// A server slower than the wait costs one wait, not a run: the first request is answered null when the wait is up,
    /// the question goes on, and a later request reads its answer without waiting again.
    /// </summary>
    [Fact]
    public async Task A_slow_server_costs_one_wait_and_its_answer_reaches_later_requests()
    {
        var release = new TaskCompletionSource();
        var server = Server.Slow(async r =>
        {
            await release.Task;
            return Catalog(r, ("claude-haiku-5-5", 1_000_000));
        });
        using var http = new HttpClient(server);
        var descriptor = Anthropic(Unique("slow"));
        int? Ask(TimeSpan wait) => ServerWindow.For(http, descriptor, "claude-haiku-5-5", null, "the catalog",
            ModelFetch.AnthropicWindowAsync, wait);

        var waited = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(Ask(TimeSpan.FromMilliseconds(200)));
        Assert.InRange(waited.ElapsedMilliseconds, 150, 5_000);
        // The wait is spent: a second request does not wait again, however long it would be allowed to.
        waited.Restart();
        Assert.Null(Ask(TimeSpan.FromSeconds(30)));
        Assert.True(waited.ElapsedMilliseconds < 1_000, $"{waited.ElapsedMilliseconds} ms: the second request waited again");

        release.SetResult();
        for (var i = 0; i < 400 && ServerWindow.Known(descriptor.Id, descriptor.Kind, descriptor.BaseUrl, "claude-haiku-5-5") is null; i++)
            await Task.Delay(20);
        Assert.Equal(1_000_000, Ask(TimeSpan.Zero));
        Assert.Single(server.Asked);
    }

    /// <summary>
    /// A server that gave no answer at all - down, or a 5xx - is asked again after a while, unwaited, so a server that was
    /// down when the first run asked does not leave the window unknown until the process restarts. One that answered "not
    /// here" is not asked again: that is the server's answer and will not change. A refused key is not "not here": it is
    /// put right in the settings without a restart.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_server_that_could_not_answer_is_asked_again_and_one_that_said_no_is_not(HttpStatusCode first)
    {
        // The first question fails, every later one is answered - by count, not by a flag flipped while a question may be
        // under way: on a loaded machine the first question can outlast its wait.
        var answered = 0;
        var server = new Server(r => Interlocked.Increment(ref answered) == 1
            ? Json("""{"type":"error"}""", first)
            : Catalog(r, ("claude-haiku-5-5", 1_000_000)));
        using var http = new HttpClient(server);
        var descriptor = Anthropic(Unique("again"));
        int? Ask() => ServerWindow.For(http, descriptor, "claude-haiku-5-5", null, "the catalog",
            ModelFetch.AnthropicWindowAsync, askAgainAfter: TimeSpan.Zero);

        Assert.Null(Ask());
        // Asked again, unwaited, once the failed question is over; the answer reaches a later request.
        int? window = null;
        for (var i = 0; i < 400 && (window = Ask()) is null; i++)
            await Task.Delay(25);
        Assert.Equal(1_000_000, window);
        Assert.Equal(2, server.Asked.Count);

        var refusing = new Server(_ => Json("""{"type":"error"}""", HttpStatusCode.NotFound));
        using var http2 = new HttpClient(refusing);
        var other = Anthropic(Unique("refused"));
        for (var i = 0; i < 3; i++)
            Assert.Null(ServerWindow.For(http2, other, "claude-haiku-5-5", null, "the catalog",
                ModelFetch.AnthropicWindowAsync, askAgainAfter: TimeSpan.Zero));
        Assert.Single(refusing.Asked);
    }

    /// <summary>
    /// Asked from a thread with a dispatcher - the log window's analysis starts on the UI thread - the first request still
    /// gets the window: the question does not need the waiting thread to finish.
    /// </summary>
    [Fact]
    public void The_first_request_from_a_thread_with_a_dispatcher_gets_the_window()
    {
        var server = Server.Slow(async r =>
        {
            await Task.Delay(50);
            return Catalog(r, ("claude-haiku-5-5", 1_000_000));
        });
        using var http = new HttpClient(server);
        var provider = new AnthropicProvider(http, Anthropic(Unique("dispatcher")));
        int? window = null;

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new OneThreadContext());
            window = provider.ContextWindow(To("claude-haiku-5-5"));
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(1_000_000, window);
    }

    /// <summary>A UI thread, near enough: work posted to it waits for a thread that never comes back to run it.</summary>
    private sealed class OneThreadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { /* the thread is blocked: never run */ }
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    // ── llama.cpp and the other OpenAI-compatible servers ───────────────────

    /// <summary>
    /// A llama.cpp server says the context one request gets - its slot's n_ctx in /props, which is the context it was
    /// started with split between its --parallel slots - asked at the server's root, for the model named.
    /// </summary>
    [Fact]
    public void A_llama_cpp_server_says_the_context_of_one_slot()
    {
        var server = new Server(r => r.RequestUri!.AbsolutePath == "/props"
            ? Json("""{"default_generation_settings":{"id":0,"n_ctx":8192,"params":{}},"total_slots":4,"model_path":"q.gguf"}""")
            : Json("{}", HttpStatusCode.NotFound));
        using var http = new HttpClient(server);
        var provider = new OpenAiCompatibleProvider(http, LlamaCpp(Unique("llama")));

        Assert.Equal(8_192, provider.ContextWindow(To("qwen3.6-35b-a3b")));

        var asked = Assert.Single(server.Asked);
        Assert.Equal("http://llama.test:8080/props?model=qwen3.6-35b-a3b", asked.RequestUri!.ToString());
    }

    /// <summary>A server that reports the whole context beside its slot count is read as the same split: what a request gets.</summary>
    [Fact]
    public void A_total_context_beside_its_slots_is_divided_between_them()
    {
        var server = new Server(_ => Json("""{"n_ctx":32768,"total_slots":4}"""));
        using var http = new HttpClient(server);

        Assert.Equal(8_192, new OpenAiCompatibleProvider(http, LlamaCpp(Unique("total"))).ContextWindow(To("m")));
    }

    /// <summary>
    /// Any other OpenAI-compatible server has no /props, or one that says nothing of a window: unknown, as before. The
    /// model's trained context (n_ctx_train in /v1/models) is not what a request gets, and is not taken.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"message":"Not found"}}""")]
    [InlineData(HttpStatusCode.OK, """{"object":"list","data":[{"id":"m","meta":{"n_ctx_train":131072}}]}""")]
    [InlineData(HttpStatusCode.OK, "<!doctype html><title>app</title>")]
    public void Another_openai_compatible_server_leaves_the_window_unknown(HttpStatusCode status, string body)
    {
        var server = new Server(_ => Json(body, status));
        using var http = new HttpClient(server);

        Assert.Null(new OpenAiCompatibleProvider(http, LlamaCpp(Unique("other"), "https://api.other.test/v1")).ContextWindow(To("m")));
    }

    // ── Ollama ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Ollama runs a request at the num_ctx it is sent, not at the most its model could take, so that is the window -
    /// and with none sent, or typed, the server is not asked: /api/show's context_length is the model's maximum, which a
    /// request does not get.
    /// </summary>
    [Fact]
    public void Ollama_s_window_is_the_num_ctx_it_is_sent_and_its_server_is_not_asked()
    {
        var server = new Server(_ => Json("""{"model_info":{"qwen3.context_length":131072}}"""));
        using var http = new HttpClient(server);
        var provider = new OllamaNativeProvider(http,
            new(Unique("ollama"), "ollama", ProviderKind.OllamaNative, "http://ollama.test:11434", null, []));

        Assert.Equal(16_384, provider.ContextWindow(To("qwen3") with { NumCtx = 16_384 }));
        Assert.Null(provider.ContextWindow(To("qwen3")));
        Assert.Empty(server.Asked);
    }

    // ── what it is for ──────────────────────────────────────────────────────

    /// <summary>
    /// End to end with the review's own budget (ReviewEvidence): a review sent to an Anthropic provider nobody typed a
    /// window for is given the evidence of the window the server states - run 2eedd5's reviewer gets the ceiling, not 6,000.
    /// </summary>
    [Fact]
    public void A_review_on_an_anthropic_provider_with_no_window_is_given_the_evidence_of_the_server_s_window()
    {
        var server = new Server(r => Catalog(r, ("claude-haiku-5-5", 1_000_000)));
        using var http = new HttpClient(server);
        var descriptor = Anthropic(Unique("review"));
        var reviewer = new ChatProviderFactory([descriptor], http, new Recorder()).Create(descriptor.Id);
        var review = new ChatRequest("claude-haiku-5-5", [], OutputTokenLimit: StepVerdictReview.AnswerTokens);

        var budget = ReviewEvidence.For(null, reviewer, review, promptChars: 9_600);

        Assert.Equal(new ReviewEvidence.Budget(ReviewEvidence.Ceiling, true, 1_000_000), budget);
        Assert.Contains("from the review model's 1,000,000-token window", budget.Describe());
    }

    /// <summary>
    /// The settings' hint for a blank evidence budget says the window the reviewer's server stated, once a run has asked
    /// it, where it used to say the provider declares none - and it reads, never asks.
    /// </summary>
    [Fact]
    public void The_settings_hint_says_the_window_the_server_stated()
    {
        var server = new Server(r => Catalog(r, ("claude-haiku-5-5", 1_000_000)));
        using var http = new HttpClient(server);
        var descriptor = Anthropic(Unique("hint"));
        var settings = new AppSettings();
        settings.Providers.Add(new ProviderConfig { Id = descriptor.Id, Kind = ProviderKind.Anthropic, BaseUrl = descriptor.BaseUrl, Models = ["claude-haiku-5-5"] });
        var folder = Path.Combine(Path.GetTempPath(), "enactive-server-window", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var vm = new SettingsViewModel(settings, _ => { }, NoStartupEntry.Instance, editsFilesHere: true,
                writableRoots: new WritableRoots(Path.Combine(folder, "writable-roots.json")), knownWorkspaces: () => []);
            vm.Review = $"{descriptor.Id}/claude-haiku-5-5";

            Assert.Equal($"auto - 6,000 characters: {descriptor.Id} declares no context window for claude-haiku-5-5", vm.EvidenceBudgetAuto);
            Assert.Empty(server.Asked);

            new AnthropicProvider(http, descriptor).ContextWindow(To("claude-haiku-5-5"));

            Assert.Equal($"auto - up to 72,000 characters, from {descriptor.Id}/claude-haiku-5-5's 1,000,000-token window, as its server states it",
                vm.EvidenceBudgetAuto);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* a temp folder */ }
        }
    }
}
