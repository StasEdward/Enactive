namespace Enactive.Engine.Tests;

using System.Reflection;
using Enactive.Application;
using Enactive.Application.Mvvm;
using Enactive.Application.Projections;
using Xunit;

/// <summary>
/// The application layer is what every host shares around a run (ADR 0002 §2): the window today, the
/// service next. These tests hold the two properties that make it shareable.
/// </summary>
public sealed class ApplicationLayerTests
{
    private static readonly Assembly Layer = typeof(BackgroundRunGroup).Assembly;

    /// <summary>
    /// Nothing that draws, directly or through another project. One convenience import of a UI package
    /// here and the Windows service would have to ship and load a desktop toolkit it never shows - or,
    /// worse, someone would "fix" that by giving the service its own copy of these classes, and the two
    /// copies of the rules around a run would drift (the reason this project exists).
    /// </summary>
    [Fact]
    public void References_no_ui_toolkit_even_transitively()
    {
        string[] forbidden = ["Avalonia", "enactive-ui", "PresentationFramework", "System.Windows.Forms", "Microsoft.AspNetCore.Components"];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<Assembly>([Layer]);
        var offenders = new List<string>();

        while (pending.TryPop(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name ?? string.Empty;
                if (!seen.Add(name))
                    continue;

                if (forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                    offenders.Add($"{assembly.GetName().Name} -> {name}");

                // Only this repository's own assemblies are walked: the framework's are not where a UI
                // dependency would be added, and loading every one of them only slows the test down.
                if (name.StartsWith("Enactive.", StringComparison.OrdinalIgnoreCase))
                    pending.Push(Assembly.Load(reference));
            }
        }

        Assert.True(offenders.Count == 0, "The application layer reaches a UI toolkit: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The tests exercise the layer's own assembly, not a copy compiled into this one. This project used
    /// to pull the window's files in one by one with Compile Include; put such a line back for a type that
    /// now lives in the layer and the compiler only WARNS (CS0436) and takes the local copy - the tests would
    /// go on passing against code that no host runs.
    /// </summary>
    [Theory]
    [InlineData(typeof(BackgroundRunGroup))]
    [InlineData(typeof(DecisionCompletion))]
    [InlineData(typeof(InboxDecisions))]
    [InlineData(typeof(IStartupEntry))]
    [InlineData(typeof(LogFilter))]
    [InlineData(typeof(EngineSwitches))]
    [InlineData(typeof(SchedulePolicy))]
    [InlineData(typeof(SettingsSave))]
    [InlineData(typeof(ObservableObject))]
    [InlineData(typeof(RelayCommand))]
    [InlineData(typeof(AsyncRelayCommand))]
    [InlineData(typeof(RunTimeline))]
    [InlineData(typeof(RunEventViewModel))]
    [InlineData(typeof(TimelineTones))]
    [InlineData(typeof(RunRouting))]
    [InlineData(typeof(ModelPerformance))]
    [InlineData(typeof(ModelWorkSplit))]
    [InlineData(typeof(UnfinishedChanges))]
    [InlineData(typeof(TokenCounts))]
    [InlineData(typeof(LiveRuns))]
    [InlineData(typeof(WorkInFlight))]
    [InlineData(typeof(RunLauncher))]
    [InlineData(typeof(RunActivity))]
    [InlineData(typeof(RunQueue))]
    [InlineData(typeof(DecisionRouter))]
    [InlineData(typeof(RunHistoryService))]
    [InlineData(typeof(BackgroundRun))]
    [InlineData(typeof(RunDigest))]
    [InlineData(typeof(ModelReaches))]
    [InlineData(typeof(WorkspaceFiles))]
    [InlineData(typeof(DueSchedules))]
    [InlineData(typeof(LogText))]
    [InlineData(typeof(RunRecovery))]
    [InlineData(typeof(LogWindowViewModel))]
    [InlineData(typeof(BatchObservableCollection<int>))]
    [InlineData(typeof(WorkspaceReports))]
    [InlineData(typeof(SettingsViewModel))]
    [InlineData(typeof(ProviderRow))]
    [InlineData(typeof(WorkerRow))]
    [InlineData(typeof(McpServerRow))]
    [InlineData(typeof(TemplateRow))]
    [InlineData(typeof(WritableRootRow))]
    [InlineData(typeof(ProviderEditViewModel))]
    [InlineData(typeof(WorkerEditViewModel))]
    [InlineData(typeof(ToolToggle))]
    [InlineData(typeof(BuildInfo))]
    [InlineData(typeof(BuildComponent))]
    [InlineData(typeof(McpEditorWords))]
    [InlineData(typeof(IHostSettingsPane))]
    [InlineData(typeof(NoStartupEntry))]
    [InlineData(typeof(IProviderCalls))]
    [InlineData(typeof(DirectProviderCalls))]
    [InlineData(typeof(CommandVerbs))]
    [InlineData(typeof(RunButton))]
    [InlineData(typeof(LogRow))]
    [InlineData(typeof(SourceToggle))]
    [InlineData(typeof(ILogHubChanges))]
    [InlineData(typeof(HubChanges))]
    [InlineData(typeof(DiffLineKind))]
    [InlineData(typeof(DiffLines))]
    [InlineData(typeof(InboxItemViewModel))]
    [InlineData(typeof(InboxOption))]
    [InlineData(typeof(InboxRows))]
    [InlineData(typeof(RepoBar))]
    [InlineData(typeof(RunAgainOffer))]
    [InlineData(typeof(PastRunTabs))]
    [InlineData(typeof(RunElapsed))]
    [InlineData(typeof(RunFigures))]
    [InlineData(typeof(FileNews))]
    [InlineData(typeof(RunFiles))]
    [InlineData(typeof(RunPanel))]
    [InlineData(typeof(RunRows))]
    [InlineData(typeof(StepEntries))]
    [InlineData(typeof(Times))]
    [InlineData(typeof(WorkspacePlace))]
    [InlineData(typeof(WorkspaceRows))]
    [InlineData(typeof(IScheduleChanges))]
    [InlineData(typeof(StoredScheduleChanges))]
    [InlineData(typeof(ScheduleState))]
    [InlineData(typeof(ScheduleListItem))]
    [InlineData(typeof(ScheduleOutcomeLine))]
    [InlineData(typeof(WorkChoice))]
    [InlineData(typeof(ZoneChoice))]
    [InlineData(typeof(SchedulesViewModel))]
    [InlineData(typeof(TemplateDrafts))]
    [InlineData(typeof(TemplateEdge))]
    [InlineData(typeof(TemplateEdges))]
    [InlineData(typeof(ParameterRow))]
    [InlineData(typeof(CriterionRow))]
    [InlineData(typeof(ToolPermissionRow))]
    [InlineData(typeof(TemplateEditViewModel))]
    [InlineData(typeof(TemplateParameterInput))]
    [InlineData(typeof(TemplatesViewModel))]
    [InlineData(typeof(TemplateListItem))]
    [InlineData(typeof(EnvironmentReport))]
    public void Moved_types_come_from_the_application_assembly(Type type)
        => Assert.Equal("Enactive.Application", type.Assembly.GetName().Name);
}
