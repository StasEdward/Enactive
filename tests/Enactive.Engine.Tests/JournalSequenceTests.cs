namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Artifacts;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The journal put under SEQUENCES of operations rather than one scenario at a time.
///
/// <para>This exists because of how the five defects in <see cref="RevertIntegrityTests"/> were
/// found: an outside reviewer wrote down orders of operations nobody had tried, and all five fell
/// out at once while a suite of 742 stayed green. Each needed two things to happen in a particular
/// order, and every test until then did one thing. Writing five more scenarios by hand would find
/// the sixth the same way — by luck.</para>
///
/// <para>So this generates them. Seeded, so a failure is reproducible and printable; bounded, so it
/// runs in the time an ordinary test may take. It asserts nothing about any particular sequence —
/// only two properties that must hold after every operation of every sequence:</para>
///
/// <list type="number">
/// <item>A file never holds content written by a scope that has since been reverted — UNLESS that
/// revert reported the path as <c>Kept</c>. The exemption is not a weakening: refusing to touch a
/// file another scope has written since is the honest outcome N1 was fixed to give, and a store that
/// says so out loud has kept its promise. Stated the first way, this generator failed on two seeds
/// against correct behaviour; the mistake was in the invariant, and finding that is what a
/// generator is for.</item>
/// <item>Every path a revert was ASKED about comes back in <c>Reverted</c> or in <c>Kept</c> — never
/// in neither. That is the promise N2 broke, and the reason it broke silently.</item>
/// <item>The converse of the first, and the one N1 actually breaks: a write nobody has reverted is
/// still on disk. N1 destroyed an ACCEPTED write — the file came back holding the original, which
/// is nobody's content, so an invariant phrased only as "no reverted scope's work survives" walks
/// straight past it. Both halves are needed, and finding that out cost a pass where this generator
/// stayed green against the defect it was written for.</item>
/// </list>
///
/// <para>When it fails it prints the sequence that broke it, so the outcome is a scenario to add to
/// <see cref="RevertIntegrityTests"/> by hand — not a mystery to reproduce.</para>
/// </summary>
public sealed class JournalSequenceTests
{
    private readonly ITestOutputHelper _output;

    public JournalSequenceTests(ITestOutputHelper output) => _output = output;

    // Three scopes over three files is the smallest world that can express all five findings:
    // interleaving needs two owners on one path, and the checkpoint defect needs a third scope
    // whose entries sit behind another's.
    private static readonly string[] Paths = { "one.txt", "nested/two.txt", "three.bin" };

    // The same file under several spellings. A model produces these in consecutive tool calls as a
    // matter of course, and to the store they must all be one file.
    private static string Spell(string path, int variant) => variant switch
    {
        0 => path,
        1 => "./" + path,
        2 => path.Replace('/', '\\'),
        _ => path,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task No_sequence_of_writes_and_reverts_loses_an_accepted_write(int seed)
    {
        var random = new Random(seed);
        var log = new StringBuilder();

        using var fixture = new EngineFixture();
        foreach (var path in Paths)
            fixture.Write(path, "original " + path);

        var scopes = new List<(int Index, IArtifactScope Scope)>();
        var reverted = new HashSet<int>();

        // Paths a revert declined to touch. It said so, which is a correct outcome (see the class
        // comment), and from that point this test has nothing left to predict about them.
        var declined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The stack of versions each path has been through, oldest first. This is the model the
        // store is judged against, and it has to be a STACK: an earlier attempt to hold only "who
        // wrote last" could not see the defect this generator exists for, because by the time
        // A → B → A is reverted, B's content has already been overwritten by A's second write and
        // "who wrote last" says A either way. What is lost is the version A's revert should have
        // uncovered.
        var history = Paths.ToDictionary(
            path => path,
            path => new List<(int Owner, string Content)> { (-1, "original " + path) },
            StringComparer.OrdinalIgnoreCase);

        for (var move = 0; move < 120; move++)
        {
            var choice = random.Next(100);

            if (scopes.Count == 0 || choice < 25)
            {
                var index = scopes.Count;
                scopes.Add((index, fixture.Artifacts.BeginStep()));
                log.AppendLine($"open scope {index}");
                continue;
            }

            var (owner, scope) = scopes[random.Next(scopes.Count)];
            var path = Paths[random.Next(Paths.Length)];

            if (choice < 80)
            {
                if (reverted.Contains(owner))
                    continue;   // a rejected step does not go on writing

                var spelling = Spell(path, random.Next(3));
                var content = $"scope {owner} wrote {path} at move {move}";
                await scope.CreateAsync(
                    spelling, ArtifactKind.FileSet, spelling,
                    async output => await output.WriteAsync(Encoding.UTF8.GetBytes(content)),
                    default);

                history[path].Add((owner, content));
                log.AppendLine($"scope {owner} writes {spelling}");
                continue;
            }

            if (reverted.Contains(owner))
                continue;

            var asked = scope.TouchedPaths;
            var report = await scope.RevertAsync(asked, default);
            reverted.Add(owner);
            log.AppendLine($"scope {owner} reverts [{string.Join(", ", asked)}] "
                         + $"-> reverted [{string.Join(", ", report.Reverted)}] "
                         + $"kept [{string.Join(", ", report.Kept)}]");

            // ── Invariant 2 — every asked-about path is accounted for ──
            foreach (var path2 in asked)
                Assert.True(
                    report.Reverted.Contains(path2, StringComparer.OrdinalIgnoreCase)
                    || report.Kept.Contains(path2, StringComparer.OrdinalIgnoreCase),
                    $"'{path2}' was asked about and came back in neither list.\n{log}");

            foreach (var path2 in report.Kept)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(report.WhyKept(path2)),
                    $"'{path2}' was kept with no reason given.\n{log}");

                declined.Add(path2);
            }

            // Reverting a scope removes ITS versions from the stack and nobody else's.
            foreach (var path2 in report.Reverted)
                if (history.TryGetValue(Canonical(path2), out var versions))
                    versions.RemoveAll(v => v.Owner == owner);

            // ── Invariant 1 and its converse — the file holds the top of the stack ──
            //
            // Both halves in one assertion, which is why it is phrased as an equality rather than as
            // two "must not contain" checks. A reverted scope's work must be gone; and the work
            // underneath it — an accepted write by somebody else, or the original file — must be
            // exactly what is uncovered. N1 satisfied the first half while failing the second: it
            // restored the version A's FIRST write displaced, so B's accepted write vanished and the
            // file came back holding content that belonged to nobody.
            foreach (var (path2, versions) in history)
            {
                if (declined.Contains(path2))
                    continue;

                var expected = versions[^1].Content;
                var actual = fixture.Exists(path2) ? fixture.Read(path2) : "(gone)";

                Assert.True(
                    expected == actual,
                    $"'{path2}' should hold \"{expected}\" and holds \"{actual}\".\n{log}");
            }
        }

        _output.WriteLine(log.ToString());
    }

    /// <summary>The path as the history dictionary keys it, whatever spelling came back.</summary>
    private static string Canonical(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    /// <summary>
    /// The same generator with one thing added: an edit made OUTSIDE the store, between operations.
    /// That is a user saving a file mid-run, and the only correct answer to it is a conflict — a
    /// revert must never quietly overwrite it.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public async Task A_users_own_edit_is_never_overwritten_by_a_revert(int seed)
    {
        var random = new Random(seed);
        var log = new StringBuilder();

        using var fixture = new EngineFixture();
        foreach (var path in Paths)
            fixture.Write(path, "original " + path);

        var scopes = new List<IArtifactScope>();
        var edited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var move = 0; move < 80; move++)
        {
            var choice = random.Next(100);

            if (scopes.Count == 0 || choice < 20)
            {
                scopes.Add(fixture.Artifacts.BeginStep());
                log.AppendLine($"open scope {scopes.Count - 1}");
                continue;
            }

            var owner = random.Next(scopes.Count);
            var scope = scopes[owner];
            var path = Paths[random.Next(Paths.Length)];

            if (choice < 60)
            {
                var spelling = Spell(path, random.Next(3));
                await scope.CreateAsync(
                    spelling, ArtifactKind.FileSet, spelling,
                    async output => await output.WriteAsync(
                        Encoding.UTF8.GetBytes($"scope {owner} wrote {path}")),
                    default);
                edited.Remove(path);
                log.AppendLine($"scope {owner} writes {spelling}");
                continue;
            }

            if (choice < 75)
            {
                // The user, in their editor, while the run is going. Bytes that do not survive a
                // text decode, because that is the case a text hash could not see.
                await File.WriteAllBytesAsync(
                    fixture.PathOf(path), new byte[] { 0xFF, (byte)move, 0xFE });
                edited.Add(path);
                log.AppendLine($"THE USER edits {path}");
                continue;
            }

            var report = await scope.RevertAsync(scope.TouchedPaths, default);
            log.AppendLine($"scope {owner} reverts -> reverted [{string.Join(", ", report.Reverted)}] "
                         + $"kept [{string.Join(", ", report.Kept)}]");

            foreach (var path2 in edited)
                Assert.False(
                    report.Reverted.Contains(path2, StringComparer.OrdinalIgnoreCase),
                    $"A revert overwrote the user's own edit to '{path2}'.\n{log}");
        }

        _output.WriteLine(log.ToString());
    }
}
