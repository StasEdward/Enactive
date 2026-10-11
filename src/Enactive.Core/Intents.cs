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
///
/// <para><see cref="Web"/> is a run started in the service's browser interface (ADR 0002): a person
/// signed in to it is at a screen, as at the command bar, but not at this computer and not as its
/// Windows user - so the run names who started it (<c>RunRequest.Principal</c>), and what that person
/// allowed for good is theirs, not everybody's. Not <see cref="Remote"/>: a phone is asked afresh for
/// every call and never runs a shell; a browser on the local network is an operator of the machine.
/// Added last, so a value an older build recorded still reads as what it was.</para>
/// </summary>
public enum IntentSource { CommandBar, ChatMessage, ContextAction, Inbox, Schedule, Remote, Web }

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
