namespace Enactive.Providers;

using System.Diagnostics;
using System.Globalization;
using Enactive.Core.Diagnostics;
using Enactive.Core.Providers;

/// <summary>
/// The context window a provider's own server states for one model - asked when nobody typed a window for the provider.
///
/// <para><b>Why.</b> The window was only ever what a person typed into the provider (ContextWindowTokens). An Anthropic
/// provider nobody filled in stated none, so the reviewer's automatic evidence budget (ReviewEvidence) fell back to
/// 6,000 characters for claude-haiku-5-5 and its 1,000,000-token window - the number that made run 2eedd5 reject a
/// correct step. The typed number is also one per provider, where the window is per model: one provider serving Haiku
/// and Sonnet shared it. The server knows the window of the model it serves; asked, it removes a setting nobody should
/// have to retune per model. Everything that reads the window - the generation allowance, handover by percent, the
/// worker's working size, the evidence budget - reads it from here through IChatProvider.ContextWindow, unchanged.</para>
///
/// <para><b>Once per provider and model, for the process.</b> ContextWindow is asked many times a turn; the answer is a
/// fact about the server's model and does not change while the process runs. Kept here rather than in a provider
/// because the factory makes a new provider for each use.</para>
///
/// <para>A typed window still wins - the caller asks here only when there is none - and a server that does not say, or
/// cannot be asked, leaves the answer null: what it was before.</para>
/// </summary>
public static class ServerWindow
{
    /// <summary>
    /// How long the FIRST question for a provider and model is waited for, from when it was asked.
    ///
    /// <para>Waited for, not answered null while pending: ContextWindow is asked as each review is put together, and a
    /// model used only to review is first asked by the run's first review. Answering null there gave that review the
    /// fixed 6,000 characters - the very failure this exists to fix - however quickly the server would have said.
    /// Anthropic's catalog answers in well under a second and a local llama.cpp in milliseconds. Three seconds bounds what
    /// a server that does not answer costs: once per provider and model in the life of the process, never at startup -
    /// nothing asks before a run does - and never again for a question asked a second time.</para>
    /// </summary>
    internal static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(3);

    /// <summary>How long the question may take at all. Past <see cref="FirstWait"/> it goes on unwaited, and the calls
    /// after it read its answer - a slow server still gives the window to the rest of the run.</summary>
    internal static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// When a question that got no answer at all - the server unreachable, timed out, 5xx, 408, 429, or the key refused (401,
    /// 403) - is asked again.
    ///
    /// <para>Without it a server down when the first run asked would leave its models' windows unknown until the process
    /// restarts, and the server process runs for days. A question the server DID answer - a number, or "not here" (404) -
    /// is never asked again: that answer is the server's and will not change. Asked again unwaited, so a server that is
    /// still down costs a run nothing.</para>
    /// </summary>
    internal static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(10);

    /// <summary>One server's question for one model: the window it states, null when it says none; throws when it could
    /// not answer now.</summary>
    internal delegate Task<int?> Question(HttpClient http, ProviderDescriptor provider, string model, CancellationToken ct);

    private readonly record struct Key(string ProviderId, ProviderKind Kind, string BaseUrl, string Model);

    /// <param name="WaitFor">How long from <paramref name="StartedAt"/> a caller may wait for the answer: the first
    /// wait for the first question, nothing for a question asked again.</param>
    private sealed record Lookup(Task<int?> Asked, long StartedAt, TimeSpan WaitFor)
    {
        /// <summary>Whether the server answered - with a number or with nothing - rather than failing to.</summary>
        public bool Answered => Asked.IsCompletedSuccessfully && Asked.Result is not Unanswerable;
    }

    // What the question task returns when the server gave no answer at all (it never returns a real window this low).
    private const int Unanswerable = int.MinValue;

    private static readonly Dictionary<Key, Lookup> Lookups = new();
    private static readonly object Gate = new();

    /// <summary>
    /// The window <paramref name="provider"/>'s server stated for <paramref name="model"/>, when it has already said - read
    /// from what was asked, never asking. For the settings' hints, which must not wait on a server.
    /// </summary>
    public static int? Known(string providerId, ProviderKind kind, string baseUrl, string model)
    {
        lock (Gate)
            return Lookups.TryGetValue(KeyOf(providerId, kind, baseUrl, model), out var lookup) ? Window(lookup) : null;
    }

    /// <summary>
    /// The window the server states for <paramref name="model"/>: asked the first time, waited for up to
    /// <paramref name="firstWait"/> (<see cref="FirstWait"/>), and remembered.
    /// </summary>
    /// <param name="firstWait">The wait, when this call is the one that asks; a test's shorter wait.</param>
    /// <param name="askAgainAfter">A test's <see cref="AskAgainAfter"/>.</param>
    /// <param name="source">Where the number comes from, for the one line the log says when it is found.</param>
    internal static int? For(HttpClient http, ProviderDescriptor provider, string? model, ILogSink? log, string source,
        Question question, TimeSpan? firstWait = null, TimeSpan? askAgainAfter = null)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var key = KeyOf(provider.Id, provider.Kind, provider.BaseUrl, model);
        Lookup lookup;
        lock (Gate)
        {
            if (!Lookups.TryGetValue(key, out var known))
                Lookups[key] = lookup = new(Ask(http, provider, model, log, source, question), Stopwatch.GetTimestamp(),
                    firstWait ?? FirstWait);
            else if (known.Asked.IsCompleted && !known.Answered
                     && Stopwatch.GetElapsedTime(known.StartedAt) >= (askAgainAfter ?? AskAgainAfter))
                Lookups[key] = lookup = new(Ask(http, provider, model, log, source, question), Stopwatch.GetTimestamp(),
                    TimeSpan.Zero);
            else
                lookup = known;
        }

        if (!lookup.Asked.IsCompleted)
        {
            // From when it was asked, not from this call: several first callers together wait one wait between them, and
            // a caller after the wait is up does not wait at all.
            var left = lookup.WaitFor - Stopwatch.GetElapsedTime(lookup.StartedAt);
            if (left > TimeSpan.Zero)
                lookup.Asked.Wait(left);
        }
        return Window(lookup);
    }

    private static int? Window(Lookup lookup)
        => lookup.Asked.IsCompletedSuccessfully && lookup.Asked.Result is > 0 and var window ? window : null;

    private static Key KeyOf(string providerId, ProviderKind kind, string baseUrl, string model)
        => new(providerId.ToLowerInvariant(), kind, baseUrl.Trim().TrimEnd('/').ToLowerInvariant(), model.Trim());

    /// <summary>
    /// The question, on the thread pool. Not on the caller's thread: the first caller may hold a dispatcher (the log
    /// window's analysis starts on the UI thread), and a continuation posted back to a thread that is blocked waiting for
    /// it never runs - the wait would always time out, and before the wait had a limit it would have hung the window.
    /// </summary>
    private static Task<int?> Ask(HttpClient http, ProviderDescriptor provider, string model, ILogSink? log, string source,
        Question question)
        => Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(AskTimeout);
            try
            {
                var window = await question(http, provider, model, timeout.Token).ConfigureAwait(false);
                if (window is > 0 and var found)
                    log.Info(LogSource.System,
                        $"{provider.Id}/{model}: window {found.ToString("N0", CultureInfo.InvariantCulture)} tokens, from {source}.");
                else
                    log.Write(LogLevel.Debug, LogSource.System,
                        $"{provider.Id}/{model}: the server states no context window; none is assumed.");
                return window is > 0 ? window : null;
            }
            catch (Exception ex)
            {
                log.Write(LogLevel.Debug, LogSource.System,
                    $"{provider.Id}/{model}: could not ask the server for the context window ({ex.GetType().Name}: {ex.Message}); "
                    + $"none is assumed, and it is asked again after {AskAgainAfter.TotalMinutes:0} minutes.");
                return Unanswerable;
            }
        });
}
