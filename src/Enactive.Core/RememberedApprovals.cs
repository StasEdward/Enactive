namespace Enactive.Core.Permissions;

/// <summary>
/// Answers, before anybody is asked, the questions a person already answered for good: "allow this
/// tool in this workspace" (<see cref="ApprovalStore"/>) and, while the application runs, "allow it
/// for this session" (<see cref="SessionApprovals"/>). Everything else goes to the handler it wraps.
///
/// <para><b>Why a decorator every run gets.</b> The window used to answer these inside its own approval
/// card, so they held only for a run somebody was watching. A background run stopped at the very
/// question its owner had answered "for this workspace", and a scheduled run was refused it. A
/// standing answer is about the tool and the folder, not about who happens to be at the screen.</para>
///
/// <para><b>Where it is not used.</b> A task from a phone needs a fresh answer for every call - remote
/// requests must not inherit desktop standing grants (<see cref="DecisionRequest.RequiresExplicitAnswer"/>)
/// - and a console given <c>--approve</c> has been told the answer for this invocation. The run
/// composer leaves both unwrapped.</para>
/// </summary>
/// <param name="workspaceRoot">The run's workspace: what a standing answer is asked about before a request exists.</param>
/// <param name="watched">
/// Somebody is at the screen for this run. Where nobody is - a run in the background, on a schedule - only an approval
/// given for unwatched runs too answers (ApprovalStore, rule 4). Not the handler's <c>CanApprove</c>: a background run
/// parks its questions for later, which can approve, and is still a run nobody is watching.
/// </param>
public sealed class RememberedApprovals(
    IDecisionHandler inner, string workspaceRoot, ApprovalStore workspace, SessionApprovals? session, bool watched) : IDecisionHandler
{
    /// <summary>The option a remembered approval answers with. A request without it is not one an approval can answer.</summary>
    public const string AllowOptionId = "allow";

    public bool CanApprove => inner.CanApprove;

    /// <summary>
    /// A tool somebody allowed for good in this workspace - for unwatched runs too, where nobody is watching this one -
    /// can be approved even by a handler that can approve nothing, so such a run is offered that tool, and only that one.
    /// </summary>
    public bool CanApproveTool(string tool)
        => inner.CanApproveTool(tool)
           || session?.Approves(workspaceRoot, tool) == true
           || workspace.Approves(workspaceRoot, tool, unwatched: !watched);

    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        // Only a request that names its tool, can be answered "allow", and does not insist on a person.
        if (!request.RequiresExplicitAnswer && !string.IsNullOrEmpty(request.Subject)
            && request.Options.Any(o => o.Id == AllowOptionId))
        {
            // Which one answered is carried back, so the timeline can say so (see DecisionOutcome.Because).
            if (session?.Approves(request) == true)
                return Task.FromResult(new DecisionOutcome(AllowOptionId, "remembered for this session and workspace"));

            if (request.MayBeRemembered && request.Action?.WorkingDirectory is { } root)
            {
                if (workspace.Approves(root, request.Subject, unwatched: true))
                    return Task.FromResult(new DecisionOutcome(AllowOptionId, "remembered for this workspace, unwatched runs too"));
                if (watched && workspace.Approves(root, request.Subject))
                    return Task.FromResult(new DecisionOutcome(AllowOptionId, "remembered for this workspace"));
            }
        }

        return inner.RequestAsync(request, ct);
    }
}
