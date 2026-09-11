namespace Enactive.Core.Templates;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Whether a template can do its job under a given policy, and what to say when it cannot.
///
/// <para><b>The join that was missing.</b> The schedules window already said, before anything was
/// saved, "Refused, not asked about: run_command, run_powershell, git, docker — nobody is watching a
/// scheduled run". It also said which template would run. Nobody put the two together, so a person
/// read both true sentences, saved <c>code-review</c> on the Execute tier, and found out at 00:14
/// that its first act was a <c>git diff</c> it was never going to be allowed to make.</para>
///
/// <para>Everything here is the same rule the RUN uses: <see cref="PermissionEngine"/> for the
/// decision and <see cref="ToolOffers"/> for what a run that cannot be asked will actually be
/// offered. A second copy of either, written for the window, would agree until somebody edited
/// one.</para>
/// </summary>
public static class TemplateNeeds
{
    /// <param name="approvalIsPossible">
    /// False for a schedule, always - that is what a schedule IS. Passed rather than assumed so the
    /// same check answers for a console run with <c>--approve deny</c>, and so the interactive case
    /// is a value somebody can supply rather than a branch nobody wrote.
    /// </param>
    /// <param name="requiredLevelOf">
    /// A tool's autonomy level, when the caller has a registry to ask. Where it does not - the
    /// schedules window does not carry one - every tool falls back to
    /// <see cref="PermissionLevel.Execute"/>, which is exactly what <c>ToolRegistry</c> itself
    /// answers for a name it does not know. The shipped read-only tools are Observe, so the
    /// fallback would be wrong for a need declared on one of those; a test asserts no shipped
    /// template does that, so the day one appears the fallback is a failure rather than a
    /// misjudgement nobody sees.
    /// </param>
    /// <returns>
    /// One sentence per need that cannot be met, empty when the template can work. Every one, not
    /// the first: a form that reveals its objections one at a time makes a person submit four times
    /// to learn four things.
    /// </returns>
    public static IReadOnlyList<string> Unmet(
        TaskTemplate template,
        PermissionPolicy policy,
        bool approvalIsPossible,
        Func<string, PermissionLevel>? requiredLevelOf = null)
    {
        if (template.Needs.Count == 0)
            return Array.Empty<string>();

        var engine = new PermissionEngine();
        var levelOf = requiredLevelOf ?? (_ => PermissionLevel.Execute);
        var problems = new List<string>();

        foreach (var need in template.Needs)
        {
            var offer = ToolOffers.For(
                need.AnyOf, tool => engine.Evaluate(policy, tool, levelOf(tool)), approvalIsPossible);

            // ANY of them is enough. The need is about a capability, and a run that has one way to
            // do it does not care that the other two are shut.
            if (offer.Offered.Count > 0)
                continue;

            problems.Add(Explain(template, need, offer));
        }

        return problems;
    }

    private static string Explain(TaskTemplate template, TemplateNeed need, ToolOffer offer)
    {
        var sentence =
            $"'{template.Name}' cannot {need.What}: it would need {Either(need.AnyOf)}, and this "
            + $"run has none of them — {offer.Because}.";

        // What to DO about it, which depends on which way it is shut. A tool the policy denies
        // outright is not going to be opened by choosing a different tier, and telling somebody to
        // raise the tier when that cannot help is worse than saying nothing.
        var anyUnanswerable = offer.Withheld.Any(w => w.Reason == ToolOffers.Unanswerable);

        sentence += anyUnanswerable
            ? " Nobody is there to approve anything, so choose a tier that allows these outright,"
              + " or start this one yourself"
            : " The tier and the template between them forbid all of them";

        return need.Otherwise is { Length: > 0 } otherwise
            ? $"{sentence}, {otherwise}."
            : $"{sentence}.";
    }

    /// <summary>"git, run_command or run_powershell" - alternatives read with "or", never "and".</summary>
    private static string Either(IReadOnlyList<string> tools)
        => tools.Count switch
        {
            0 => "a tool it does not name",
            1 => tools[0],
            _ => string.Join(", ", tools.Take(tools.Count - 1)) + " or " + tools[^1]
        };
}
