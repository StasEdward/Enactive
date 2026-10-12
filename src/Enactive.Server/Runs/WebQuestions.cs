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

/// <summary>
/// The browser's side of asking (IDecisionPresenter): the run's waiting question is held here, where every page
/// watching the run - the owner's, another Operator's, one opened after the question was asked - finds it. The rules of
/// asking stay the DecisionRouter's; this only keeps the question up and takes it down.
/// </summary>
internal sealed class WebQuestions : IDecisionPresenter
{
    private volatile WebQuestion? _pending;

    /// <summary>The question up now, or null.</summary>
    public WebQuestion? Pending => _pending;

    /// <summary>A question went up or came down. Raised on the run's thread; a page marshals it to its own.</summary>
    public event Action? Changed;

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
