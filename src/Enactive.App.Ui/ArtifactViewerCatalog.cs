namespace Enactive.App.Ui;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Media;
using Enactive.Core.Text;

/// <summary>
/// One way of showing a file the app has opened.
///
/// <para>Implement this in an assembly, drop it in <see cref="ViewerPlugins.Folder"/>, and the
/// next start will use it. The app ships two — see <see cref="ArtifactViewerCatalog"/> — and both
/// are written against this same interface, so there is no privileged kind.</para>
/// </summary>
public interface IArtifactViewer
{
    /// <summary>
    /// What this viewer is called. Two viewers with the same id are the same viewer, and the one
    /// registered last is the one used — which is how a plugin replaces a built-in.
    /// </summary>
    string Id { get; }

    /// <summary>The file extensions it claims, with or without the leading dot.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// The control that shows this content. Never scrolls itself: the window puts it in a scroll
    /// viewer, so a viewer that brought its own would nest two.
    /// </summary>
    Control Build(string content);
}

/// <summary>
/// The viewers the app knows about, built in and loaded.
///
/// <para><b>Reading is the deliverable.</b> A run's whole output is often one Markdown file, and
/// until now every one of them was shown as monospaced source in a box that does not wrap —
/// the same box used for an environment probe, because that box was all there was. The renderer
/// for it already existed and had been sitting in the log analysis window since it was written.
/// </para>
///
/// <para><b>A renderer is never the only way to see a file.</b> The log analysis window learnt
/// this first and says why: <i>"a renderer that handles a subset must never be the only way to see
/// the answer: anything it did not understand is readable here"</i>. So the window keeps its
/// Source toggle for every file type, and this catalog only decides what the OTHER tab shows.
/// </para>
/// </summary>
public static class ArtifactViewerCatalog
{
    private static readonly object Gate = new();
    private static Dictionary<string, IArtifactViewer>? _byId;
    private static ViewerRegistry? _registry;

    /// <summary>What went wrong while loading plugins, for a window that wants to say so.</summary>
    public static IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    /// <summary>The viewer for this path — never null; plain text is a real answer, not a failure.</summary>
    public static IArtifactViewer For(string? relativePath)
    {
        Ensure();
        var id = _registry!.Choose(relativePath);
        return _byId!.TryGetValue(id, out var viewer) ? viewer : _byId[ViewerRegistry.PlainText];
    }

    /// <summary>Whether this file gets anything more than its own text.</summary>
    public static bool HasRenderer(string? relativePath)
    {
        Ensure();
        return _registry!.Choose(relativePath) != ViewerRegistry.PlainText;
    }

    private static void Ensure()
    {
        lock (Gate)
        {
            if (_byId is not null)
                return;

            var byId = new Dictionary<string, IArtifactViewer>(StringComparer.OrdinalIgnoreCase);
            var registry = ViewerRegistry.Builtin();
            var problems = new List<string>();

            foreach (var viewer in new IArtifactViewer[] { new PlainTextViewer(), new MarkdownViewer() })
                byId[viewer.Id] = viewer;

            // After the built-ins, so a plugin claiming .md wins - see ViewerRegistry.Register.
            foreach (var viewer in Load(problems))
            {
                byId[viewer.Id] = viewer;
                registry.Register(viewer.Id, viewer.Extensions.ToArray());
            }

            _byId = byId;
            _registry = registry;
            Problems = problems;
        }
    }

    /// <summary>
    /// Every viewer in <see cref="ViewerPlugins.Folder"/>.
    ///
    /// <para><b>Nothing is loaded from a workspace, ever.</b> The folder is fixed, beside the
    /// app's settings, and takes a path from nowhere — see the note on
    /// <see cref="ViewerPlugins.Folder"/> for why that is the whole of this feature's security.
    /// </para>
    ///
    /// <para><b>A bad plugin must not take the window with it.</b> A folder that is not there is
    /// the normal case and says nothing. An assembly that will not load, or a viewer whose
    /// constructor throws, is recorded in <see cref="Problems"/> and skipped: the person opened a
    /// file and is entitled to see it, whatever somebody's DLL did.</para>
    /// </summary>
    private static IEnumerable<IArtifactViewer> Load(List<string> problems)
    {
        var folder = ViewerPlugins.Folder;

        string[] files;
        try
        {
            if (!Directory.Exists(folder))
                yield break;

            files = Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            problems.Add($"Could not read the viewer folder: {ex.Message}");
            yield break;
        }

        foreach (var file in files)
        {
            List<IArtifactViewer> found;
            try
            {
                found = FromAssembly(file);
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                continue;
            }

            foreach (var viewer in found)
                yield return viewer;
        }
    }

    private static List<IArtifactViewer> FromAssembly(string path)
    {
        // Its own context, named for the file, so two plugins that depend on different versions of
        // the same library do not have to agree with each other. Not collectible: a viewer stays in
        // use for as long as the window is open, and unloading is a problem nobody has yet.
        var context = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(path));
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));

        var viewers = new List<IArtifactViewer>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(IArtifactViewer).IsAssignableFrom(type))
                continue;

            if (type.GetConstructor(Type.EmptyTypes) is null)
                continue;

            if (Activator.CreateInstance(type) is IArtifactViewer viewer
                && !string.IsNullOrWhiteSpace(viewer.Id))
                viewers.Add(viewer);
        }

        return viewers;
    }
}

/// <summary>The file as it is, which is the right answer for most of what a run writes.</summary>
internal sealed class PlainTextViewer : IArtifactViewer
{
    public string Id => ViewerRegistry.PlainText;

    public IReadOnlyList<string> Extensions => Array.Empty<string>();

    public Control Build(string content) => new TextBox
    {
        Text = content,
        IsReadOnly = true,
        AcceptsReturn = true,

        // Not wrapped, like the box this replaced: these are often reports whose columns mean
        // something, and re-flowing them loses the alignment that carries the meaning.
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Menlo, monospace"),
        FontSize = 12
    };
}

/// <summary>
/// Markdown, through the renderer the log analysis window has used since it was written. The same
/// renderer, not a second one: a heading that looks one way in an analysis and another way in an
/// artifact would be two answers to one question.
/// </summary>
internal sealed class MarkdownViewer : IArtifactViewer
{
    public string Id => ViewerRegistry.Markdown;

    public IReadOnlyList<string> Extensions => new[] { ".md", ".markdown" };

    public Control Build(string content) => MarkdownRender.Build(content);
}
