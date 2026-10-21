namespace Enactive.Engine.Tests;
using System.Globalization;
using System.Net;
using System.Text;
using Enactive.Application.Projections;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Enactive.App.Ui.ViewModels;

public sealed class ModelPerformanceTests
{
    private static readonly Guid Run = Guid.NewGuid();
    private static ModelCallMetrics InRun(ModelCallMetrics m) => m with { RunId = Run };
    private static readonly ChatRequest Request = new("test", new[] { ChatMessage.User("hello") });
    private static ProviderDescriptor Descriptor(ProviderKind kind) => new("test", "test", kind, "http://localhost:1234/v1", null, new[] { "test" });
    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
    }
    [Fact]
    public async Task Native_stream_converts_nanoseconds_and_keeps_token_counts()
    {
        using var http = new HttpClient(new Handler("""{"message":{"content":"done"},"done":true,"prompt_eval_count":100,"eval_count":20,"prompt_eval_duration":250000000,"eval_duration":2000000000,"load_duration":100000000}""" + "\n"));
        var reports = new List<ModelCallMetrics>();
        var provider = new MeteredChatProvider(new OllamaNativeProvider(http, Descriptor(ProviderKind.OllamaNative)), "native", reports.Add);
        await foreach (var _ in provider.StreamChatAsync(Request, default)) { }
        var result = Assert.Single(reports);
        Assert.Equal(0.25, result.Timings!.PromptSeconds);
        Assert.Equal(2, result.Timings.GenerationSeconds);
        Assert.Equal(0.1, result.Timings.LoadSeconds);
        Assert.Equal(20, result.CompletionTokens);
        Assert.NotNull(result.FirstDeltaSeconds);
    }
    [Fact]
    public async Task Compatible_stream_reads_timings_from_separate_usage_chunk()
    {
        const string body = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20},\"timings\":{\"prompt_ms\":250,\"predicted_ms\":2000,\"predicted_n\":20}}\n\ndata: [DONE]\n\n";
        using var http = new HttpClient(new Handler(body));
        var reports = new List<ModelCallMetrics>();
        var provider = new MeteredChatProvider(new OpenAiCompatibleProvider(http, Descriptor(ProviderKind.OpenAiCompatible)), "local", reports.Add);
        await foreach (var _ in provider.StreamChatAsync(Request, default)) { }
        var m = Assert.Single(reports);
        Assert.Equal(0.25, m.Timings!.PromptSeconds);
        Assert.Equal(2, m.Timings.GenerationSeconds);
    }
    [Fact]
    public async Task Compatible_completion_carries_server_timings()
    {
        using var http = new HttpClient(new Handler("""{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":20},"timings":{"prompt_ms":500,"predicted_ms":1000}}"""));
        var reports = new List<ModelCallMetrics>();
        var provider = new MeteredChatProvider(new OpenAiCompatibleProvider(http, Descriptor(ProviderKind.OpenAiCompatible)), "cloud", reports.Add);
        await provider.CompleteAsync(Request, default);
        var m = Assert.Single(reports);
        Assert.Null(m.FirstDeltaSeconds);
        Assert.Equal(0.5, m.Timings!.PromptSeconds);
        Assert.Equal(1, m.Timings.GenerationSeconds);
    }
    [Fact]
    public async Task Malformed_optional_timings_do_not_break_a_valid_response()
    {
        using var http = new HttpClient(new Handler("""{"choices":[{"message":{"content":"done"}}],"timings":{"prompt_ms":"unknown","predicted_ms":-1}}"""));
        var result = await new OpenAiCompatibleProvider(http, Descriptor(ProviderKind.OpenAiCompatible)).CompleteAsync(Request, default);
        Assert.Null(result.Timings!.PromptSeconds);
        Assert.Null(result.Timings.GenerationSeconds);
    }
    [Fact]
    public void Rate_is_weighted_and_unknown_timings_do_not_dilute_it()
    {
        var vm = new ModelPerformance(); vm.ShowRun(Run);
        vm.Add(InRun(new("a", "worker", 100, 100, 50, 12, 1, new(1, 10))));
        vm.Add(InRun(new("a", "worker", 100, 100, null, 3, 1, new(1, 1))));
        vm.Add(InRun(new("a", "worker", 100, 900, null, 2, null, null)));
        var row = Assert.Single(vm.Rows);
        Assert.Equal("18.2 T/s", row.Speed);
        Assert.Equal("Generation avg", row.SpeedLabel);
        Assert.Contains("decode 2/3", row.Explanation);
        Assert.Contains("50.0%", row.Cache);
        vm.Add(InRun(new("b", "worker", null, null, null, 1, null, null)));
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("—", vm.Rows[1].Speed);
        Assert.Contains("— in / — out", vm.Rows[1].Tokens);
    }
    [Fact]
    public void Request_estimate_uses_weighted_totals_and_excludes_unknown_or_invalid_samples()
    {
        var vm = new ModelPerformance(); vm.ShowRun(Run);
        vm.Add(InRun(new("cloud", "model", 100, 100, null, 10, null, null)));
        vm.Add(InRun(new("cloud", "model", 100, 100, null, 2, null, null)));
        vm.Add(InRun(new("cloud", "model", 100, null, null, 100, null, null)));
        vm.Add(InRun(new("cloud", "model", 100, 900, null, 0, null, null)));
        var row = Assert.Single(vm.Rows);
        Assert.Equal("≈ 16.7 T/s", row.Speed);
        Assert.Equal("Whole request avg", row.SpeedLabel);
        Assert.Contains("including queue, prompt processing and network time", row.Explanation);
    }
    [Fact]
    public void A_reported_zero_output_is_zero_rate_but_zero_duration_is_unknown()
    {
        var vm = new ModelPerformance(); vm.ShowRun(Run);
        vm.Add(InRun(new("cloud", "zero", 100, 0, null, 2, null, null)));
        vm.Add(InRun(new("cloud", "unknown", 100, 50, null, 0, null, null)));
        Assert.Equal("≈ 0.0 T/s", vm.Rows[0].Speed);
        Assert.Equal("—", vm.Rows[1].Speed);
    }
    /// <summary>The service's culture must not change the shared figures, including their decimal and group separators.</summary>
    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void Performance_figures_use_the_same_format_in_every_culture(string culture)
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var vm = new ModelPerformance();
            vm.ShowRun(Run);
            vm.Add(InRun(new("local", "worker", 2000, 1000, 500, 12.5, 1.25, new(2.5, 10, 0.5))));
            vm.Add(InRun(new("cloud", "worker", 100, 100, null, 12, null, null)));

            Assert.Equal("100.0 T/s", vm.Rows[0].Speed);
            Assert.Equal("Tokens  2,000 in / 1,000 out", vm.Rows[0].Tokens);
            Assert.Contains("25.0%", vm.Rows[0].Cache);
            Assert.Contains("0.50 s", vm.Rows[0].Cache);
            Assert.Contains("2.50 s", vm.Rows[0].Times);
            Assert.Contains("1.25 s", vm.Rows[0].First);
            Assert.Contains("12.50 s", vm.Rows[0].Requests);
            Assert.Equal("≈ 8.3 T/s", vm.Rows[1].Speed);
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public async Task Observer_failure_does_not_fail_the_model_call()
    {
        var provider = new MeteredChatProvider(new FakeChatProvider(Turn.Says("done")), "p", _ => throw new Exception("UI gone"));
        Assert.Equal("done", (await provider.CompleteAsync(Request, default)).Message.Content);
    }
    [Fact]
    public async Task Abandoned_stream_is_not_counted_as_a_completed_request()
    {
        var reports = new List<ModelCallMetrics>();
        var provider = new MeteredChatProvider(new FakeChatProvider(Turn.Says("hello")), "p", reports.Add);
        await foreach (var _ in provider.StreamChatAsync(Request, default)) break;
        Assert.Empty(reports);
    }

    // ── one run's figures, not the session's ──────────────────────────────

    private static ModelCallMetrics Call(Guid run, string model = "worker") => new("p", model, 100, 10, null, 1, null, null, run);

    /// <summary>
    /// The panel summed every call since the window opened - background runs, every run before (2026-10-09). It shows the
    /// run the window shows, and starts again with the next.
    /// </summary>
    [Fact]
    public void The_panel_shows_one_run_and_starts_again_with_the_next()
    {
        var vm = new ModelPerformance();
        Guid first = Guid.NewGuid(), next = Guid.NewGuid(), elsewhere = Guid.NewGuid();
        vm.ShowRun(first);
        vm.Add(Call(first));
        vm.Add(Call(first));
        vm.Add(Call(elsewhere, "background"));                         // a background run's call: not this run's
        Assert.Equal(["worker"], vm.Rows.Select(r => r.Model));
        Assert.Contains("2 calls", vm.Rows[0].Requests);

        vm.ShowRun(next);
        Assert.Empty(vm.Rows);
        vm.Add(Call(next));
        Assert.Contains("1 calls", vm.Rows.Single().Requests);
    }

    /// <summary>A call can come before the window has learnt its run from the run's first event: it is shown with its run.</summary>
    [Fact]
    public void A_call_that_came_before_its_run_was_shown_is_shown_with_it()
    {
        var vm = new ModelPerformance();
        var run = Guid.NewGuid();
        vm.Add(Call(run, "planner"));

        vm.ShowRun(run);

        Assert.Equal(["planner"], vm.Rows.Select(r => r.Model));
    }

    [Fact]
    public void A_call_made_outside_any_run_is_not_shown()
    {
        var vm = new ModelPerformance();
        var run = Guid.NewGuid();
        vm.ShowRun(run);
        vm.Add(new("p", "worker", 100, 10, null, 1, null, null));

        Assert.Empty(vm.Rows);
    }

    /// <summary>Each call says the run it was made for - the scope it was made in.</summary>
    [Fact]
    public async Task A_call_carries_the_run_it_was_made_in()
    {
        var reports = new List<ModelCallMetrics>();
        var provider = new MeteredChatProvider(new FakeChatProvider(Turn.Says("hello"), Turn.Says("again")), "p", reports.Add);
        var run = Guid.NewGuid();
        using (Enactive.Core.Diagnostics.LogScope.Begin(run, Guid.NewGuid()))
        {
            await foreach (var _ in provider.StreamChatAsync(Request, default)) { }
            await provider.CompleteAsync(Request, default);
        }

        Assert.All(reports, m => Assert.Equal(run, m.RunId));
        Assert.Equal(2, reports.Count);
    }

    /// <summary>The window shows the run its live events are from, and says the figures are the run's.</summary>
    [Fact]
    public void The_window_shows_the_live_run_s_figures()
    {
        var ui = Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui");
        var render = File.ReadAllText(Path.Combine(ui, "MainWindow.axaml.cs"));
        var at = render.IndexOf("private void RenderEvent(WorkEvent ev)", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("_vm.Performance.ShowRun(ev.RunId);", render[at..(at + 1500)], StringComparison.Ordinal);
        Assert.Contains("This run · completed requests", File.ReadAllText(Path.Combine(ui, "MainWindow.axaml")), StringComparison.Ordinal);
    }

    /// <summary>The panel opens folded to one line a model, and a click opens or folds it.</summary>
    [Fact]
    public void The_panel_starts_folded_and_a_click_toggles_it()
    {
        var vm = new ModelPerformance();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(vm.IsExpanded);

        vm.Toggle();
        Assert.True(vm.IsExpanded);
        vm.Toggle();
        Assert.False(vm.IsExpanded);
        Assert.Equal([nameof(ModelPerformance.IsExpanded), nameof(ModelPerformance.IsExpanded)], changed);

        var ui = Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui");
        Assert.Contains("_vm.Performance.Toggle()", File.ReadAllText(Path.Combine(ui, "MainWindow.axaml.cs")), StringComparison.Ordinal);
        var xaml = File.ReadAllText(Path.Combine(ui, "MainWindow.axaml"));
        Assert.Contains("x:Name=\"PerformancePanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding Performance.IsExpanded}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding !Performance.IsExpanded}\"", xaml, StringComparison.Ordinal);
    }

    // ── the cache: what was read, and what was written ───────────────────

    /// <summary>
    /// A provider that reports writes gets both shares. 2026-10-19 the panel said "Cache 0.0%" for Sonnet while nearly
    /// every input token was being written to the cache at above the input price - the read share alone hid the bill.
    /// </summary>
    [Fact]
    public void A_provider_that_reports_writes_shows_the_written_share_beside_the_read_one()
    {
        var vm = new ModelPerformance(); vm.ShowRun(Run);
        vm.Add(InRun(new("anthropic", "sonnet", 5_000, 1_000, 0, 10, null, null) { CacheCreationPromptTokens = 4_900 }));
        vm.Add(InRun(new("anthropic", "sonnet", 5_000, 1_000, 100, 10, null, null) { CacheCreationPromptTokens = 4_800 }));

        var row = Assert.Single(vm.Rows);
        Assert.Contains("Cache  read 1.0% · written 97.0%", row.Cache);
        Assert.Equal("cache 1% read · 97% written", row.CacheShort);
    }

    /// <summary>A provider that reports only reads (llama.cpp, the OpenAI-compatible ones) shows the read share, and no
    /// written share it never reported.</summary>
    [Fact]
    public void A_provider_that_reports_only_reads_shows_the_read_share_alone()
    {
        var vm = new ModelPerformance(); vm.ShowRun(Run);
        vm.Add(InRun(new("local", "gemma", 10_000, 100, 8_400, 3, null, null)));

        var row = Assert.Single(vm.Rows);
        Assert.Contains("Cache  84.0%", row.Cache);
        Assert.Equal("cache 84%", row.CacheShort);
    }

    /// <summary>One that reports nothing about its cache shows nothing on the folded line - not a confident 0%.</summary>
    [Fact]
    public void A_provider_that_reports_nothing_about_its_cache_shows_nothing_folded()
    {
        var vm = new ModelPerformance(); vm.ShowRun(Run);
        vm.Add(InRun(new("cloud", "model", 1_000, 100, null, 3, null, null)));

        var row = Assert.Single(vm.Rows);
        Assert.Contains("Cache  —", row.Cache);
        Assert.Equal("", row.CacheShort);
    }

    /// <summary>The written count reaches the panel from the provider's own answer, through the meter every call passes.</summary>
    [Fact]
    public async Task The_written_count_reaches_the_panel_from_the_provider_s_answer()
    {
        using var http = new HttpClient(new Handler("""
            {"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn",
             "usage":{"input_tokens":10,"cache_read_input_tokens":90,"cache_creation_input_tokens":900,"output_tokens":5}}
            """));
        var reports = new List<ModelCallMetrics>();
        var anthropic = new AnthropicProvider(http, new ProviderDescriptor("anthropic", "Anthropic", ProviderKind.Anthropic,
            "https://api.anthropic.com", "key", new[] { "test" }));
        await new MeteredChatProvider(anthropic, "anthropic", reports.Add).CompleteAsync(Request, default);

        var m = Assert.Single(reports);
        Assert.Equal(1_000, m.PromptTokens);
        Assert.Equal(90, m.CachedPromptTokens);
        Assert.Equal(900, m.CacheCreationPromptTokens);
    }

    /// <summary>The folded line carries the shares in the window and on the service alike.</summary>
    [Fact]
    public void The_folded_panel_shows_the_cache_shares_in_the_window_and_on_the_service()
    {
        var xaml = File.ReadAllText(Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui", "MainWindow.axaml"));
        Assert.Contains("Text=\"{Binding CacheShort}\"", xaml, StringComparison.Ordinal);

        var server = Path.Combine(TestRepository.Root, "src", "Enactive.Server", "Components", "Runs");
        Assert.Contains("@row.CacheShort", File.ReadAllText(Path.Combine(server, "StatusColumn.razor")), StringComparison.Ordinal);
        Assert.Contains("r.CacheShort", File.ReadAllText(Path.Combine(server, "LiveRunView.cs")), StringComparison.Ordinal);
    }
}
