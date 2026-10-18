namespace Enactive.App.Ui.Tests;

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Enactive.App.Ui.ViewModels;
using Enactive.Application.Projections;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;

/// <summary>
/// "Pause after this step" in the window's run header (Plan Phase 4.2): offered while a planned run that does not stage its
/// changes is going; pressed, it asks THAT run's pause and says it is pending, while Stop stays. Each run gets a pause of
/// its own beside the cancellation Stop uses - a pause kept from the last run would pause a resumed one at once.
/// </summary>
public sealed class PauseAfterThisStepTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static WorkEvent Ev(EventKind kind, string summary = "", string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), RunId, DateTimeOffset.UtcNow, kind, summary, payload);

    private static WorkEvent Planned()
        => Ev(EventKind.PlanCreated, "Report — 2 steps", WorkEventPayload.PlanPayload("Report", ["gather", "write"]));

    /// <summary>The window's side: a run started, its pause handed to the header, and events folded in as they are drawn.</summary>
    private static (MainWindowViewModel Vm, RunPause Pause) Running(params WorkEvent[] events)
    {
        var vm = new MainWindowViewModel { IsBusy = true };
        var pause = new RunPause();
        vm.Pause = new PauseOffer(pause);
        foreach (var ev in events)
            vm.ApplyToPause(ev);
        return (vm, pause);
    }

    [Fact]
    public void It_is_offered_for_a_planned_run_that_is_going()
    {
        var (vm, _) = Running(Planned());

        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.IsPauseShown);
        Assert.Equal("Pause after this step", vm.PauseLabel);
    }

    [Fact]
    public void Before_the_plan_it_is_shown_greyed_and_says_when_it_comes()
    {
        var (vm, _) = Running(Ev(EventKind.IntentReceived, "Intent: write the report"));

        Assert.True(vm.IsPauseShown);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.Contains("once the run has a plan", vm.PauseTip);
    }

    /// <summary>A quick action has no step to pause after, and a staged run ignores a pause: neither is offered one.</summary>
    [Fact]
    public void A_quick_action_and_a_staged_run_are_offered_none()
    {
        var (quick, _) = Running(Ev(EventKind.Routed, "Quick action: list files", WorkEventPayload.QuickActionPayload("list files")));
        Assert.False(quick.IsPauseShown);
        Assert.False(quick.PauseCommand.CanExecute(null));

        var (staged, _) = Running();
        staged.Pause!.Staging();
        staged.PauseChanged();
        staged.ApplyToPause(Planned());
        Assert.False(staged.IsPauseShown);
        Assert.False(staged.PauseCommand.CanExecute(null));
    }

    [Fact]
    public void Pressing_it_asks_that_run_to_pause_and_says_it_is_pending_while_Stop_stays()
    {
        var (vm, pause) = Running(Planned());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.PauseCommand.Execute(null);

        Assert.True(pause.IsRequested);
        Assert.Equal("Pausing after the steps running now…", vm.PauseLabel);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.IsPauseShown);
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.Contains(nameof(MainWindowViewModel.PauseLabel), raised);
    }

    [Fact]
    public void Once_the_run_ends_it_is_gone()
    {
        var (vm, _) = Running(Planned());
        vm.PauseCommand.Execute(null);

        vm.ApplyToPause(Ev(EventKind.TaskFailed, "Paused", WorkEventPayload.OutcomePayload(RunOutcomeKind.Paused, "Paused at step 1 of 2")));

        Assert.False(vm.IsPauseShown);
        Assert.False(vm.PauseCommand.CanExecute(null));
    }

    /// <summary>The pause is kept beside the run's cancellation, one per run: the next run - a resume - gets a new one.</summary>
    [Fact]
    public void Each_run_gets_a_pause_of_its_own()
    {
        var slot = new ForegroundRunSlot();
        Assert.Null(slot.Pause);

        var first = slot.TryStart()!;
        var firstPause = slot.Pause;
        Assert.NotNull(firstPause);
        firstPause!.Request();
        Assert.True(slot.Finish(first));
        Assert.Null(slot.Pause);

        var second = slot.TryStart()!;
        Assert.NotNull(slot.Pause);
        Assert.NotSame(firstPause, slot.Pause);
        Assert.False(slot.Pause!.IsRequested);
        slot.Finish(second);
    }

    /// <summary>In the run header beside the phase pill, bound to the header's command, label and hint.</summary>
    [Fact]
    public void The_header_has_the_button()
    {
        var window = XDocument.Load(Path.Combine(Root(), "src", "Enactive.App.Ui", "MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = window.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PauseAfterThisStep");

        Assert.Equal("{Binding PauseCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("{Binding PauseLabel}", (string?)button.Attribute("Content"));
        Assert.Equal("{Binding PauseTip}", (string?)button.Attribute("ToolTip.Tip"));
        Assert.Equal("{Binding IsPauseShown}", (string?)button.Attribute("IsVisible"));
    }

    /// <summary>An UNFINISHED row of a run somebody paused says "paused", and keeps its Resume.</summary>
    [Fact]
    public void A_paused_run_is_listed_as_paused_and_can_be_resumed()
    {
        var first = Guid.NewGuid();
        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now,
            "Write the report", "Report", "developer", Spec: null,
            Steps:
            [
                new CheckpointStep(first, "gather", [], "Normal", "Done", "Succeeded"),
                new CheckpointStep(Guid.NewGuid(), "write", [first], "Normal", "Pending")
            ],
            Digest: [], Transcript: [], Artifacts: [], StepsRun: 1, TokensSpent: 0) { Paused = true };
        var runs = new RunsViewModel();
        RunCheckpoint? resumed = null;
        runs.ResumeRequested += c => resumed = c;

        runs.ShowResumable([checkpoint]);

        var row = Assert.Single(runs.Resumable);
        Assert.StartsWith("paused ", row.Meta);
        row.ResumeCommand.Execute(null);
        Assert.Same(checkpoint, resumed);
    }

    /// <summary>From this file, not from the build output: the tests are built outside the repository while the app runs.</summary>
    private static string Root([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
