namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// The engine's switches as typed into a box (EngineSwitches), shared by the window's settings and the service's
/// settings page: what a wrong number means has to be the same in both.
/// </summary>
public sealed class EngineSwitchesTests
{
    [Fact]
    public void Opened_and_saved_untouched_they_are_what_they_were()
    {
        var engine = EngineOptions.Default with { ReviewRetries = 3, MaxParallelSteps = 2, NumCtx = 32768, DisableThinking = false };

        Assert.Equal(engine, EngineSwitches.Of(engine).ApplyTo(engine));
    }

    /// <summary>A number typed wrong is not a choice: it means the engine's default, never zero.</summary>
    [Fact]
    public void A_number_that_does_not_parse_or_is_out_of_range_means_the_default()
    {
        var switches = EngineSwitches.Of(EngineOptions.Default);
        switches.MaxParallelSteps = "two";
        switches.GenerationAction = "0";
        switches.EvidenceBudget = "a lot";
        switches.ReviewRetries = "12";
        switches.RepairThreshold = "1";

        var engine = switches.ApplyTo(EngineOptions.Default);

        Assert.Equal(EngineOptions.Default.MaxParallelSteps, engine.MaxParallelSteps);
        Assert.Equal(EngineOptions.Default.GenerationBudgets.Action, engine.GenerationBudgets.Action);
        // Not a number: automatic, the default. (A number below the floor is raised to it - AutomaticEvidenceBudgetTests.)
        Assert.Equal(EngineOptions.Default.EvidenceBudget, engine.EvidenceBudget);
        Assert.Equal(5, engine.ReviewRetries);
        Assert.Equal(0, engine.RepairConsultation.FailedRepairs);
    }

    /// <summary>The experimental switches have no box; saving the ones that do must not turn them off.</summary>
    [Fact]
    public void Switches_without_a_box_are_kept()
    {
        var engine = EngineOptions.Default with { StepOutputs = true, TypedCriteria = true };

        var saved = EngineSwitches.Of(engine).ApplyTo(engine);

        Assert.True(saved.StepOutputs);
        Assert.True(saved.TypedCriteria);
    }

    [Fact]
    public void A_repair_model_is_read_as_provider_slash_model()
    {
        var switches = EngineSwitches.Of(EngineOptions.Default);
        switches.RepairModel = " cloud/big ";
        switches.RepairThreshold = "2";

        var repair = switches.ApplyTo(EngineOptions.Default).RepairConsultation;

        Assert.Equal(("cloud", "big"), (repair.Model!.ProviderId, repair.Model.Model));
        Assert.Equal(2, repair.FailedRepairs);
    }
}
