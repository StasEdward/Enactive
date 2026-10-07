namespace Enactive.Core.Tools;

using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Permissions;

/// <summary>
/// Tool description plus trusted execution metadata. WorkspaceEffect describes successful calls;
/// None also promises that failure paths do not mutate. Other failures remain Unknown unless the
/// result reports an explicit effect. Adapters send only name, description and schema to the model.
/// ChangedPathArguments identifies destinations for project/scratch scope, not execution success.
/// RepairsFileFailures and ProgressIdentity are separate recovery and stall policies.
/// ParallelRead explicitly promises concurrent read safety, including with the same tool instance;
/// it is honoured only with None effects, Observe permission and no required approval.
/// RunsSuccessChecks opts into command:string input and exitCode metadata; it is not permission.
/// OnceOnly marks an action outside the workspace that cannot be taken back - a message sent, a post
/// published - so doing it twice is doing two things: a task carried on does not repeat one it has
/// already taken with the same arguments (see TaskProgress).
/// </summary>
public sealed record ToolDefinition(string Name, string Description, string JsonSchema,
    WorkspaceEffect WorkspaceEffect = WorkspaceEffect.Unknown,
    IReadOnlyList<string>? ChangedPathArguments = null,
    bool RepairsFileFailures = false,
    ProgressIdentity ProgressIdentity = ProgressIdentity.Observation,
    bool ParallelRead = false,
    ToolKind Kind = ToolKind.Unknown,
    FileCoverageBehavior FileCoverage = FileCoverageBehavior.None,
    bool RunsSuccessChecks = false,
    CommandPolicySyntax CommandPolicy = CommandPolicySyntax.None,
    bool OnceOnly = false);

/// <summary>Trusted command-policy adapter. None refuses command tools under an allowlist.</summary>
public enum CommandPolicySyntax { None, SimpleCommand, PowerShell }

/// <summary>Validates a command language without executing the requested script.</summary>
public interface ICommandPolicyValidator
{
    Task<string?> ValidatePolicyAsync(string argumentsJson, TaskActionPolicy policy, IReadOnlyList<TaskRestriction> restrictions, CancellationToken ct);
}

/// <summary>Trusted host classification; never inferred from a name or from model/MCP text.
/// Unknown keeps conservative review. Kind grants no permissions or concurrency guarantees.</summary>
public enum ToolKind { Unknown, Read, Write, Relocate, Command }

/// <summary>Opt-in coverage protocol: Read returns FileCoverage/line metadata; Replace uses path;
/// Delete forgets path; Move transfers from to to. Independent of workspace effects.</summary>
public enum FileCoverageBehavior { None, Read, Replace, Delete, Move }

/// <summary>Workspace effects, independent of success and of whether the action is novel.</summary>
public enum WorkspaceEffect { None, Changed, Unknown }

/// <summary>Stall identity policy, not evidence that the workspace changed.</summary>
public enum ProgressIdentity { Observation, Action }

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// Argument names the ENGINE has to know about as well as the tool that reads them. Kept here
/// because Core is the only layer both sides share, and a name spelled twice is a name that drifts.
/// </summary>
public static class ToolArguments
{
    /// <summary>
    /// The exit codes a caller declares it expects from a command (see the command-running tools).
    ///
    /// <para>The engine cares because it says how to READ a result, not what to do: a command and
    /// the same command declaring that 1 is an answer are the same action, and a run-again-and-
    /// declare that produced a different identity would leave the first failure open forever.</para>
    /// </summary>
    public const string ExpectedExitCodes = "expectedExitCodes";

    /// <summary>The command line <c>run_command</c> is given.</summary>
    public const string Command = "command";

    /// <summary>
    /// The argument LIST <c>git</c> and <c>docker</c> are given — an array, or one string they
    /// split themselves. No shell sees it, which is why a pipe inside one element is data.
    /// </summary>
    public const string Args = "args";

    /// <summary>The script <c>run_powershell</c> is given.</summary>
    public const string Script = "script";

    /// <summary>
    /// Set to run a command AGAIN despite the engine's own knowledge that it already ran with these
    /// exact arguments this step at the same tracked workspace version - see the repeat gate in
    /// Orchestrator. Not read by any tool's own logic; it exists only for the engine to see before
    /// the tool is invoked, the same way <see cref="ExpectedExitCodes"/> is.
    /// </summary>
    public const string Force = "force";
}

/// <summary>Resolves declared path effects; contains no tool-name classifications or mutable state.</summary>
public static class ToolEffects
{
    public static IReadOnlyList<string>? Paths(ToolDefinition definition, string? arguments)
    {
        if (definition.ChangedPathArguments is not { Count: > 0 } names) return null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var paths = new List<string>();
            foreach (var name in names)
            {
                if (!doc.RootElement.TryGetProperty(name, out var value)
                    || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    return null;
                paths.Add(value.GetString()!);
            }
            return paths.AsReadOnly();
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The tools whose argument is a COMMAND LINE the operating system interprets.
///
/// <para>The workspace is where they start, and that is all it is. Every file tool and both
/// artifact stores resolve through <see cref="Context.WorkspaceGuard"/>, which refuses a path that
/// leaves the root and follows links to find out; a shell is handed a string and the OS does the
/// rest. <c>cd</c> elsewhere, an absolute path, a pipe to a network tool - none of it is reachable
/// from here, and pretending otherwise by inspecting the command text would be the guard that is
/// stepped around by writing <c>./x</c> instead of <c>x</c>.</para>
///
/// <para><b>2026-09-10: the command text IS now inspected, and the paragraph above is still
/// right.</b> <see cref="Context.ShellGeography"/> reads a command line for writes that land
/// outside the workspace - and everything said above about how easily that is stepped around holds
/// exactly. What changed is what the answer is used for: it raises a QUESTION for the person, and
/// it never refuses on its own. A guard that refuses on a guess this rough would be the failure
/// named above; a guess that turns <c>dotnet publish -o C:\out</c> from silent into asked is worth
/// having, and claims nothing it cannot do.</para>
///
/// <para>So what this list is for is not containment. It marks the calls whose approval must not
/// outlive the session: <c>run_command</c> approved once for a workspace is unlimited command
/// execution on the machine, forever, from one click - and it was offered by the same button as
/// <c>read_file</c>. See <c>ApprovalStore</c>.</para>
///
/// <para><c>git</c> also grants command execution: aliases, hooks, filters and helpers can run
/// external programs, and arguments/configuration can select another workspace. ArgumentList
/// prevents shell quoting injection, not these git capabilities. It therefore receives
/// the same approval lifetime and remote restrictions. Docker remains a separate policy issue.</para>
/// </summary>
public static class ShellTools
{
    /// <summary>
    /// The names, for a caller that has to WRITE them somewhere rather than ask about one — into a
    /// policy's deny list, say.
    ///
    /// <para>Public because it was being retyped. The same two strings were a literal in the remote
    /// policy and a literal in the settings composition, next to this set that answers
    /// <see cref="IsShell"/>: three copies of the list that decides what counts as handing a command
    /// line to the machine. Adding a third shell tool would have had to be remembered in all three,
    /// and the one that was forgotten would have been the one that refuses.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> All = ["run_command", "run_powershell", "git", "docker"];

    private static readonly HashSet<string> Names = new(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a tool grants general command execution, directly or through git helpers/hooks.</summary>
    public static bool IsShell(string tool) => Names.Contains(tool);

    /// <summary>
    /// The same policy with every shell added to a list on it — <c>Deny</c> to refuse them outright,
    /// <c>AskBefore</c> to put the question to a person.
    ///
    /// <para>Here rather than at either call site because both of them are the same operation on the
    /// same list, and they had each written it out. Case-insensitive and idempotent: applying it to a
    /// policy that already names a shell must not name it twice.</para>
    /// </summary>
    public static PermissionPolicy Denied(PermissionPolicy policy)
        => policy with { Deny = Plus(policy.Deny) };

    /// <inheritdoc cref="Denied"/>
    public static PermissionPolicy Asked(PermissionPolicy policy)
        => policy with { AskBefore = Plus(policy.AskBefore) };

    private static string[] Plus(IEnumerable<string> names)
        => names.Concat(All).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>Structured tool result — never a bare string.</summary>
/// <param name="IsAnswer">
/// Set on a FAILED result that is nevertheless the answer to what was asked: the file the model
/// wanted to look at is not there, and being told so is the whole information it needed. Nothing
/// is left unfinished and there is nothing to retry, so a step must not be held open by it —
/// unless such lookups are ALL the step has to show, which is a step that did nothing.
///
/// <para>Only a LOOKUP can set this. "File not found" from <c>edit_file</c> or <c>move_file</c> is
/// the opposite — an edit that did not happen — and those keep it false.</para>
/// </param>
public sealed record ToolResult(
    bool Success,
    string? Output,
    string? Error,
    IReadOnlyList<ArtifactRef> Artifacts,
    IReadOnlyDictionary<string, object?> Metadata,
    bool IsAnswer = false,
    bool DidNotRun = false,
    WorkspaceEffect? WorkspaceEffect = null,
    IReadOnlyList<string>? ChangedPaths = null);

/// <summary>Factory helpers for <see cref="ToolResult"/>.</summary>
public static class ToolResults
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyMeta = new Dictionary<string, object?>();

    public static ToolResult Ok(
        string? output = null,
        IReadOnlyList<ArtifactRef>? artifacts = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new(true, output, null, artifacts ?? Array.Empty<ArtifactRef>(), metadata ?? EmptyMeta);

    /// <summary>
    /// A failed tool call. <paramref name="output"/> and <paramref name="metadata"/> are kept on the
    /// failure path too: a command that fails is exactly when its stdout/stderr and exit code matter,
    /// and dropping them would force the model to guess what went wrong.
    /// </summary>
    public static ToolResult Fail(
        string error,
        string? output = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new(false, output, error, Array.Empty<ArtifactRef>(), metadata ?? EmptyMeta);

    /// <summary>
    /// A lookup whose target is not there. The call did not succeed, and it is not unfinished
    /// either: "there is no such file" is what the model asked to be told.
    ///
    /// <para>Reported with a log, 2026-09-07: a step listed a folder, read the one file in it,
    /// guessed at a second name, was told that file does not exist, wrote the documentation it was
    /// asked to write — and the run was failed anyway, because an unresolved <c>read_file</c> held
    /// the step open and the next step was skipped behind it. Guessing at a path and being told no
    /// is how exploring works.</para>
    ///
    /// <para>With one condition, which two shipping tests insisted on: a step whose ENTIRE record is
    /// lookups that found nothing has produced nothing, and saying "Done" over that is the defect
    /// the unresolved-failure guard exists for. One call that WORKED is the difference.</para>
    ///
    /// <para>For LOOKUPS only — <c>read_file</c>, <c>list_dir</c>, <c>search_files</c>. A write,
    /// an edit or a move that cannot find its target uses <see cref="Fail"/>: there, the missing
    /// file means the work did not happen.</para>
    /// </summary>
    public static ToolResult NotFound(
        string error,
        string? output = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new(false, output, error, Array.Empty<ArtifactRef>(), metadata ?? EmptyMeta, IsAnswer: true);

    /// <summary>
    /// The call could not be READ, so nothing was attempted: a required argument missing, arguments
    /// that are not JSON, a whole command line packed into one array element.
    ///
    /// <para>A sentence that did not parse, not an action that did not happen — and the difference
    /// decides whether the step is left holding something unfinished. Nothing is: the tool returned
    /// before it touched anything, and when the model says it properly the next moment, there is no
    /// residue from the first attempt to make good.</para>
    ///
    /// <para>The engine already believed this for the tools that write files: a <c>write_file</c>
    /// refused for a missing <c>path</c> is closed by the same tool succeeding afterwards, on the
    /// grounds that it "is not work that did not happen, it is a sentence that did not parse". That
    /// reasoning has nothing to do with writing, but the rule was written to depend on it. Reported
    /// 2026-09-08 17:25: <c>git ["diff HEAD"]</c> was refused before git ran, the model then ran
    /// <c>git ["diff"]</c> and <c>git ["status"]</c>, reported truthfully what they showed, wrote its
    /// file — and the run was failed for two calls that never happened.</para>
    ///
    /// <para>Set by the TOOL, like <see cref="ToolResult.IsAnswer"/> and for the same reason: only
    /// the tool knows whether it got as far as doing anything, and no error text can be parsed for
    /// it afterwards.</para>
    /// </summary>
    public static ToolResult Unreadable(
        string error,
        string? output = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new(false, output, error, Array.Empty<ArtifactRef>(), metadata ?? EmptyMeta, DidNotRun: true);

    /// <summary>
    /// A command the SHELL refused to start — see <see cref="Context.ShellRefusal"/> for what that
    /// means and how it is told apart from a command that ran and failed.
    ///
    /// <para><see cref="Unreadable"/>'s note says this must be <i>"set by the TOOL … no error text
    /// can be parsed for it afterwards"</i>, and that is why this factory is called from inside
    /// <c>ProcessExec</c> rather than from anything downstream. The tool is the one that knows: it
    /// launched the shell, it holds the line it sent, and what it is reading is the shell's own
    /// fixed wording about that line — not a message some later layer is guessing at.</para>
    /// </summary>
    public static ToolResult NeverRan(
        string error,
        string? output = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new(false, output, error, Array.Empty<ArtifactRef>(), metadata ?? EmptyMeta, DidNotRun: true);
}

/// <summary>
/// The only surface a tool sees. No UI / Orchestrator back-channel.
///
/// <para>It carried an IServiceProvider until 2026-10-08 that no tool ever read: every host and test handed in
/// a provider that answered null to everything, four copies of it, so that there was something to pass. A tool
/// that needs something takes it when it is built (BuiltInTools.Create), as the mail and web tools do.</para>
/// </summary>
public sealed record ToolContext(
    Guid TaskId,
    Guid RunId,
    Guid WorkspaceId,
    WorkContext Context,
    PermissionPolicy PermissionPolicy,
    string WorkspaceRoot,
    IArtifactStore Artifacts);

/// <summary>A capability the agent can invoke.</summary>
public interface ITool
{
    bool RequiresApproval => false;
    ToolDefinition Definition { get; }
    PermissionLevel RequiredLevel { get; }
    Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct);
}

/// <summary>Resolves and invokes tools by name.</summary>
public interface IToolRegistry
{
    /// <summary>Shared workspace revision, or null while effects are pending or cannot be tracked.</summary>
    long? WorkspaceVersion(Guid workspaceId) => null;
    bool RequiresApprovalOf(string toolName) => false;
    IReadOnlyList<ToolDefinition> Definitions { get; }

    ToolDefinition? DefinitionOf(string name) => Definitions.FirstOrDefault(
        d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    PermissionLevel RequiredLevelOf(string toolName);
    Task<ToolResult> InvokeAsync(ToolCall call, ToolContext ctx, CancellationToken ct);
}
