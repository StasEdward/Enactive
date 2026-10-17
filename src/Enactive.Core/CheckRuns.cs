namespace Enactive.Core.Checks;

using System.Text.Json.Serialization;
using Enactive.Core.Templates;

/// <summary>
/// "Run checks now" on a staged run (Plan Phase 3.2, staged runs, first version): what a person's press found - the run's
/// snapshotted checks, run under the permissions of the moment against the workspace as it was then - kept in the run's
/// history beside the run, one record per press.
///
/// <para>Not hung off the proposal set: the service deletes a set's folder once every file in it is decided
/// (ServerStaging), and the records must stay reachable from the run's history after that. Never the run's outcome: the
/// run's outcome was recorded when it ended, and a record says only "this state was checked at this time".</para>
/// </summary>
/// <param name="Id">This record, among the run's.</param>
/// <param name="RunId">The run whose snapshotted checks were run.</param>
/// <param name="Who">Who pressed it - the signed-in account on the service, the person at the desktop.</param>
/// <param name="At">When the checks ran.</param>
/// <param name="Checks">The definitions run, as the run's snapshot held them - not the manifests as they are now.</param>
/// <param name="Results">What each check came to, in the order of <paramref name="Checks"/>.</param>
/// <param name="Fingerprint">The workspace's state the record is about: a mismatch later marks the record outdated.</param>
public sealed record CheckRecord(
    Guid Id,
    Guid RunId,
    string Who,
    DateTimeOffset At,
    IReadOnlyList<CheckDefinition> Checks,
    IReadOnlyList<CheckResultLine> Results,
    StateFingerprint Fingerprint)
{
    /// <summary>
    /// What the person should know about this record beyond its results - the workspace changed while the checks ran, say.
    /// Null when there is nothing to add.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>One check's result in a <see cref="CheckRecord"/>.</summary>
/// <param name="Outcome">Stored by name, as every enum the engine persists: a record written today must read the same after a member is added.</param>
/// <param name="Blocking">The check's severity was Blocking.</param>
/// <param name="Command">What ran: the check's command, or the commands its ecosystem gave it.</param>
/// <param name="Detail">Why it was not run, or the start and end of what it printed - as a criterion's detail.</param>
public sealed record CheckResultLine(
    string CheckId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<CriterionOutcome>))] CriterionOutcome Outcome,
    bool Blocking,
    string Command,
    int? ExitCode,
    string? Detail)
{
    /// <summary>Workspace-relative path of the file with its whole output (CheckRecords.WriteDetail), with '/'; null when none.</summary>
    public string? DetailPath { get; init; }
}

/// <summary>How a <see cref="StateFingerprint"/> was taken. By name, as every persisted enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FingerprintKind>))]
public enum FingerprintKind
{
    /// <summary>From git: HEAD, the status and the content of every changed file - any change in the tracked tree is caught.</summary>
    Git,

    /// <summary>
    /// Outside git (or where git would not answer): the content of the paths the run proposed and of the files the checks'
    /// matches cover. A change anywhere else is not caught, and the record says so.
    /// </summary>
    Files,

    /// <summary>Nothing could be taken: the record cannot be shown as matching anything, so it reads as outdated.</summary>
    None
}

/// <summary>
/// The state of a workspace a <see cref="CheckRecord"/> was taken against, as one digest and what it was made of. Compared
/// by <see cref="Digest"/> with one taken the same way later (StateFingerprints.MatchesAsync): a difference marks the record
/// outdated. A match is not proof that nothing outside it changed - <see cref="Caveat"/> says what it does not cover.
/// </summary>
/// <param name="Digest">SHA-256, lower-case hex, over everything below; empty for <see cref="FingerprintKind.None"/>.</param>
public sealed record StateFingerprint(FingerprintKind Kind, string Digest)
{
    /// <summary>The commit HEAD named, in a git workspace; null outside git or in a repository with no commit yet.</summary>
    public string? Head { get; init; }

    /// <summary>How many files' contents went into the digest.</summary>
    public int Files { get; init; }

    /// <summary>
    /// Outside git: the paths the run proposed, workspace-relative with '/' - kept so the same paths are hashed again when
    /// the record is compared, whatever the history says by then. Null in a git workspace.
    /// </summary>
    public IReadOnlyList<string>? Proposed { get; init; }

    /// <summary>Outside git: the checks' matches globs whose files were hashed. Null in a git workspace.</summary>
    public IReadOnlyList<string>? Matches { get; init; }

    /// <summary>Outside git: the ecosystems whose files were hashed, for a check that runs through one with no matches of its own.</summary>
    public IReadOnlyList<string>? Ecosystems { get; init; }

    /// <summary>What a match of this fingerprint does not prove - always said for <see cref="FingerprintKind.Files"/>; null when nothing needs adding.</summary>
    public string? Caveat { get; init; }
}
