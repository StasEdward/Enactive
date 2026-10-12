namespace Enactive.App.Ui.ViewModels;

using Enactive.Core.Events;

// What the view models say about what they show, for the view to draw (Palette). Each is the fact a colour, a weight
// or an indent stood for when the view models picked those themselves - kept together, and free of Avalonia, so the
// tests can reach them along with the step cards.

/// <summary>
/// Where a step card stands, as its colour says it: the card's status - except that a card waiting for a person, or
/// blocked until its cause is put right, says so over whatever its status is.
/// </summary>
internal enum CardTone { Pending, Running, Done, Failed, Skipped, Unverified, NeedsYou }

/// <summary>
/// Where a staged change stands: waiting for a person, applied, rejected - or refused, when applying or rejecting it
/// could not be done (the file changed after the proposal was made) and the person still has to decide.
/// </summary>
internal enum ChangeState { Staged, Applied, Rejected, Refused }

/// <summary>Whether a schedule will run: off, on but with no time it can ever be run at, or on with a next run.</summary>
internal enum ScheduleState { Off, Untimed, Scheduled }
