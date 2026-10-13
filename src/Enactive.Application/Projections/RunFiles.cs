namespace Enactive.Application.Projections;

using Enactive.Core.Events;

/// <summary>What one event did to a run's files, for a host to draw: nothing, a file written, a change proposed, or files put back.</summary>
internal abstract record FileNews
{
    private FileNews() { }

    /// <summary>The event said nothing new about the run's files - another kind, or a repeated write to a known path.</summary>
    public sealed record None : FileNews
    {
        public static readonly None Instance = new();
    }

    /// <summary>The run wrote a file it had not written before: a card that reviews it and can undo it.</summary>
    public sealed record Written(string Path) : FileNews;

    /// <summary>
    /// The run proposed a change instead of writing it (the folder stages): it is in the staging store, to apply or
    /// reject, and the file on disk is unchanged. Told for every such event, a repeated path too - the store keeps one
    /// change per event, and the window shows each of them.
    /// </summary>
    public sealed record Staged(string Path) : FileNews;

    /// <summary>A rejected step's files were put back: the cards for <see cref="Paths"/> describe a file that is no longer there.</summary>
    public sealed record Reverted(IReadOnlyList<string> Paths) : FileNews;
}

/// <summary>
/// A run's files as its events say them - written, proposed, put back - in ONE place.
///
/// <para>The window decided this in its own switch: a staging run's ArtifactProduced was a proposal, not a file; an
/// ArtifactReverted marked the cards put back and took their actions away. The service's live page read only "a path was
/// produced" (RunPanel.ArtifactPaths), so a staged proposal and a put-back file were both listed as files the run wrote,
/// offering Review - which showed the file on disk, unchanged - and Delete - which deleted the person's own file.</para>
/// </summary>
internal sealed class RunFiles
{
    /// <summary>What a card for a put-back file says, in place of its actions.</summary>
    public const string PutBackStatus = "put back — the reviewer rejected this step";

    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _writtenPaths = new();
    private readonly HashSet<string> _staged = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _stagedPaths = new();
    private readonly HashSet<string> _reverted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The run stages its changes - what it produces is proposed, not written. Known only once the run is composed (the
    /// composer decides it), which is before its first event: set then, never after.
    /// </summary>
    public bool Stages { get; set; }

    /// <summary>Files the run wrote, one per path (case-insensitive), in the order first written - put-back ones too.</summary>
    public IReadOnlyList<string> Written => _writtenPaths;

    /// <summary>Changes the run proposed, one per path, in the order first proposed.</summary>
    public IReadOnlyList<string> Staged => _stagedPaths;

    /// <summary>The written files a rejected step put back, in the order they were written.</summary>
    public IReadOnlyList<string> Reverted => _writtenPaths.Where(_reverted.Contains).ToList();

    /// <summary>Whether a written file was put back.</summary>
    public bool IsReverted(string path) => _reverted.Contains(path);

    /// <summary>Folds one event in, and says what it did to the run's files.</summary>
    public FileNews Apply(WorkEvent ev)
    {
        switch (ev.Kind)
        {
            case EventKind.ArtifactProduced:
                var path = PathOf(ev);
                if (Stages)
                {
                    if (_staged.Add(path))
                        _stagedPaths.Add(path);
                    return new FileNews.Staged(path);
                }
                // One card per file: a repeated write to a known path says nothing new.
                if (!_written.Add(path))
                    return FileNews.None.Instance;
                _writtenPaths.Add(path);
                return new FileNews.Written(path);

            case EventKind.ArtifactReverted:
                // The paths as values (WorkEventPayload.RevertPayload): read out of the sentence, the line about files the
                // review found right - "NOT put back: a.md" - marked a.md put back while it stayed.
                var paths = ev.RevertedPaths() ?? [];
                foreach (var reverted in paths)
                    if (_written.Contains(reverted))
                        _reverted.Add(reverted);
                return new FileNews.Reverted(paths);

            default:
                return FileNews.None.Instance;
        }
    }

    /// <summary>
    /// The path, with the fallback for a run recorded before the payload named one: the text after the first ": " in the
    /// summary, else the summary itself.
    /// </summary>
    private static string PathOf(WorkEvent ev)
        => ev.ArtifactPath()
           ?? (ev.Summary.Contains(": ", StringComparison.Ordinal)
               ? ev.Summary[(ev.Summary.IndexOf(": ", StringComparison.Ordinal) + 2)..]
               : ev.Summary);
}
