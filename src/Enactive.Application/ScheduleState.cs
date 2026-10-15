namespace Enactive.Application;

/// <summary>Whether a schedule will run: off, on but with no time it can ever be run at, or on with a next run.</summary>
internal enum ScheduleState { Off, Untimed, Scheduled }
