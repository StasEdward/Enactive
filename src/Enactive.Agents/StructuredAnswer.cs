namespace Enactive.Agents;

using Enactive.Core.Chat;
using Enactive.Core.Providers;

/// <summary>How a round of asking for a structured answer came out.</summary>
internal enum AnswerKind
{
    /// <summary>A usable answer arrived - the first time, or after one correction.</summary>
    Answered,

    /// <summary>Answers arrived and none could be used, even after being told what was wrong.</summary>
    Unusable,

    /// <summary>The provider failed: nothing came back to use.</summary>
    Failed,

    /// <summary>The budget was spent before the model could be asked (again).</summary>
    OutOfBudget
}

/// <summary>What a round of asking came to, and what it cost - every call made, the correction included.</summary>
/// <param name="Errors">What was wrong with the last answer, when none could be used.</param>
/// <param name="Problem">The provider's error, or why the budget stopped the round.</param>
internal sealed record AnswerRound<T>(
    AnswerKind Kind,
    T? Value,
    IReadOnlyList<string> Errors,
    string? Problem,
    int PromptTokens,
    int CompletionTokens,
    int? CachedPromptTokens,
    int? CacheCreationPromptTokens)
{
    /// <summary>How many times the model was asked - a caller says a first failure apart from a failed correction.</summary>
    public int Asked { get; init; }

    /// <summary>
    /// Whether any answer of the round was cut off at its length limit - not only the last. The planner starts no work
    /// after a plan that ran out of room, even when the correction that followed was unreadable for another reason.
    /// </summary>
    public bool CutOff { get; init; }
}

/// <summary>
/// Asks a model for a structured answer - a verdict, a contract - and, when the answer cannot be used, tells it
/// what was wrong and asks once more.
///
/// <para><b>Why one.</b> The step's review, the criteria review and the plan's contract review each wrote this round
/// by hand: two attempts, the budget, the provider's error, the token tally, the correction. The copies drifted where
/// nobody looked: the step's review refused an answer cut off at its length limit, or one that called a tool, however
/// whole the JSON in it looked (code review of engeen_v4, P2) - the criteria review read either as a finished verdict.
/// What each caller really has of its own is its prompt, how it reads an answer, and how it words a correction.</para>
/// </summary>
internal static class StructuredAnswer
{
    /// <param name="messages">The conversation so far; an unusable answer and its correction are added to it.</param>
    /// <param name="request">The request for the conversation as it stands - the caller's model, purpose and limits.</param>
    /// <param name="read">Reads an answer, given whether it was finished: a value, or what is wrong with it.</param>
    /// <param name="correction">What the model is told when an answer cannot be used, from what was wrong with it.</param>
    /// <param name="budget">Why the model may not be asked again, given what this round has spent - or null when it may.</param>
    /// <param name="requireComplete">
    /// Refuse an answer cut off at its length limit, or one that called a tool, before reading it. Off only for a reader
    /// that judges an unfinished answer itself.
    /// </param>
    public static async Task<AnswerRound<T>> AskAsync<T>(
        IChatProvider provider,
        List<ChatMessage> messages,
        Func<List<ChatMessage>, ChatRequest> request,
        Func<string, bool, (T? Value, IReadOnlyList<string> Errors)> read,
        Func<IReadOnlyList<string>, string> correction,
        Func<int, int, string?>? budget,
        bool requireComplete,
        CancellationToken ct,
        int attempts = 2)
    {
        int prompt = 0, output = 0, asked = 0;
        int? cached = null, created = null;
        var cutOff = false;
        IReadOnlyList<string> errors = [];

        AnswerRound<T> Round(AnswerKind kind, T? value = default, string? problem = null)
            => new(kind, value, errors, problem, prompt, output, cached, created) { Asked = asked, CutOff = cutOff };

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (budget?.Invoke(prompt, output) is { } spent)
                return Round(AnswerKind.OutOfBudget, problem: spent);

            ChatCompletion completion;
            try
            {
                asked++;
                completion = await provider.CompleteAsync(GenerationAllowance.Fit(request(messages), provider), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Round(AnswerKind.Failed, problem: ex.Message);
            }

            prompt += completion.PromptTokens ?? 0;
            output += completion.CompletionTokens ?? 0;
            cached = TokenCounts.Add(cached, completion.CachedPromptTokens);
            created = TokenCounts.Add(created, completion.CacheCreationPromptTokens);

            var answer = completion.Message.Content ?? "";
            // An answer cut off at its length, or one that called for a tool, is not a finished answer, however whole
            // the JSON in it looks.
            cutOff |= completion.FinishReason is "length" or "max_tokens";
            var unfinished = completion.FinishReason is "length" or "max_tokens"
                ? "your answer was cut off at its length limit; return the JSON object alone, and keep it short"
                : completion.Message.ToolCalls is { Count: > 0 }
                    ? "no tools are offered here; return the JSON object alone"
                    : null;

            T? value;
            if (requireComplete && unfinished is not null)
                (value, errors) = (default, [unfinished]);
            else
                (value, errors) = read(answer, unfinished is null);

            if (errors.Count == 0 && value is not null)
                return Round(AnswerKind.Answered, value);

            messages.Add(ChatMessage.Assistant(answer));
            messages.Add(ChatMessage.User(correction(errors)));
        }

        return Round(AnswerKind.Unusable);
    }

    /// <summary>The correction most answers get: the problems, one per line, and a request for the corrected object.</summary>
    public static string Listed(IReadOnlyList<string> errors, string ask)
        => "Your answer could not be used:\n" + string.Join("\n", errors.Select(e => "- " + e)) + "\n" + ask;
}
