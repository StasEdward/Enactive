namespace Enactive.Core.Intents;

using Enactive.Core.Context;

/// <summary>
/// Where an intent came from.
///
/// <para><see cref="Remote"/> is its own source rather than being folded into
/// <see cref="Schedule"/> or <see cref="Inbox"/>: a run started from another machine is a different
/// fact about provenance, and the two nearest existing values would each be a lie. It matters
/// because a person reading a timeline is entitled to know that nobody was sitting at this computer
/// when the run began.
/// </para>
/// </summary>
public enum IntentSource { CommandBar, ChatMessage, ContextAction, Inbox, Schedule, Remote }

/// <summary>
/// The entry point of the whole pipeline. The user expresses an intent;
/// the orchestrator decides whether it becomes a QuickAction or a Task.
/// </summary>
public sealed record Intent(
    Guid Id,
    string RawText,
    IntentSource Source,
    WorkContext Context,
    DateTimeOffset At,
    string? WorkerId = null);   // which role should handle this (null = provider's default)
