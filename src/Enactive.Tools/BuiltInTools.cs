namespace Enactive.Tools;

using Enactive.Core.Mail;
using Enactive.Core.Tools;

/// <summary>Fresh built-in instances for a host configuration snapshot; MCP and logging are host-owned wrappers.</summary>
public static class BuiltInTools
{
    public static ITool[] Create(MailAccount mail) =>
    [
        new WriteFileTool(), new EditFileTool(), new ReadFileTool(), new ReadFilesTool(),
        new SearchFilesTool(), new CountMatchesTool(), new FileStatsTool(), new CompareFilesTool(),
        new ListDirectoryTool(), new CreateDirectoryTool(), new MoveFileTool(), new CopyFileTool(), new DeleteFileTool(),
        new RunCommandTool(), new RunPowerShellTool(), new GitTool(), new DockerTool(),
        // Roles name this even without an account; the description explains its availability.
        new SendEmailTool(mail)
    ];
}
