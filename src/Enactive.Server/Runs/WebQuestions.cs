namespace Enactive.Server.Runs;

using Enactive.Application;
using Enactive.Core.Permissions;

/// <summary>A question a run is waiting on, as a page shows it: what it asks, and the answers it offers.</summary>
/// <param name="AskedAt">
/// When it went up (UTC): with the service's wait (IRunControl.QuestionWait) it says how long it still waits before the
/// run stops at it for the inbox - without it a page could say only that it waits, not for how long.
/// </param>
internal sealed record WebQuestion(DecisionRequest Request, IReadOnlyList<DecisionChoice> Choices, Func<DecisionChoice, bool> Answer,
    DateTimeOffset AskedAt);

/// <summary>A question answered from a page: which (DecisionRequest.Id), by whom, with which answer (its label).</summary>
internal sealed record AnsweredQuestion(Guid Question, string By, string Answer);

/// <summary>
/// The browser's side of asking (IDecisionPresenter): the run's waiting question is held here, where every page
/// watching the run - the owner's, another Operator's, one opened after the question was asked - finds it. The rules of
/// asking stay the DecisionRouter's; this only keeps the question up and takes it down.
/// </summary>
internal sealed class WebQuestions : IDecisionPresenter
{
    private volatile WebQuestion? _pending;
    private volatile AnsweredQuestion? _answered;

    /// <summary>The question up now, or null.</summary>
    public WebQuestion? Pending => _pending;

    /// <summary>
    /// The last question answered from a page, and by whom; null before any. Every other page that showed it says so: two
    /// people who press at once each expect their answer to count, and the press that came second is often dropped
    /// unseen - the card it was on was gone by then - so the page has only this to tell its person what happened.
    /// </summary>
    public AnsweredQuestion? LastAnswered => _answered;

    /// <summary>A question went up or came down, or was answered. Raised on the run's thread, or on the answering caller's (Answered); a page marshals it to its own.</summary>
    public event Action? Changed;

    /// <summary>Records who answered a question, once the run has taken the answer (RunControl.Answer).</summary>
    public void Answered(Guid question, string by, string answer)
    {
        _answered = new AnsweredQuestion(question, by, answer);
        Changed?.Invoke();
    }

    public Task ShowAsync(DecisionRequest request, IReadOnlyList<DecisionChoice> choices, Func<DecisionChoice, bool> answer)
    {
        _pending = new WebQuestion(request, choices, answer, DateTimeOffset.UtcNow);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ClearAsync(DecisionRequest request)
    {
        // Only THIS question: a later one already up in its place stays.
        if (_pending?.Request.Id == request.Id)
        {
            _pending = null;
            Changed?.Invoke();
        }
        return Task.CompletedTask;
    }
}
