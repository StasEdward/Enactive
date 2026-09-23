namespace Enactive.Engine.Tests;

using Enactive.Core.Guidance;
using Xunit;

/// <summary>
/// The two model fields on a role explain themselves, because the screen next door offers the same
/// models again and nothing said which one wins.
///
/// <para><b>The question, in the words it was asked in, 2026-09-23:</b> <i>"in the roles section a
/// model is chosen for each role, and then the same again in phases. It looks like configuring it
/// twice. What do we choose a model per role for? It works by phases anyway."</i></para>
///
/// <para>It does not. <c>ModelRouter.ResolveExecute</c> is four lines —
/// <c>Trivial =&gt; light ?? base, Complex =&gt; heavy ?? base, _ =&gt; base</c> — so the role's
/// model runs every NORMAL step, which is the planner's default and therefore most of them, and
/// the two Execute bindings are optional overrides for the ends of the scale. Neither screen said
/// so.</para>
/// </summary>
public sealed class RoleAdviceTests
{
    [Fact]
    public void Both_fields_have_advice()
        => Assert.All(RoleAdvice.All, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Text)));

    /// <summary>
    /// The thing that was actually asked. The advice has to name what runs here — normal steps —
    /// and say that Phases does not take them away, or it has not answered the question.
    /// </summary>
    [Fact]
    public void The_model_field_says_what_it_decides_and_what_phases_do_not_take()
    {
        Assert.Contains("normal", RoleAdvice.Model, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Phases", RoleAdvice.Model, StringComparison.Ordinal);

        // And the part that is easy to miss from the role's own screen: the Execute bindings are
        // not this role's, they are everybody's.
        Assert.Contains("GLOBAL", RoleAdvice.Model, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fallback is a substitute for a provider that is down, not a cheaper second choice, and
    /// (none) is a real answer rather than an omission — the same bargain every phase hint makes.
    /// </summary>
    [Fact]
    public void The_fallback_says_what_none_means()
        => Assert.Contains("(none)", RoleAdvice.Fallback, StringComparison.Ordinal);

    /// <summary>
    /// And the collision that started it: "reviewer" is a phase AND a role, and the phase hint now
    /// says which one it is talking about. One word, two meanings, one application.
    /// </summary>
    [Fact]
    public void The_review_phase_says_it_is_not_the_reviewer_role()
    {
        Assert.Contains("not the", PhaseAdvice.Review, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROLE", PhaseAdvice.Review, StringComparison.Ordinal);
        Assert.Contains("read-only", PhaseAdvice.Review, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guidance, not rules — the constraint PhaseAdvice set for itself and the reason these read
    /// as help rather than as a policy the engine will enforce, which it will not.
    /// </summary>
    [Theory]
    [InlineData("must ")]
    [InlineData("never ")]
    [InlineData("forbidden")]
    public void It_reads_as_advice_and_not_as_a_rule(string word)
        => Assert.All(RoleAdvice.All,
                      pair => Assert.DoesNotContain(word, pair.Text, StringComparison.OrdinalIgnoreCase));
}
