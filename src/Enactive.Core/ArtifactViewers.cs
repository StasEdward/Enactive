namespace Enactive.Core.Text;

/// <summary>
/// Which viewer shows a file, decided from its name.
///
/// <para><b>Why the decision is here and not in the window.</b> Everything about this that can be
/// WRONG is a matter of names — an extension matched with the wrong case, a folder called
/// <c>docs.v2</c> taken for an extension, a plugin that claims <c>.md</c> and is silently ignored.
/// None of that needs a control, a window or an Avalonia application to test, and the part that
/// does need one — what the file looks like on screen — has nothing in it to get wrong. The same
/// split <c>MarkdownRender</c> already makes against <c>Markdown</c>.</para>
/// </summary>
public sealed class ViewerRegistry
{
    /// <summary>
    /// The viewer for a file nothing else claims: the text itself, as it is on disk.
    ///
    /// <para>It is not a fallback in the apologetic sense. Most of what an agent writes is text and
    /// reading it verbatim is the correct answer; a renderer is the exception, offered where one
    /// exists.</para>
    /// </summary>
    public const string PlainText = "text";

    /// <summary>The viewer for Markdown — the renderer the log analysis window already uses.</summary>
    public const string Markdown = "markdown";

    private readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The registry the app starts with. Everything here is built in; a plugin registers on top.
    /// </summary>
    public static ViewerRegistry Builtin()
    {
        var registry = new ViewerRegistry();
        registry.Register(Markdown, ".md", ".markdown");
        return registry;
    }

    /// <summary>
    /// Claims these extensions for this viewer.
    ///
    /// <para><b>The last registration wins</b>, and that is the point: built-ins are registered
    /// first and a plugin afterwards, so somebody who writes a better Markdown viewer can have it
    /// without editing the app. The alternative — first wins — would make a plugin folder
    /// decoration, since every interesting extension is claimed before it is read.</para>
    ///
    /// <para>A leading dot is optional and an empty extension is ignored: a registry that threw at
    /// a plugin's typo would take the window down with it.</para>
    /// </summary>
    public void Register(string id, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        foreach (var extension in extensions)
        {
            if (string.IsNullOrWhiteSpace(extension))
                continue;

            var key = extension.Trim();
            if (!key.StartsWith('.'))
                key = "." + key;

            if (key.Length > 1)
                _byExtension[key] = id.Trim();
        }
    }

    /// <summary>
    /// The viewer for this path, or <see cref="PlainText"/> when nothing claims it — which
    /// includes a path with no extension, a path that is only an extension, and no path at all.
    /// </summary>
    public string Choose(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return PlainText;

        string extension;
        try
        {
            // Of the FILE. A folder called "docs.v2" is not an extension of the file inside it,
            // and taking the last dot in the whole string would say it was.
            extension = Path.GetExtension(relativePath.AsSpan().TrimEnd()).ToString();
        }
        catch (ArgumentException)
        {
            return PlainText;
        }

        return extension.Length > 1 && _byExtension.TryGetValue(extension, out var id)
            ? id
            : PlainText;
    }

    /// <summary>Every extension claimed, for a window that wants to say what it can render.</summary>
    public IReadOnlyCollection<string> Claimed => _byExtension.Keys.ToArray();
}

/// <summary>Where a viewer that did not ship with the app is loaded from.</summary>
public static class ViewerPlugins
{
    /// <summary>The folder's name under the application's own data directory.</summary>
    public const string FolderName = "viewers";

    /// <summary>
    /// The one folder plugins are loaded from, beside the app's settings and logs.
    ///
    /// <para><b>It is outside every workspace, and that is the whole of its security.</b> A viewer
    /// plugin is code running in this process — the process that holds the provider API keys and
    /// may write anywhere the person has allowed. An agent writes into a workspace by design and
    /// all day long. A plugin folder inside a workspace would therefore be a way for a model to
    /// hand itself arbitrary code execution in its own supervisor, by writing a file, which is
    /// precisely the boundary <see cref="Context.WorkspaceGuard"/> exists to hold.</para>
    ///
    /// <para>So the location is not configurable and does not take a path from anywhere: putting a
    /// DLL here is a deliberate act by the person at the keyboard, in a folder no run can reach.
    /// </para>
    /// </summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Enactive",
        FolderName);
}
