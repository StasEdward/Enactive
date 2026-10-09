namespace Enactive.Tools;

using Enactive.Core.Mail;
using Enactive.Core.Tools;
using Enactive.Core.Web;
using Enactive.Tools.Web;

/// <summary>Fresh built-in instances for a host configuration snapshot; MCP and logging are host-owned wrappers.</summary>
public static class BuiltInTools
{
    public static ITool[] Create(MailAccount mail) => Create(mail, WebAccess.None);

    /// <param name="ecosystems">The kinds of project the engine knows (KnownEcosystems): with any, run_tests is registered.</param>
    public static ITool[] Create(MailAccount mail, WebAccess web, HttpClient? fetch = null, HttpClient? search = null,
        IReadOnlyList<Enactive.Core.Builds.IEcosystem>? ecosystems = null) =>
    [
        new WriteFileTool(), new EditFileTool(), new ReadFileTool(), new ReadFilesTool(),
        new SearchFilesTool(), new CountMatchesTool(), new FileStatsTool(), new CompareFilesTool(),
        new ListDirectoryTool(), new CreateDirectoryTool(), new MoveFileTool(), new CopyFileTool(), new DeleteFileTool(),
        new RestoreFileTool(),
        new RunCommandTool(), new RunPowerShellTool(), new GitTool(), new DockerTool(),
        .. (ecosystems is { Count: > 0 } ? [new RunTestsTool(ecosystems)] : Array.Empty<ITool>()),
        // Roles name this even without an account; the description explains its availability.
        new SendEmailTool(mail),
        // Registered only as far as a person turned them on, and named by no role until a person gives them to one.
        // Unlike send_email, the default roles do not name them: reading the web is sending a URL, or a query
        // written from the task, out of this machine, and that is a decision, not a default. When they exist and
        // no role names them, the engine says so (ToolReach.Unnamed), which is the reminder to give them to one.
        .. (web.Enabled ? [new FetchUrlTool(web, fetch ?? WebHttp.Fetching)] : Array.Empty<ITool>()),
        .. (web.CanSearch ? [new WebSearchTool(web, search ?? WebHttp.Searching)] : Array.Empty<ITool>())
    ];
}
