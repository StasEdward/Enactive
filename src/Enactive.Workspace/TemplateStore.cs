namespace Enactive.Workspace;

using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.Core.Templates;

/// <summary>Where a template is kept, and therefore who it is for.</summary>
public enum TemplateScope
{
    /// <summary>%APPDATA%/Enactive/templates - available in every workspace.</summary>
    Global,

    /// <summary>&lt;workspace&gt;/.enactive/templates - lives with the project.</summary>
    Workspace
}

/// <summary>
/// Templates as files, one per template, named by its id.
///
/// <para><b>Why files and not the run store.</b> The database was the other candidate. A release
/// check that lives IN the repository is versioned with the code it checks, arrives with a clone,
/// is reviewed in a pull request and can be edited in a text editor with the app closed. None of
/// that is true of a row. The cost is that two processes writing the same template can clobber each
/// other, which is not a situation this feature creates.</para>
///
/// <para>A workspace template SHADOWS a global one with the same id, and both shadow a built-in.
/// That is the mechanism behind "generic template, project-specific build command": the generic one
/// stays generic, and the project keeps its own next to the code rather than editing the shared
/// copy.</para>
/// </summary>
public sealed class TemplateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string? _workspaceRoot;

    /// <param name="globalFolder">
    /// Overrides where the global templates live. Only a test passes this: without it a test would
    /// read and write the real %APPDATA% folder, so its result would depend on which templates the
    /// person running it happens to have.
    /// </param>
    public TemplateStore(string? workspaceRoot = null, string? globalFolder = null)
    {
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? null : workspaceRoot;
        GlobalFolder = string.IsNullOrWhiteSpace(globalFolder) ? DefaultGlobalFolder : globalFolder!;
    }

    public static string DefaultGlobalFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Enactive", "templates");

    public string GlobalFolder { get; }

    public string? WorkspaceFolder => _workspaceRoot is null
        ? null
        : Path.Combine(_workspaceRoot, ".enactive", "templates");

    /// <summary>
    /// Every template visible here, workspace-local ones shadowing global ones of the same id,
    /// ordered by name.
    ///
    /// <para>A file that cannot be read or understood is SKIPPED rather than failing the listing.
    /// One malformed template must not hide the other nine - the user would see an empty library and
    /// have no idea which file to fix, which is a worse failure than the one it reports.</para>
    /// </summary>
    public IReadOnlyList<TaskTemplate> Load()
    {
        var byId = new Dictionary<string, TaskTemplate>(StringComparer.OrdinalIgnoreCase);

        // Built-ins first, so a file of the same id replaces one. That is the customisation path
        // for someone who wants THIS project's Release Check rather than a copy under a new name.
        foreach (var template in BuiltinTemplates.All)
            byId[template.Id] = template;

        foreach (var template in ReadFolder(GlobalFolder))
            byId[template.Id] = template;

        if (WorkspaceFolder is { } local)
            foreach (var template in ReadFolder(local))
                byId[template.Id] = template;

        return byId.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public TaskTemplate? Find(string id)
        => Load().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes a template, refusing an invalid one.
    ///
    /// <para>The id is re-checked here even though <see cref="TemplateValidator"/> already checked
    /// it, because this is the method that turns an id into a path. A store that trusts its caller
    /// is one refactor away from not being validated at all, and the failure mode is a write outside
    /// the template folder.</para>
    /// </summary>
    public void Save(TaskTemplate template, TemplateScope scope)
    {
        var problems = TemplateValidator.Validate(template);
        if (problems.Count > 0)
            throw new ArgumentException(
                "This template cannot be stored: " + string.Join("; ", problems), nameof(template));

        if (template.Builtin)
            throw new InvalidOperationException(
                $"'{template.Id}' is a built-in template and is read-only. Duplicate it first - a "
                + "user who edits the original has no way to get it back.");

        var path = PathFor(template.Id, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite.Replace(path, JsonSerializer.Serialize(template, Json));
    }

    /// <summary>Removes a template from one scope. A missing file is not an error - it is the goal.</summary>
    public void Delete(string id, TemplateScope scope)
    {
        var path = PathFor(id, scope);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
    }

    /// <summary>The file a template with this id lives in. Refuses an id that is not a safe slug.</summary>
    public string PathFor(string id, TemplateScope scope)
    {
        if (!TemplateValidator.IsSafeId(id))
            throw new ArgumentException(
                $"'{id}' is not a valid template id. It becomes a file name, so a path separator, a "
                + "drive letter or '..' is refused rather than sanitised.", nameof(id));

        var folder = scope == TemplateScope.Workspace
            ? WorkspaceFolder ?? throw new InvalidOperationException(
                "This store has no workspace, so it cannot address a workspace-scoped template.")
            : GlobalFolder;

        return Path.Combine(folder, id + ".json");
    }

    private static IEnumerable<TaskTemplate> ReadFolder(string folder)
    {
        if (!Directory.Exists(folder))
            yield break;

        string[] files;
        try { files = Directory.GetFiles(folder, "*.json"); }
        catch { yield break; }

        foreach (var file in files)
        {
            TaskTemplate? template = null;
            try { template = JsonSerializer.Deserialize<TaskTemplate>(File.ReadAllText(file), Json); }
            catch { /* skipped: see Load */ }

            // The id in the file wins over the file name, but a file whose template has no usable id
            // at all is unaddressable and would break Save, so it never enters the library.
            if (template is not null && TemplateValidator.IsSafeId(template.Id))
                yield return template;
        }
    }
}
