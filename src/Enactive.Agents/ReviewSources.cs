namespace Enactive.Agents;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record ReviewSource(string Id, string Kind, string Label, string VisibleText)
{
    public string[] Fragments { get; } = VisibleText.Replace("\r\n", "\n").Split('\n');
    public string? Fragment(string id) => id.StartsWith('F') && int.TryParse(id.AsSpan(1), out var n)
        && n > 0 && n <= Fragments.Length && id == $"F{n}" && !string.IsNullOrWhiteSpace(Fragments[n - 1])
        ? Fragments[n - 1] : null;
    public string Render() => string.Join("\n", Fragments.Select((text, i) => $"[F{i + 1}] {text}"));
}

internal sealed class ReviewSources
{
    private readonly List<ReviewSource> sources = [];
    private readonly ReviewReferences? references;
    public ReviewSources(string report, ReviewReferences? references = null)
    {
        this.references = references;
        sources.Add(new("worker-report", "worker-report", "Worker message (not a file)", report));
    }
    private string Render(ReviewSource source) => references?.Render(source) ?? source.Render();
    internal static string FileId(string path) => "file-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    public string RenderReport() => Render(sources[0]);
    public string AddEvidence(string visibleText)
    {
        var source = new ReviewSource("execution-evidence", "execution-evidence", "Displayed tool evidence (not a report)", visibleText);
        sources.Add(source);
        return Render(source);
    }
    public string AddFile(string path, string visibleText)
    {
        var source = new ReviewSource(FileId(path), "saved-file", path, visibleText);
        sources.Add(source);
        return Render(source);
    }
    public IReadOnlyList<ReviewSource> All => sources;
    public int VisibleCharacters => sources.Sum(s => s.VisibleText.Length);
    public ReviewSource? Find(string id) => sources.FirstOrDefault(s => s.Id == id);
    public string Describe() => references?.Describe() ?? "\nAllowed review sources (IDs are not paths):\n"
        + JsonSerializer.Serialize(sources.Select(s => new { source_id = s.Id, source_type = s.Kind, label = s.Label }))
        + "\nUse execution-evidence for assertions inspected in displayed tool output, never as a report source. Use saved-file only for listed file IDs; worker-report only for the worker message. "
        + "Never invent an agent report file. Cite fragment_id=F<number> from that source's displayed lines; "
        + "do not retype or normalize Markdown. If neither source contains command-outcome assertions, use command_reports=[].\n";
}
