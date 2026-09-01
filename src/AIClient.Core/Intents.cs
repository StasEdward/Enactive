namespace AIClient.Core.Intents;

using AIClient.Core.Context;

/// <summary>Where an intent came from.</summary>
public enum IntentSource { CommandBar, ChatMessage, ContextAction, Inbox, Schedule }

/// <summary>
/// The entry point of the whole pipeline (PLAN_v2 §2.1). The user expresses an intent;
/// the orchestrator decides whether it becomes a QuickAction or a Task.
/// </summary>
public sealed record Intent(
    Guid Id,
    string RawText,
    IntentSource Source,
    WorkContext Context,
    DateTimeOffset At);
