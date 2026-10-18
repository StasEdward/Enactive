namespace Enactive.Server.Tests.Components;

using System.Text.RegularExpressions;

/// <summary>
/// The pages' look as files: theme.css's two themes, the typefaces it names, and where any of it comes from. The service
/// stands in a LAN that may have no internet, so what a page needs to look as it should is served by the service itself.
/// </summary>
public sealed class ThemeTests
{
    private static readonly string WwwRoot = RepoRoot.Source("src", "Enactive.Server", "wwwroot");

    private static readonly Lazy<string> Theme = new(() => File.ReadAllText(Path.Combine(WwwRoot, "theme.css")));

    /// <summary>
    /// Each typeface the theme asks for first is one fonts.css serves, from a file that is there: named and not served,
    /// the browser drew the page in whatever it had - Segoe on one machine, Times on another.
    /// </summary>
    [Theory]
    [InlineData("font-sans")]
    [InlineData("font-display")]
    [InlineData("font-mono")]
    public void The_typefaces_the_theme_names_are_served_here(string token)
    {
        var family = Regex.Match(Theme.Value, $@"--{token}:\s*'(?<family>[^']+)'").Groups["family"].Value;
        Assert.False(string.IsNullOrEmpty(family), $"--{token} names no quoted family first.");

        var fonts = File.ReadAllText(Path.Combine(WwwRoot, "fonts", "fonts.css"));
        var faces = Regex.Matches(fonts, @"@font-face\s*\{(?<body>[^}]*)\}")
            .Select(m => m.Groups["body"].Value)
            .Where(body => body.Contains($"font-family: '{family}'", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(faces);
        foreach (var face in faces)
        {
            var file = Regex.Match(face, @"url\((?<file>[^)]+)\)").Groups["file"].Value;
            Assert.True(File.Exists(Path.Combine(WwwRoot, "fonts", file)), $"{family}: fonts.css names {file}, which is not in wwwroot/fonts.");
        }
    }

    /// <summary>
    /// Nothing the pages load comes from another host - a font CDN, a script from a package site: in a LAN with no way
    /// out, a page waiting on one drew late or wrong, and a page that loads from a stranger's host is one more thing
    /// that can change under it.
    /// </summary>
    [Fact]
    public void Nothing_the_pages_load_comes_from_another_host()
    {
        var files = Directory.EnumerateFiles(WwwRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".css", StringComparison.Ordinal) || f.EndsWith(".js", StringComparison.Ordinal))
            .Append(RepoRoot.Source("src", "Enactive.Server", "Components", "App.razor"))
            .Append(RepoRoot.Source("src", "Enactive.Remote.Gateway", "wwwroot", "brand", "brand.css"));

        var outside = files
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?:url\(\s*['""]?|@import\s+['""]|(?:src|href)\s*=\s*['""])\s*(?<address>(?:https?:)?//[^'"")\s]+)")
                .Select(m => $"{Path.GetFileName(f)}: {m.Groups["address"].Value}"))
            .ToList();

        Assert.Empty(outside);
    }

    /// <summary>
    /// The light theme only re-points what the dark one defines: a name mistyped there (--ink-90) set a property nothing
    /// reads, and that one surface stayed dark on a light page with nothing to say why.
    /// </summary>
    [Fact]
    public void The_light_theme_re_points_only_what_the_dark_one_defines()
    {
        var dark = Block(":root, :root[data-theme=\"light\"], :root:not([data-theme=\"dark\"]) {");
        var light = Block(":root[data-theme=\"light\"] {");

        var defined = Names(dark);
        var repointed = Names(light);
        Assert.NotEmpty(repointed);
        Assert.Empty(repointed.Except(defined));
    }

    /// <summary>
    /// Words on ember are the ink in both themes - never white: white on ember-500 is 3.0:1, under WCAG AA's 4.5 for a
    /// button's words. The light theme's ink-950 IS white, so a light --on-ember that followed the ink failed again.
    /// </summary>
    [Fact]
    public void Words_on_ember_are_dark_in_both_themes()
    {
        var dark = Regex.Match(Block(":root, :root[data-theme=\"light\"], :root:not([data-theme=\"dark\"]) {"), @"--on-ember:\s*(?<v>[^;]+);");
        var light = Regex.Match(Block(":root[data-theme=\"light\"] {"), @"--on-ember:\s*(?<v>[^;]+);");

        Assert.Equal("var(--ink-950)", dark.Groups["v"].Value.Trim());
        Assert.True(light.Success, "The light theme does not set --on-ember, and the dark one's var(--ink-950) is white there.");
        Assert.Equal("#0A0B0D", light.Groups["v"].Value.Trim(), ignoreCase: true);
    }

    private static string Block(string opening)
    {
        var start = Theme.Value.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, $"theme.css has no block opening with {opening}");
        var end = Theme.Value.IndexOf("\n}", start, StringComparison.Ordinal);
        return Theme.Value[start..end];
    }

    private static HashSet<string> Names(string block)
        => Regex.Matches(block, @"(?m)^\s*--(?<name>[\w-]+)\s*:").Select(m => m.Groups["name"].Value).ToHashSet();
}
