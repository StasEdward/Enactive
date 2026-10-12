namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Diagnostics;
using Enactive.Core.Workers;
using Enactive.Server.Accounts;

/// <summary>
/// The service's log in the browser (ADR 0002, open question "the log window and log analysis come over as pages"):
/// the recent entries for anybody signed in, filtered as the window filters them (LogFilter), and an analysis of what
/// is shown by a model for an Operator - it spends a model's time, and its answer is about runs (ADR 0002 §4).
/// </summary>
internal sealed class ServerLogs(UserStore users, ServerEngine engine)
{
    /// <summary>The newest entries that match, oldest first, at most <paramref name="newest"/>.</summary>
    public IReadOnlyList<LogEntry> Entries(ClaimsPrincipal user, LogLevel minimum, IReadOnlyCollection<LogSource> sources, string search, int newest = 500)
    {
        if (ServerAuth.StillValid(users, user) is null)
            return [];
        var wanted = sources.Count == 0 ? Enum.GetValues<LogSource>().ToHashSet() : sources.ToHashSet();
        return engine.Log.Snapshot()
            .Where(e => LogFilter.Matches(e, minimum, wanted, search))
            .TakeLast(newest)
            .ToList();
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
    /// Asks the review model what went wrong in these entries - the text a person sees and exports (LogText), not the
    /// whole buffer: narrowing is how a person points at what they are asking about.
    /// </summary>
    public async Task<(LogAnalysisResult? Result, string? Problem)> AnalyseAsync(ClaimsPrincipal user, IReadOnlyList<LogEntry> entries, CancellationToken ct)
    {
        if (ServerAuth.StillValid(users, user) is not { Role: >= Role.Operator } account)
            return (null, "Your role does not include asking a model about the log.");
        if (engine.Current is not { } composed)
            return (null, engine.Problem ?? "There is no engine to ask.");
        if (entries.Count == 0)
            return (null, "Nothing is shown to ask about.");

        // The review model of the default worker, as the window asks it.
        var worker = composed.Workers.Default;
        if ((composed.Router.Resolve(ModelPurpose.Review, worker) ?? worker.ModelPolicy.Preferred) is not { } model)
            return (null, "No model is set to review with.");

        users.Audit(account.Name, "log analysed", $"{entries.Count} entries, {model.ProviderId}/{model.Model}");
        // promptBodies: false - this prompt CARRIES the log, and logging it would write the log back into itself.
        var result = await new LogAnalyst().AnalyseAsync(LogText.Render(entries),
            composed.Providers.Create(model.ProviderId, promptBodies: false), model.Model, composed.EngineOptions.NumCtx, ct);
        return (result, null);
    }
}
