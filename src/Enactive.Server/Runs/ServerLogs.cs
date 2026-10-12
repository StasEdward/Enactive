namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Diagnostics;
using Enactive.Core.Workers;
using Enactive.Server.Accounts;

/// <summary>
/// The service's log in the browser (ADR 0002, open question "the log window and log analysis come over as pages"): the
/// window's own log view (LogWindowViewModel) over the service's one hub, for anybody signed in; an analysis of what is
/// shown by a model for an Operator - it spends a model's time, and its answer is about runs (ADR 0002 §4); and the
/// raw-wire switch for an Admin, because it changes what the service records for everybody.
/// </summary>
internal sealed class ServerLogs(UserStore users, ServerEngine engine)
{
    /// <summary>
    /// The window's log view, over the service's hub, for a Viewer or above: the filtering, the level and source choices,
    /// Pause, the status line and Export's text are the window's. What it changes beyond itself comes back here as the
    /// person (<see cref="AsThePerson"/>): its Clear empties only the view - the hub is everybody's - and its raw-wire
    /// switch is <see cref="SetCapture"/>. Analyze is given to an Operator and comes back through <see cref="AnalyseAsync"/>.
    /// </summary>
    /// <param name="drain">The page's clock for the lines that arrive (LogWindowViewModel's drain), on its own circuit.</param>
    public (LogWindowViewModel? View, string? Problem) View(ClaimsPrincipal user, Func<Action, IDisposable> drain)
    {
        if (ServerAuth.StillValid(users, user) is not { Role: >= Role.Viewer } account)
            return (null, "Your session has ended. Sign in again.");
        var view = new LogWindowViewModel(engine.Log, drain, new AsThePerson(this, engine, user));
        if (account.Role >= Role.Operator)
            view.Analyse = async (text, ct) =>
            {
                var (result, problem) = await AnalyseAsync(user, text, ct);
                // Thrown, so the view says it as the window says a failed analysis: "The analysis did not run: …".
                return result ?? throw new InvalidOperationException(problem);
            };
        return (view, null);
    }

    /// <summary>
    /// One run's lines, at every level, oldest first - the live run's Log tab, as the window's (RunLogViewModel): the entries
    /// that carry the run's id, the newest <paramref name="newest"/> of them. A tail, not an archive.
    /// </summary>
    public IReadOnlyList<LogEntry> OfRun(ClaimsPrincipal user, Guid runId, int newest = 4000)
    {
        if (ServerAuth.StillValid(users, user) is null)
            return [];
        return engine.Log.Snapshot().Where(e => e.RunId == runId).TakeLast(newest).ToList();
    }

    /// <summary>
    /// Turns raw-wire capture on (the hub's level to Trace, which is what records the byte-level HTTP dumps) or back off to
    /// the level the service runs at. An Admin's, and audited: it changes what the service records for every run and every
    /// person from that moment, and the dumps carry whole prompts and replies. Null when done; otherwise why not.
    /// </summary>
    public string? SetCapture(ClaimsPrincipal user, bool trace)
    {
        if (ServerAuth.StillValid(users, user) is not { } account)
            return "Your session has ended. Sign in again.";
        if (account.Role < Role.Admin)
            return "Your role does not include changing what the service's log records.";
        engine.Log.MinLevel = trace ? LogLevel.Trace : engine.NormalLogLevel;
        users.Audit(account.Name, trace ? "raw wire capture on" : "raw wire capture off", $"log level {engine.Log.MinLevel}");
        return null;
    }

    /// <summary>
    /// Asks the review model what went wrong in this text - the log as a person sees and exports it (LogText), not the
    /// whole buffer: narrowing is how a person points at what they are asking about.
    /// </summary>
    public async Task<(LogAnalysisResult? Result, string? Problem)> AnalyseAsync(ClaimsPrincipal user, string text, CancellationToken ct)
    {
        if (ServerAuth.StillValid(users, user) is not { Role: >= Role.Operator } account)
            return (null, "Your role does not include asking a model about the log.");
        if (engine.Current is not { } composed)
            return (null, engine.Problem ?? "There is no engine to ask.");
        if (string.IsNullOrWhiteSpace(text))
            return (null, "Nothing is shown to ask about.");

        // The review model of the default worker, as the window asks it.
        var worker = composed.Workers.Default;
        if ((composed.Router.Resolve(ModelPurpose.Review, worker) ?? worker.ModelPolicy.Preferred) is not { } model)
            return (null, "No model is set to review with.");

        var lines = text.Count(c => c == '\n');
        users.Audit(account.Name, "log analysed", $"{lines} lines, {model.ProviderId}/{model.Model}");
        // promptBodies: false - this prompt CARRIES the log, and logging it would write the log back into itself.
        var result = await new LogAnalyst().AnalyseAsync(text,
            composed.Providers.Create(model.ProviderId, promptBodies: false), model.Model, composed.EngineOptions.NumCtx, ct);
        return (result, null);
    }

    /// <summary>
    /// The log view's changes to the hub, as the person whose page it is. Clear is the view's alone: the service's hub is
    /// everybody's, and emptying it from one page emptied every other open log page and the analysis' material with it.
    /// The switch goes through <see cref="SetCapture"/>, which checks and audits.
    /// </summary>
    private sealed class AsThePerson(ServerLogs logs, ServerEngine engine, ClaimsPrincipal user) : ILogHubChanges
    {
        public bool Capturing => engine.Log.MinLevel <= LogLevel.Trace;

        public string? Capture(bool trace) => logs.SetCapture(user, trace);

        public void Cleared() { }
    }
}
