namespace Enactive.Engine.Tests;

using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The gate a corpus of recorded refusals is held to in CI. A count printed and never compared is a
/// number nobody reads: it passes at 0/0, and it passes the day a change makes the validators refuse
/// what they used to accept.
///
/// <para>So each corpus folder carries <c>expected.json</c>, one line per case: <c>"accepted"</c> or
/// <c>"refused"</c> by today's validators. It is a ratchet, and it only turns one way unless someone
/// edits it on purpose:</para>
/// <list type="bullet">
/// <item>a case expected accepted that is refused now is a <b>regression</b>;</item>
/// <item>a case expected refused that is accepted now is an improvement that must be <b>locked in</b>
/// by editing the manifest - otherwise the next change could quietly lose it;</item>
/// <item>a case file the manifest does not name, or a name with no file, fails: adding or dropping a
/// case is a decision, and it is made where a reviewer of the change sees it;</item>
/// <item>an empty corpus fails: a gate over nothing measures nothing.</item>
/// </list>
/// </summary>
internal static class CorpusRatchet
{
    internal const string Manifest = "expected.json";
    internal const string Accepted = "accepted";
    internal const string Refused = "refused";

    /// <summary>Everything wrong with <paramref name="now"/> against <paramref name="expected"/>, in words a person can act on.</summary>
    internal static List<string> Problems(IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string?> now)
    {
        var problems = new List<string>();
        if (now.Count == 0)
            problems.Add("The corpus is empty. A gate over no cases passes whatever the validators do.");

        foreach (var (name, refusal) in now.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var state = refusal is null ? Accepted : Refused;
            if (!expected.TryGetValue(name, out var was))
                problems.Add($"{name}: not in {Manifest}. It is {state} now - add it as \"{state}\".");
            else if (was is not (Accepted or Refused))
                problems.Add($"{name}: {Manifest} says \"{was}\"; the only states are \"{Accepted}\" and \"{Refused}\".");
            else if (was == Accepted && refusal is not null)
                problems.Add($"{name}: REGRESSION - accepted before, refused now: {refusal}");
            else if (was == Refused && refusal is null)
                problems.Add($"{name}: accepted now. Lock the improvement in: set it to \"{Accepted}\" in {Manifest}.");
        }

        foreach (var name in expected.Keys.Where(k => !now.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            problems.Add($"{name}: in {Manifest}, but there is no such case. Remove it from the manifest if dropping it was meant.");

        return problems;
    }

    /// <summary>Replays every case in <paramref name="folder"/> and fails on anything <see cref="Problems"/> finds.</summary>
    internal static void Hold(string folder, Func<string, string?> replay, ITestOutputHelper output)
    {
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.json")
                .Where(f => !string.Equals(Path.GetFileName(f), Manifest, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : [];
        var manifest = Path.Combine(folder, Manifest);
        var expected = File.Exists(manifest)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest)) ?? []
            : [];

        var now = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var refusal = replay(path);
            now[Path.GetFileName(path)] = refusal;
            output.WriteLine($"{Path.GetFileName(path)}: " + (refusal is null ? "ACCEPTED" : "refused: " + refusal));
        }

        var accepted = now.Values.Count(v => v is null);
        output.WriteLine($"\n{accepted}/{now.Count} recorded refusals are accepted by today's validators"
            + $" (manifest expects {expected.Values.Count(v => v == Accepted)}).");

        var problems = Problems(expected, now);
        if (problems.Count > 0)
            Assert.Fail($"{folder}\n" + string.Join("\n", problems));
    }

    internal static string RepositoryFolder(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Enactive.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "Enactive.Engine.Tests", name);
    }
}

/// <summary>The gate itself, tested - a gate that cannot fail is the defect this exists to fix.</summary>
public sealed class CorpusRatchetTests
{
    private static Dictionary<string, string?> Now(params (string Name, string? Refusal)[] cases)
        => cases.ToDictionary(c => c.Name, c => c.Refusal, StringComparer.Ordinal);

    [Fact]
    public void An_empty_corpus_does_not_pass()
        => Assert.Contains(CorpusRatchet.Problems(new Dictionary<string, string>(), Now()),
            p => p.Contains("empty", StringComparison.Ordinal));

    [Fact]
    public void A_case_that_was_accepted_and_is_refused_now_is_a_regression()
        => Assert.Contains(CorpusRatchet.Problems(new Dictionary<string, string> { ["a.json"] = "accepted" },
                Now(("a.json", "missing field"))),
            p => p.Contains("REGRESSION", StringComparison.Ordinal) && p.Contains("missing field", StringComparison.Ordinal));

    [Fact]
    public void An_improvement_has_to_be_locked_in()
        => Assert.Contains(CorpusRatchet.Problems(new Dictionary<string, string> { ["a.json"] = "refused" },
                Now(("a.json", null))),
            p => p.Contains("Lock the improvement in", StringComparison.Ordinal));

    [Fact]
    public void A_case_the_manifest_does_not_name_or_a_name_with_no_case_fails()
    {
        var problems = CorpusRatchet.Problems(new Dictionary<string, string> { ["gone.json"] = "refused" },
            Now(("new.json", "x")));
        Assert.Contains(problems, p => p.StartsWith("new.json: not in", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("gone.json: in", StringComparison.Ordinal));
    }

    [Fact]
    public void A_corpus_as_the_manifest_says_passes()
        => Assert.Empty(CorpusRatchet.Problems(
            new Dictionary<string, string> { ["a.json"] = "refused", ["b.json"] = "accepted" },
            Now(("a.json", "still wrong"), ("b.json", null))));
}
