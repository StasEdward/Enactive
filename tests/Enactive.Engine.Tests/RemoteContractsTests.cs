namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Remote.Contracts;
using Xunit;

/// <summary>
/// The remote protocol's vocabulary, before anything uses it.
///
/// <para>Stage 1 of <c>Docs/REMOTE_DESIGN.md</c>. Nothing here does I/O, which is why it lives in
/// this project rather than in one of its own: the split that matters is between tests that need a
/// database and tests that do not, and these do not.</para>
///
/// <para>Three of these pin a decision that would otherwise be invisible when it changed - the
/// action hash's exact output, the classification of a fault code nobody has heard of, and the
/// pairing between a terminal event and the status it leaves behind.</para>
/// </summary>
public sealed class RemoteContractsTests
{
    // ── the action hash ─────────────────────────────────────────────────────

    /// <summary>
    /// The whole reason <see cref="ActionIdentity"/> is specified rather than left to the caller.
    ///
    /// <para>The expected value was computed independently of this codebase, from the canonical
    /// form written in that file's summary, so it pins the FORM and not merely whatever the C#
    /// currently produces. If this test fails, every approval that was pending across the change
    /// stopped matching its action - which in production looks like the owner pressing Allow and
    /// the Host answering that the action does not match, with nothing anywhere saying why.</para>
    /// </summary>
    [Fact]
    public void The_action_hash_matches_its_pinned_value()
    {
        var hash = ActionIdentity.Hash(
            runId: "run-1",
            toolCallId: "call-1",
            tool: "run_command",
            workingDirectory: "C:/work/Enactive",
            argumentsJson: """{"command":"dotnet test"}""");

        Assert.Equal("faebd5292ad32e9663c0d1f0215d53204f8c54019d02b6f203a1184401b99b00", hash);
        Assert.Equal("enactive-action-v1", ActionIdentity.Version);
    }

    /// <summary>
    /// One folder, three spellings, one action. A model writes the same path with backslashes, with
    /// forward slashes and with a trailing separator across three consecutive calls; an approval
    /// that stopped matching because of a slash would be an approval nobody could give.
    /// </summary>
    [Fact]
    public void A_folder_written_three_ways_is_one_action()
    {
        string Hash(string directory) => ActionIdentity.Hash("r", "c", "t", directory, "{}");

        Assert.Equal(Hash(@"C:\work\Enactive"), Hash("C:/work/Enactive"));
        Assert.Equal(Hash(@"C:\work\Enactive"), Hash(@"C:\work\Enactive\"));
    }

    /// <summary>
    /// Why the fields carry their length instead of being joined by a separator.
    ///
    /// <para>These two calls are different actions. Joined by newlines they produce the SAME text -
    /// the first hides a boundary inside its run id, the second puts the swallowed newline back at
    /// the front of a later field, and the two spellings cancel out. A hash that two different
    /// actions share is not an identity; it is a way to have one action approved by showing the
    /// owner another.</para>
    ///
    /// <para>The first version of this test was written against a forgery that the plain join
    /// already defeated, so it passed with the length prefix removed - it asserted something true
    /// and proved nothing. This fixture is the collision itself.</para>
    /// </summary>
    [Fact]
    public void A_newline_in_one_field_cannot_forge_a_boundary_in_another()
    {
        // Joined:  v \n a \n b \n \nz \n d \n e \n
        var first = ActionIdentity.Hash("a", "b", "\nz", "d", "e");

        // Joined:  v \n a\nb \n «empty» \n z \n d \n e \n   - the same bytes.
        var second = ActionIdentity.Hash("a\nb", "", "z", "d", "e");

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Two identical commands in one run are two actions. Without the call id, approving the first
    /// would approve the second - and "run the tests" twice is the benign version of that.
    /// </summary>
    [Fact]
    public void Two_calls_that_differ_only_by_call_id_are_two_actions()
        => Assert.NotEqual(
            ActionIdentity.Hash("r", "call-1", "run_command", "C:/w", "{}"),
            ActionIdentity.Hash("r", "call-2", "run_command", "C:/w", "{}"));

    // ── faults ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The rule a Host's outbox rests on, in the one case that is a judgement rather than a lookup.
    ///
    /// <para>A newer gateway can name a condition this build has never heard of. Retrying is the
    /// answer that never silently loses an event; dropping the unknown would be quiet and
    /// undiagnosable. It is only safe because the Host caps attempts and parks what it cannot get
    /// rid of - so if that cap is ever removed, this rule has to be revisited with it.</para>
    /// </summary>
    [Fact]
    public void An_unknown_fault_code_is_retried_rather_than_dropped()
    {
        Assert.Equal(FaultDisposition.Retry, RemoteFaults.DispositionOf("something-from-a-newer-gateway"));
        Assert.Equal(FaultDisposition.Retry, RemoteFaults.DispositionOf(null));
    }

    /// <summary>A settled condition is dropped: the past does not change on the second attempt.</summary>
    [Theory]
    [InlineData(FaultCode.RunEnded)]
    [InlineData(FaultCode.SequenceAlreadyApplied)]
    [InlineData(FaultCode.ApprovalAlreadyResolved)]
    [InlineData(FaultCode.ApprovalNotRemotelyDecidable)]
    [InlineData(FaultCode.ActionHashMismatch)]
    [InlineData(FaultCode.MalformedEvent)]
    public void A_settled_condition_is_dropped(string code)
        => Assert.Equal(FaultDisposition.Drop, RemoteFaults.DispositionOf(code));

    /// <summary>A revoked credential ends the connection. Reconnecting with it is pointless.</summary>
    [Theory]
    [InlineData(FaultCode.HostRevoked)]
    [InlineData(FaultCode.UnknownHost)]
    public void A_gone_credential_is_fatal(string code)
        => Assert.Equal(FaultDisposition.Fatal, RemoteFaults.DispositionOf(code));

    /// <summary>
    /// Every code this build defines is in the table. A constant added to <see cref="FaultCode"/>
    /// and forgotten in the table would be classified Retry by the unknown-code rule - which is the
    /// safe default for a stranger's code and the wrong answer for one of our own.
    /// </summary>
    [Fact]
    public void Every_defined_fault_code_is_classified()
    {
        var defined = typeof(FaultCode)
            .GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(defined);
        Assert.Empty(defined.Except(RemoteFaults.KnownCodes));
    }

    // ── the lifecycle vocabulary ────────────────────────────────────────────

    /// <summary>
    /// Every event kind is either an ending or one of the four that are not. A kind added without
    /// deciding which it is would otherwise be treated as non-terminal by omission, and a run that
    /// ended would stay open in the panel forever.
    /// </summary>
    [Fact]
    public void Every_event_kind_is_classified_as_an_ending_or_not()
    {
        RemoteEventKind[] notEndings =
        [
            RemoteEventKind.Running,
            RemoteEventKind.Progress,
            RemoteEventKind.ApprovalRequested,
            RemoteEventKind.ApprovalResolved
        ];

        foreach (var kind in Enum.GetValues<RemoteEventKind>())
        {
            Assert.Equal(!notEndings.Contains(kind), RunLifecycle.IsTerminal(kind));
        }
    }

    /// <summary>Same question for the gateway's own view of a run.</summary>
    [Fact]
    public void Every_run_status_is_classified_as_an_ending_or_not()
    {
        RemoteRunStatus[] notEndings =
        [
            RemoteRunStatus.Queued,
            RemoteRunStatus.Running,
            RemoteRunStatus.WaitingForUser,
            RemoteRunStatus.CancelRequested
        ];

        foreach (var status in Enum.GetValues<RemoteRunStatus>())
        {
            Assert.Equal(!notEndings.Contains(status), RunLifecycle.IsTerminal(status));
        }
    }

    /// <summary>
    /// <see cref="RunLifecycle.StatusOf(RemoteEventKind)"/> resolves a terminal kind by NAME, which
    /// works only for as long as the two enums keep naming the same endings the same way. That is a
    /// fact about today's declarations, not a guarantee the compiler makes, so it is asserted.
    /// </summary>
    [Fact]
    public void Every_ending_event_names_the_status_it_leaves_behind()
    {
        foreach (var kind in Enum.GetValues<RemoteEventKind>().Where(RunLifecycle.IsTerminal))
        {
            var status = RunLifecycle.StatusOf(kind);
            Assert.True(RunLifecycle.IsTerminal(status));
            Assert.Equal(kind.ToString(), status.ToString());
        }
    }

    /// <summary>A kind that is not an ending has no final status, and says so rather than guessing.</summary>
    [Fact]
    public void A_kind_that_is_not_an_ending_has_no_final_status()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RunLifecycle.StatusOf(RemoteEventKind.Progress));

    /// <summary>Every outcome settles into a status, and never into one of the two that are not endings.</summary>
    [Fact]
    public void Every_approval_outcome_settles_into_a_resolved_status()
    {
        foreach (var outcome in Enum.GetValues<ApprovalOutcome>())
        {
            var status = RunLifecycle.StatusOf(outcome);
            Assert.NotEqual(ApprovalStatus.Pending, status);
            Assert.NotEqual(ApprovalStatus.DecisionQueued, status);
            Assert.Equal(outcome.ToString(), status.ToString());
        }
    }

    // ── the wire format ─────────────────────────────────────────────────────

    /// <summary>
    /// Enums travel as names. As numbers they would be smaller and inserting a member in the middle
    /// of an enum would silently renumber every stored payload - and these are stored, in a
    /// gateway's command rows and in a Host's outbox, across upgrades of both ends.
    /// </summary>
    [Fact]
    public void Enums_travel_as_names_and_come_back_unchanged()
    {
        var sent = new HostEvent("e1", "r1", 7, RemoteEventKind.ApprovalResolved,
            Detail: "the owner allowed it",
            Resolution: new ApprovalResolution("a1", "hash", ApprovalOutcome.Allowed));

        var json = RemoteJson.Serialize(sent);

        Assert.Contains("\"ApprovalResolved\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Allowed\"", json, StringComparison.Ordinal);
        Assert.Equal(sent, RemoteJson.Deserialize<HostEvent>(json));
    }

    /// <summary>
    /// A field the reader does not recognise is a message from a version it does not understand.
    /// Ignoring it is how a Host carries out half of an instruction and reports success.
    /// </summary>
    [Fact]
    public void A_field_from_a_version_we_do_not_understand_is_refused()
        => Assert.Throws<JsonException>(() => RemoteJson.Deserialize<CancelRunPayload>(
            """{"runId":"r1","alsoDeleteEverything":true}"""));

    /// <summary>
    /// A start command names a workspace the Host already has. There is no path field, and adding
    /// one is what would turn "run a task remotely" into "read any folder on that machine
    /// remotely" - so its absence is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void A_start_command_cannot_name_a_folder()
    {
        var names = typeof(StartTaskPayload).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(names, n => n.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Root", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Directory", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("WorkspaceId", names);
    }
}
