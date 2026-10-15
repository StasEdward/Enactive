namespace Enactive.Server.Tests;

using System.Text.Json;

/// <summary>
/// The files the pages link to, as the build tells a service run from its build output (dotnet run, Visual Studio) to
/// find them: the development manifest names, for every path the service answers, a folder and a file under it.
/// </summary>
public sealed class StaticAssetsTests
{
    /// <summary>
    /// Every file the manifest names is there. The brand, linked in from the gateway's wwwroot, was once recorded as under
    /// the server's own wwwroot, where it is not: /brand/brand.css answered 200 with nothing in it, and every page came
    /// up unstyled with a broken mark. A published service copies the files and did not show it; a run from the build did.
    /// </summary>
    [Fact]
    public void Every_file_the_development_manifest_names_exists()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "Enactive.Server.staticwebassets.runtime.json");
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        var roots = json.RootElement.GetProperty("ContentRoots").EnumerateArray().Select(r => r.GetString()!).ToList();

        var named = new List<(string Route, string File)>();
        Collect(json.RootElement.GetProperty("Root"), "", roots, named);

        Assert.Contains(named, n => n.Route == "brand/brand.css");
        Assert.Empty(named.Where(n => !File.Exists(n.File)).Select(n => $"{n.Route} -> {n.File}"));
    }

    private static void Collect(JsonElement node, string route, IReadOnlyList<string> roots, List<(string, string)> named)
    {
        if (node.TryGetProperty("Asset", out var asset) && asset.ValueKind == JsonValueKind.Object)
            named.Add((route, Path.Combine(roots[asset.GetProperty("ContentRootIndex").GetInt32()],
                asset.GetProperty("SubPath").GetString()!.Replace('/', Path.DirectorySeparatorChar))));

        if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Object)
            foreach (var child in children.EnumerateObject())
                Collect(child.Value, route.Length == 0 ? child.Name : $"{route}/{child.Name}", roots, named);
    }
}
