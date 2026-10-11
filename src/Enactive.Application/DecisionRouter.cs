namespace Enactive.Application;

using Enactive.Core.Permissions;

/// <summary>What answering with a choice also remembers, beyond this one question.</summary>
internal enum RememberAs { Nothing, Session, Workspace, WorkspaceUnwatched }

/// <summary>One way a person can answer a question: an option the request offers, or "allow" remembered for longer.</summary>
internal sealed record DecisionChoice(string Label, string OptionId, RememberAs Remember = RememberAs.Nothing);

/// <summary>
/// Shows a question to a person, wherever they are - a card in the window, a page in the browser - and takes it away.
/// The rules of asking (one question at a time, which answers are offered, what an answer remembers, a cancelled
/// question never answered) are the <see cref="DecisionRouter"/>'s; this only draws.
/// </summary>
internal interface IDecisionPresenter
{
    /// <summary>
    /// Puts the question up. When the person picks a choice the presenter calls <paramref name="answer"/>, from
    /// whatever thread; it returns false when the question is no longer the one being asked (cancelled meanwhile).
    /// </summary>
    Task ShowAsync(DecisionRequest request, IReadOnlyList<DecisionChoice> choices, Func<DecisionChoice, bool> answer);

    /// <summary>Takes THIS question down - and leaves alone a later one already put up in its place.</summary>
    Task ClearAsync(DecisionRequest request);
}

/// <summary>
/// Asks a person the questions a run raises, through a presenter, and turns the answer into the decision the run is
/// waiting for - remembering it, when the person chose that, as the person who answered.
///
/// <para><b>Why here and not in the window.</b> The window built the buttons, wrote the remembered approval and held
/// the "a question is pending" state inside its own card code, where no test could reach it. The service will ask its
/// people the same questions in a browser, and a second copy of which "allow" may be remembered is exactly the copy
/// that would drift - in the permissive direction, the one that matters (a shell remembered for good).</para>
/// </summary>
/// <param name="principal">Who answers, for a host serving several people; null for the account the process runs as.</param>
/// <param name="parkAfter">
/// How long a question waits before the run stops at it and it goes to the inbox, to be answered there (the service:
/// a closed tab must not hold a run's slot and folder for hours). Null waits for as long as it takes - the window,
/// whose person is at the screen or closes the window and with it the run.
/// </param>
internal sealed class DecisionRouter(
    IDecisionPresenter presenter, SessionApprovals session, ApprovalStore approvals, string? principal = null,
    TimeSpan? parkAfter = null) : IDecisionHandler
{
    private readonly DecisionQueue _queue = new();
    private readonly object _gate = new();
    private DecisionCompletion? _pending;

    /// <summary>A question is up and waiting for its answer.</summary>
    public bool IsPending
    {
        get { lock (_gate) return _pending is not null; }
    }

    /// <summary>One question at a time: a second run's question waits until the first is answered or withdrawn.</summary>
    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
        => _queue.RequestAsync(request, ct, AskAsync);

    /// <summary>
    /// The answers a question offers: its own options, and - for one an approval can answer - "allow" remembered for
    /// the session, and for the workspace (watched runs, or unwatched too) unless it is a shell.
    /// </summary>
    public static IReadOnlyList<DecisionChoice> ChoicesFor(DecisionRequest request)
    {
        var choices = request.Options.Select(o => new DecisionChoice(o.Label, o.Id)).ToList();

        // Only a request an approval can answer: one that names its tool, is bound to a folder, has an "allow" to
        // give and does not insist on a fresh answer (a phone's).
        if (request.RequiresExplicitAnswer || string.IsNullOrEmpty(request.Subject) || request.Action is null
            || !request.Options.Any(o => o.Id == RememberedApprovals.AllowOptionId))
            return choices;

        const string allow = RememberedApprovals.AllowOptionId;
        choices.Add(new DecisionChoice("Allow (session)", allow, RememberAs.Session));

        // Not for a shell: that approval would outlive the process, and what it grants is arbitrary command execution
        // rather than one named action. Two, because they grant two different things: the first holds while somebody
        // is at the screen, the second lets runs in the background and on a schedule use the tool too - said on its
        // button, so nobody gives it by clicking the first (ApprovalStore, rule 4).
        if (request.MayBeRemembered)
        {
            choices.Add(new DecisionChoice("Allow (workspace)", allow, RememberAs.Workspace));
            choices.Add(new DecisionChoice("Allow (workspace, unwatched runs too)", allow, RememberAs.WorkspaceUnwatched));
        }

        return choices;
    }

    private async Task<DecisionOutcome> AskAsync(DecisionRequest request, CancellationToken ct)
    {
        var completion = new DecisionCompletion();

        // The question lives exactly as long as the request: cancelling used to complete the task and stop there, the
        // card stayed up and "a decision is pending" stayed true, so the NEXT run would not start - held by a question
        // belonging to a task that had already been stopped.
        using var registration = ct.Register(() => completion.Cancel(ct));
        using var parking = new CancellationTokenSource();
        if (parkAfter is { } wait)
            _ = Task.Delay(wait, parking.Token).ContinueWith(_ => completion.Park(request),
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        lock (_gate)
            _pending = completion;
        try
        {
            // Cancelled before it could be shown: no card for a dead request.
            if (!completion.Task.IsCompleted)
                await presenter.ShowAsync(request, ChoicesFor(request), choice => Answer(completion, request, choice)).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            parking.Cancel();
            lock (_gate)
            {
                if (ReferenceEquals(_pending, completion))
                    _pending = null;
            }
            await presenter.ClearAsync(request).ConfigureAwait(false);
        }
    }

    /// <summary>An answer counts only for the question still being asked, and remembers only what it was given for.</summary>
    private bool Answer(DecisionCompletion completion, DecisionRequest request, DecisionChoice choice)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, completion))
                return false;
        }
        return completion.Resolve(choice.OptionId, Remembering(request, choice));
    }

    private Action? Remembering(DecisionRequest request, DecisionChoice choice)
    {
        if (request.Action?.WorkingDirectory is not { } root || request.Subject is not { } tool)
            return null;

        return choice.Remember switch
        {
            RememberAs.Session => () => session.Remember(request, principal),
            // Asked again rather than trusted from the choice: a choice made up by a presenter must not remember a
            // shell for good (DecisionRequest.MayBeRemembered). ApprovalStore refuses a shell either way.
            RememberAs.Workspace when request.MayBeRemembered => () => approvals.Approve(root, tool, principal: principal),
            RememberAs.WorkspaceUnwatched when request.MayBeRemembered => () => approvals.Approve(root, tool, unwatched: true, principal),
            _ => null
        };
    }
}
