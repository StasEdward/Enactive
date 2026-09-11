namespace Enactive.Agents;

using Enactive.Core.Permissions;

/// <summary>
/// The answer when nobody is there to give one: no.
///
/// <para>This is the rule the whole unattended design rests on — <b>unattended + Ask = Deny</b>. A
/// run on a schedule at three in the morning has no one to approve anything, so a policy that says
/// "ask before running commands" means, for that run, "do not run commands". The consequence is
/// deliberate and visible: a template that needs an approval to do its job cannot do its job
/// unattended, the refusal is recorded like any other, and the run comes out Incomplete rather than
/// finishing green over work it never did.</para>
///
/// <para>The alternative — taking the recommended option when no answer arrives — is what the
/// console host actually did before this existed: <c>Console.ReadLine()</c> returns null at end of
/// input, and the fallback was the RECOMMENDED option, which for a tool approval is "allow". A
/// scheduled run would have approved every command it was asked about, silently, on the grounds
/// that nobody objected. Nobody was there to object.</para>
/// </summary>
public sealed class UnattendedDecisionHandler : IDecisionHandler
{
    /// <summary>
    /// No. Not "usually no" - this handler has one answer and it is known before any question is
    /// put to it, which is what lets the engine stop offering the model tools it could only ever be
    /// refused.
    /// </summary>
    public bool CanApprove => false;

    /// <summary>
    /// Every decision this handler was actually asked, and refused.
    ///
    /// <para>It said "for the run report" for five weeks and no report has ever read it — the run's
    /// refusals reach a reader through the events and the journal, which is where every other
    /// refusal in the engine goes. Said plainly here rather than left as a claim: a property that
    /// describes a consumer it does not have is how somebody later builds on a feature that is not
    /// there.</para>
    ///
    /// <para>What it IS good for, and what it is now used for: counting the questions that were put
    /// to a handler which could only ever refuse them. An empty list is the whole point of §9an.</para>
    /// </summary>
    public IReadOnlyList<string> Refusals => _refusals;

    private readonly List<string> _refusals = new();

    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        lock (_refusals)
            _refusals.Add($"{request.Topic} — refused: this run is unattended.");

        // "deny" is the option every gate offers alongside "allow"; falling back to the LAST option
        // rather than a hard-coded id keeps this correct for a fork whose options are named
        // differently, because the last option is the conservative one by construction.
        var denial = request.Options.FirstOrDefault(
                         o => string.Equals(o.Id, "deny", StringComparison.OrdinalIgnoreCase))
                     ?? request.Options[^1];

        return Task.FromResult(new DecisionOutcome(denial.Id));
    }
}
