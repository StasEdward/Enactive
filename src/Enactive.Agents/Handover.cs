namespace Enactive.Agents;

using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>Generates a handover note. Rejects incomplete/tool replies and accounts for consumed tokens.
/// Scheduling, retry intervals and transcript replacement remain with the caller.</summary>
public sealed class Handover : IHandover
{
    public async Task<string?> GenerateAsync(
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

        try
        {
            if (runBudget.TurnExhausted is not null) return null;
            var request = GenerationAllowance.Fit(step with { Messages = asked }, provider);
            var completion = await provider.CompleteAsync(request, ct);

            runBudget.TokensUsed(completion.PromptTokens ?? 0, completion.CompletionTokens ?? 0);

            // A reply that CALLS a tool is not a note, whatever text comes with it: the call will not
            // be run, and "I will read Prod.cs next" carried into the next conversation is an
            // intention where a record of results should be.
            if (completion.Message.ToolCalls is { Count: > 0 } || completion.FinishReason is "length" or "max_tokens")
                return null;

            var note = completion.Message.Content?.Trim();
            return string.IsNullOrWhiteSpace(note) ? null : note;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A provider that failed this one call has not failed the step. The handover is an
            // economy, and an economy that throws is worse than one that does not happen.
            return null;
        }
    }

}
