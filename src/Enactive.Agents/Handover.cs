namespace Enactive.Agents;

using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>Why a handover produced no note. Each is a different fault with a different remedy.</summary>
public enum HandoverFailure
{
    /// <summary>The run's budget was already spent; nothing was asked.</summary>
    BudgetExhausted,

    /// <summary>The conversation leaves no room in the window for the note; nothing was asked.</summary>
    NoRoom,

    /// <summary>The provider failed the call.</summary>
    ProviderError,

    /// <summary>The model called a tool instead of writing the note.</summary>
    ToolCall,

    /// <summary>The answer was cut at the output limit - by the note itself, or by reasoning before it.</summary>
    Truncated,

    /// <summary>The model finished and wrote nothing.</summary>
    Empty
}

/// <summary>
/// What asking for a handover note came to: the note, or why there is none - with what was measured,
/// so the reason can be told from a log line and not guessed at.
/// </summary>
public sealed record HandoverResult(string? Note, HandoverFailure? Failure = null, string? Detail = null)
{
    public string? FinishReason { get; init; }
    public int? OutputTokenLimit { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int NoteCharacters { get; init; }
    public int ThinkingCharacters { get; init; }
    public IReadOnlyList<string> ToolsCalled { get; init; } = [];

    public static HandoverResult Written(string note) => new(note);

    /// <summary>One line for the log and the step's events: the reason, then what was measured.</summary>
    public string Describe()
    {
        var reason = Failure switch
        {
            HandoverFailure.BudgetExhausted => "the run's budget was already spent, so it was not asked for",
            HandoverFailure.NoRoom => "the conversation left no room in the window to write it, so it was not asked for",
            HandoverFailure.ProviderError => "the provider failed the call",
            HandoverFailure.ToolCall => $"the model called {string.Join(", ", ToolsCalled)} instead of writing it",
            HandoverFailure.Truncated => ThinkingCharacters > 0 && NoteCharacters == 0
                ? "the output limit was reached while the model was still reasoning, before any note"
                : "the note was cut at the output limit",
            HandoverFailure.Empty => "the model finished without writing anything",
            _ => "it was written"
        };
        var measured = new List<string>();
        if (FinishReason is not null) measured.Add($"finish={FinishReason}");
        if (CompletionTokens is not null) measured.Add($"{CompletionTokens} output token(s)" + (OutputTokenLimit is { } limit ? $" of {limit}" : ""));
        else if (OutputTokenLimit is { } only) measured.Add($"output limit {only}");
        if (PromptTokens is not null) measured.Add($"{PromptTokens} prompt token(s)");
        if (Failure is not (HandoverFailure.BudgetExhausted or HandoverFailure.NoRoom or HandoverFailure.ProviderError))
            measured.Add($"{NoteCharacters} character(s) of note, {ThinkingCharacters} of reasoning");
        if (Detail is not null) measured.Add(Detail);
        return reason + (measured.Count > 0 ? " (" + string.Join("; ", measured) + ")" : "");
    }
}

/// <summary>Generates a handover note. Rejects incomplete/tool replies and accounts for consumed tokens.
/// Scheduling, retry intervals and transcript replacement remain with the caller.</summary>
public sealed class Handover : IHandover
{
    public async Task<HandoverResult> GenerateAsync(
        IChatProvider provider, ChatRequest step, RunBudget runBudget, CancellationToken ct)
    {
        var asked = new List<ChatMessage>(step.Messages)
        {
            ChatMessage.User(
                "Before you continue: this conversation is being started over to keep it short, and "
                + "everything except your instructions will be dropped. Write the note you would "
                + "want to find. State what you have ALREADY established - findings, file paths, "
                + "numbers, what you checked and what it said - and what is still to do, in that "
                + "order. Facts only, no plan for the future beyond the next concrete action. Do "
                + "not call any tool; just write the note. Keep it under "
                + Math.Clamp((step.OutputTokenLimit ?? 2048) / 5, 50, 400) + " words; prioritize facts needed for the next action.")
        };

        if (runBudget.TurnExhausted is { } spent)
            return new(null, HandoverFailure.BudgetExhausted, spent);

        // Fitted apart from the call, so a note that cannot fit is not reported as a provider failing.
        ChatRequest request;
        try
        {
            request = GenerationAllowance.Fit(step with { Messages = asked }, provider);
        }
        catch (InvalidOperationException ex)
        {
            return new(null, HandoverFailure.NoRoom, ex.Message) { OutputTokenLimit = step.OutputTokenLimit };
        }

        ChatCompletion completion;
        try
        {
            completion = await provider.CompleteAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A provider that failed this one call has not failed the step. The handover is an
            // economy, and an economy that throws is worse than one that does not happen.
            return new(null, HandoverFailure.ProviderError, $"{ex.GetType().Name}: {ex.Message}")
                { OutputTokenLimit = request.OutputTokenLimit };
        }

        runBudget.TokensUsed(completion.PromptTokens ?? 0, completion.CompletionTokens ?? 0);
        var note = completion.Message.Content?.Trim();
        HandoverResult Measured(string? written, HandoverFailure? failure) => new(written, failure)
        {
            FinishReason = completion.FinishReason,
            OutputTokenLimit = request.OutputTokenLimit,
            PromptTokens = completion.PromptTokens,
            CompletionTokens = completion.CompletionTokens,
            NoteCharacters = note?.Length ?? 0,
            ThinkingCharacters = completion.Thinking?.Length ?? 0,
            ToolsCalled = completion.Message.ToolCalls?.Select(c => c.Name).ToArray() ?? []
        };

        // A reply that CALLS a tool is not a note, whatever text comes with it: the call will not
        // be run, and "I will read Prod.cs next" carried into the next conversation is an
        // intention where a record of results should be.
        if (completion.Message.ToolCalls is { Count: > 0 })
            return Measured(null, HandoverFailure.ToolCall);
        if (completion.FinishReason is "length" or "max_tokens")
            return Measured(null, HandoverFailure.Truncated);
        return string.IsNullOrWhiteSpace(note) ? Measured(null, HandoverFailure.Empty) : Measured(note, null);
    }
}
