namespace Enactive.Server.Tests.Components;

using System.Globalization;
using System.Text.RegularExpressions;
using Enactive.Application.Projections;
using Enactive.Core.History;
using Enactive.Providers;
using Enactive.Server.Components;
using Enactive.Server.Components.Runs;
using Enactive.Workspace;
using Xunit.Sdk;

/// <summary>
/// Looks is the web's Palette: which state is drawn in which colour. These hold it to the window's.
///
/// <para>The server must never load a desktop toolkit, so the window's palette is not referenced: Brand.cs and Palette.cs
/// are read as TEXT. That relies on how they are written - a colour is a <c>Color.Parse("#RRGGBB")</c> or another Brand
/// member (<c>Of(Ink300)</c>, <c>Info</c>), and a Palette arm is <c>Type.Value =&gt; Brand.Member</c>. A refactor that
/// changes either shape fails here with a message that says which line it could not read, rather than passing on
/// nothing.</para>
/// </summary>
public sealed class LooksTests
{
    // ── every value has a look ──────────────────────────────────────────────

    [Fact]
    public void Every_value_of_every_state_has_a_look()
    {
        var classes = new List<string>();
        classes.AddRange(Enum.GetValues<FeedCardStatus>().Select(Looks.Card));
        classes.AddRange(Enum.GetValues<RunStandingKind>().Select(Looks.Run));
        classes.AddRange(Enum.GetValues<TimelineTone>().Select(Looks.Timeline));
        classes.AddRange(Enum.GetValues<ModelReach>().Select(Looks.Reach));
        classes.AddRange(Enum.GetValues<CiState>().Select(Looks.Ci));
        classes.AddRange(Enum.GetValues<ProviderHealth>().Select(Looks.Health));
        classes.AddRange(Enumerable.Range(0, 4).Select(Looks.Autonomy));
        classes.AddRange(Enum.GetValues<WorkspacePlace>().Select(Looks.Workspace));
        classes.Add(Looks.Change(applied: false, rejected: false));
        classes.Add(Looks.Change(applied: true, rejected: false));
        classes.Add(Looks.Change(applied: false, rejected: true));

        Assert.All(classes, c => Assert.Matches(@"^(tone|tier|edge)-[\w-]+$", c));
        // A class Looks hands out that the stylesheet does not define draws as no colour at all.
        var undefined = classes.Distinct().Where(c => !ToneTokens.Value.ContainsKey(c)).ToList();
        Assert.True(undefined.Count == 0, $"theme.css defines no rule for: {string.Join(", ", undefined)}");
    }

    [Fact]
    public void The_classes_are_the_ones_the_pages_ask_for()
    {
        Assert.Equal("tone-running", Looks.Card(FeedCardStatus.Running));
        Assert.Equal("tone-failed", Looks.Run(RunStandingKind.Failed));
        Assert.Equal("tier-3", Looks.Autonomy(3));
    }

    [Fact]
    public void A_value_not_named_is_refused_not_drawn_in_a_fallback()
    {
        // A new state that quietly drew in a fallback colour is the state nobody noticed was new (as in Palette.cs).
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Card((FeedCardStatus)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Run((RunStandingKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Timeline((TimelineTone)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Reach((ModelReach)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Ci((CiState)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Health((ProviderHealth)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Workspace((WorkspacePlace)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Autonomy(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Autonomy(-1));
        // Applied and rejected at once is not a state a change is ever in.
        Assert.Throws<ArgumentOutOfRangeException>(() => Looks.Change(applied: true, rejected: true));
    }

    // ── and it is the window's look ─────────────────────────────────────────

    [Fact]
    public void The_looks_are_the_window_s()
    {
        var rows = new List<(string State, string Class, string Window)>();
        rows.AddRange(Rows(Looks.Card, new Dictionary<FeedCardStatus, string>
        {
            [FeedCardStatus.Pending] = "CardTone.Pending",
            [FeedCardStatus.Running] = "CardTone.Running",
            [FeedCardStatus.Done] = "CardTone.Done",
            [FeedCardStatus.Failed] = "CardTone.Failed",
            [FeedCardStatus.Skipped] = "CardTone.Skipped",
            [FeedCardStatus.Unverified] = "CardTone.Unverified",
            // The window draws a blocked step as one that waits for a person (StepCardViewModel.ToneOf).
            [FeedCardStatus.Blocked] = "CardTone.NeedsYou"
        }));
        rows.AddRange(Rows(Looks.Run, new Dictionary<RunStandingKind, string>
        {
            // RunEdge names Done, Failed and Idle; every other word, a run in progress included, takes its amber.
            [RunStandingKind.Running] = RunEdgeOther,
            [RunStandingKind.Done] = "RunStandingKind.Done",
            [RunStandingKind.Failed] = "RunStandingKind.Failed",
            [RunStandingKind.Open] = RunEdgeOther,
            [RunStandingKind.Idle] = "RunStandingKind.Idle"
        }));
        rows.AddRange(Rows(Looks.Timeline, new Dictionary<TimelineTone, string>
        {
            [TimelineTone.Failed] = "TimelineTone.Failed",
            [TimelineTone.Refused] = "TimelineTone.Refused",
            [TimelineTone.Succeeded] = "TimelineTone.Succeeded",
            [TimelineTone.Quiet] = "TimelineTone.Quiet",
            [TimelineTone.Plain] = "TimelineTone.Plain"
        }));
        rows.AddRange(Rows(Looks.Reach, new Dictionary<ModelReach, string>
        {
            [ModelReach.Local] = "ModelReach.Local",
            [ModelReach.Cloud] = "ModelReach.Cloud",
            [ModelReach.Unknown] = "ModelReach.Unknown"
        }));
        rows.AddRange(Rows(Looks.Ci, new Dictionary<CiState, string>
        {
            [CiState.Passing] = "CiState.Passing",
            [CiState.Failing] = "CiState.Failing",
            [CiState.Pending] = "CiState.Pending",
            [CiState.None] = "CiState.None"
        }));
        rows.AddRange(Rows(Looks.Health, new Dictionary<ProviderHealth, string>
        {
            [ProviderHealth.Ready] = "ProviderHealth.Ready",
            [ProviderHealth.ModelMissing] = "ProviderHealth.ModelMissing",
            [ProviderHealth.Unreachable] = "ProviderHealth.Unreachable",
            [ProviderHealth.Unknown] = "ProviderHealth.Unknown",
            [ProviderHealth.Checking] = "ProviderHealth.Checking"
        }));

        rows.AddRange(Rows(Looks.Workspace, new Dictionary<WorkspacePlace, string>
        {
            [WorkspacePlace.Current] = "WorkspacePlace.Current",
            [WorkspacePlace.Other] = "WorkspacePlace.Other",
            [WorkspacePlace.Missing] = "WorkspacePlace.Missing"
        }));

        // The change is two flags here and an enum in the window: staged and refused wait for a person, then what was done.
        rows.Add(("Change.Staged", Looks.Change(false, false), WindowHex("ChangeState.Staged")));
        rows.Add(("Change.Applied", Looks.Change(true, false), WindowHex("ChangeState.Applied")));
        rows.Add(("Change.Rejected", Looks.Change(false, true), WindowHex("ChangeState.Rejected")));

        // The autonomy scale is Brand.Autonomy(level), not an arm of Palette.
        string[] tiers = ["AutonomyObserve", "AutonomySuggest", "AutonomyExecute", "AutonomyAutonomous"];
        for (var level = 0; level < tiers.Length; level++)
            rows.Add(($"Autonomy.{level}", Looks.Autonomy(level), BrandHex(tiers[level])));

        var differ = rows
            .Where(r => ToneHex(r.Class) != r.Window)
            .Select(r => $"{r.State}: {r.Class} is {ToneHex(r.Class)} in theme.css, {r.Window} in the window");
        Assert.Empty(differ);
    }

    [Fact]
    public void The_tokens_are_the_brand_s()
    {
        // theme.css names a token for a Brand member the way brand.css does: ink-300 is Ink300; the semantic four carry
        // "Color" in Brand.cs, and info-light is InfoLight.
        string[] named =
        [
            "ink-950", "ink-900", "ink-800", "ink-700", "ink-600", "ink-500", "ink-400", "ink-300", "ink-200", "ink-100", "ink-50",
            "ember-200", "ember-300", "ember-400", "ember-500", "ember-600", "ember-700", "ember-800", "amber-400",
            "success", "warning", "danger", "info", "info-light", "go-500", "go-600", "go-700", "decision-fill"
        ];

        var wrong = new List<string>();
        foreach (var token in named)
        {
            if (!Tokens.Value.TryGetValue(token, out var css))
            {
                wrong.Add($"--{token} is not in theme.css");
                continue;
            }
            var brand = BrandHex(BrandMemberOf(token));
            if (css != brand)
                wrong.Add($"--{token} is {css} in theme.css, {brand} in Brand.{BrandMemberOf(token)}");
        }
        Assert.Empty(wrong);
    }

    [Fact]
    public void The_translucent_fills_are_the_brand_s()
    {
        // White or black at an alpha: Brand.cs writes them as ImmutableSolidColorBrush(Colors.White, 0.10), theme.css as
        // rgb(255 255 255 / 10%).
        var brand = Regex.Matches(File.ReadAllText(RepoRoot.Source("src", "Enactive.App.Ui", "Brand.cs")),
                @"(?<name>CardFill|CardFillHover|CardFillActive|InputFill|InputFillHover|Scrim)\s*=\s*new ImmutableSolidColorBrush\(Colors\.(?<c>White|Black),\s*(?<a>[\d.]+)\)")
            .ToDictionary(m => m.Groups["name"].Value, m => (Channel: m.Groups["c"].Value == "White" ? 255 : 0,
                Percent: (int)Math.Round(double.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture) * 100)));
        var tokens = new Dictionary<string, string>
        {
            ["CardFill"] = "card-fill", ["CardFillHover"] = "card-fill-hover", ["CardFillActive"] = "card-fill-active",
            ["InputFill"] = "input-fill", ["InputFillHover"] = "input-fill-hover", ["Scrim"] = "scrim"
        };
        Assert.Equal(tokens.Keys.Order(), brand.Keys.Order());

        var css = Regex.Matches(Theme.Value, @"--(?<t>[\w-]+):\s*rgb\((?<c>\d+) \k<c> \k<c> / (?<p>\d+)%\)")
            .ToDictionary(m => m.Groups["t"].Value, m => (Channel: int.Parse(m.Groups["c"].Value, CultureInfo.InvariantCulture),
                Percent: int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture)));

        Assert.Empty(tokens.Where(t => !css.TryGetValue(t.Value, out var seen) || seen != brand[t.Key])
            .Select(t => $"--{t.Value} is not {brand[t.Key]} in theme.css"));
    }

    // ── reading the window's palette as text ────────────────────────────────

    private const string RunEdgeOther = "RunEdge._";

    private static IEnumerable<(string State, string Class, string Window)> Rows<T>(Func<T, string> look,
        IReadOnlyDictionary<T, string> window) where T : struct, Enum
        => Enum.GetValues<T>().Select(value => ($"{typeof(T).Name}.{value}", look(value), window.TryGetValue(value, out var key)
            ? WindowHex(key)
            : throw new XunitException($"{typeof(T).Name}.{value} has no desktop equivalent named in this test: add it, with the Palette.cs arm it follows.")));

    private static readonly Lazy<string> Theme = new(() => File.ReadAllText(RepoRoot.Source("src", "Enactive.Server", "wwwroot", "theme.css")));

    /// <summary>Every <c>--name: #RRGGBB;</c> in theme.css (the first of a name, which is the :root block's).</summary>
    private static readonly Lazy<Dictionary<string, string>> Tokens = new(() =>
    {
        var found = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(Theme.Value, @"--(?<name>[\w-]+):\s*(?<hex>#[0-9A-Fa-f]{6})\s*;"))
            found.TryAdd(m.Groups["name"].Value, m.Groups["hex"].Value.ToUpperInvariant());
        return found;
    });

    /// <summary>The token each state class sets: <c>.tone-done { --tone: var(--success); }</c>, one rule on one line.</summary>
    private static readonly Lazy<Dictionary<string, string>> ToneTokens = new(() =>
        Regex.Matches(Theme.Value, @"(?m)^\.(?<class>(?:tone|tier|edge)-[\w-]+)\s*\{\s*--tone:\s*var\(--(?<token>[\w-]+)\)\s*;\s*\}")
            .ToDictionary(m => m.Groups["class"].Value, m => m.Groups["token"].Value));

    private static string ToneHex(string cssClass)
        => ToneTokens.Value.TryGetValue(cssClass, out var token)
            ? Tokens.Value.TryGetValue(token, out var hex) ? hex : $"(no #RRGGBB for --{token})"
            : $"(no .{cssClass} rule)";

    /// <summary>Brand.cs: <c>public static readonly Type Name = expression;</c> - the expression up to its semicolon.</summary>
    private static readonly Lazy<Dictionary<string, string>> BrandMembers = new(() =>
        Regex.Matches(File.ReadAllText(RepoRoot.Source("src", "Enactive.App.Ui", "Brand.cs")),
                @"public static readonly \w+ (?<name>\w+) = (?<value>[^;]+);")
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value));

    /// <summary>A Brand member's colour: its own <c>Color.Parse("#hex")</c>, or the colour of the member it names.</summary>
    private static string BrandHex(string member)
    {
        if (!BrandMembers.Value.TryGetValue(member, out var expression))
            throw new XunitException($"Brand.cs has no member {member} in the shape this test reads.");

        var parsed = Regex.Match(expression, "Color\\.Parse\\(\"(?<hex>#[0-9A-Fa-f]{6})\"\\)");
        if (parsed.Success)
            return parsed.Groups["hex"].Value.ToUpperInvariant();

        var named = Regex.Match(expression, @"^\s*(?:Of\()?\s*(?<name>\w+)\s*\)?\s*$");
        return named.Success
            ? BrandHex(named.Groups["name"].Value)
            : throw new XunitException($"Brand.{member} = {expression.Trim()} is neither a Color.Parse nor another member.");
    }

    /// <summary>theme.css names a token as brand.css does; Brand.cs adds "Color" to the four semantic ones.</summary>
    private static string BrandMemberOf(string token) => token switch
    {
        "success" or "warning" or "danger" or "info" => char.ToUpperInvariant(token[0]) + token[1..] + "Color",
        _ => string.Concat(token.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]))
    };

    /// <summary>Palette.cs arms: <c>[Ns.]Type.Value [or Type.Value] =&gt; Brand.Member</c>, keyed "Type.Value".</summary>
    private static readonly Lazy<(Dictionary<string, string> Arms, string Other)> Palette = new(() =>
    {
        var text = File.ReadAllText(RepoRoot.Source("src", "Enactive.App.Ui", "Palette.cs"));
        var arms = new Dictionary<string, string>();
        foreach (Match arm in Regex.Matches(text, @"(?<left>[\w.]+(?:\s+or\s+[\w.]+)*)\s*=>\s*Brand\.(?<member>\w+)"))
            foreach (var key in Regex.Split(arm.Groups["left"].Value, @"\s+or\s+").Select(k => k.Split('.')).Where(k => k.Length >= 2))
                arms[string.Join('.', key[^2..])] = arm.Groups["member"].Value; // a lone `_` is the wildcard, read below

        var runEdge = text.IndexOf("RunEdge =", StringComparison.Ordinal);
        var other = Regex.Match(text[runEdge..], @"_\s*=>\s*Brand\.(?<member>\w+)");
        return runEdge >= 0 && other.Success
            ? (arms, other.Groups["member"].Value)
            : throw new XunitException("Palette.RunEdge has no `_ => Brand.X` arm in the shape this test reads.");
    });

    private static string WindowHex(string key)
        => BrandHex(key == RunEdgeOther
            ? Palette.Value.Other
            : Palette.Value.Arms.TryGetValue(key, out var member)
                ? member
                : throw new XunitException($"Palette.cs has no arm for {key}."));
}
