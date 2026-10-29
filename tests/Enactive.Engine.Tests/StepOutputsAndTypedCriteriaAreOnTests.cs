namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Permissions;
using Enactive.Settings;
using Xunit;

/// <summary>
/// Plan Phase 1.5: StepOutputs and TypedCriteria are on by default. Measured 2026-10-28/29, three runs a side with a strong
/// planner and reviewer (plans/aidlc-adoption-bench.md): no false verdict either way; wiki-drift in half the steps and
/// time, a third fewer tokens; test-coverage two runs of three through against one. Every settings.json written so far
/// holds them as false - the default written out, since no settings page offered them - so an older file is read as on.
/// </summary>
public sealed class StepOutputsAndTypedCriteriaAreOnTests
{
    [Fact]
    public void They_are_on_by_default_and_dynamic_steps_is_not()
    {
        Assert.True(EngineOptions.Default.StepOutputs);
        Assert.True(EngineOptions.Default.TypedCriteria);
        // Judged on its own: a step per item multiplies model calls, and it was not measured.
        Assert.False(EngineOptions.Default.DynamicSteps);
    }

    private static void Edit(string path, Action<JsonObject> change)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(root);
        File.WriteAllText(path, root.ToJsonString());
    }

    /// <summary>A file as a build before schema 10 wrote it: both switches off, in the section and at the top level.</summary>
    private static string OlderFile(EngineFixture fx)
    {
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings().Save(path));
        Edit(path, root =>
        {
            root["SchemaVersion"] = 9;
            foreach (var name in new[] { "StepOutputs", "TypedCriteria" })
            {
                root["Engine"]![name] = false;
                root[name] = false;
            }
        });
        return path;
    }

    [Fact]
    public void An_older_file_s_written_out_off_is_read_as_on()
    {
        using var fx = new EngineFixture();

        var loaded = AppSettings.Load(OlderFile(fx));

        Assert.True(loaded.Engine.StepOutputs);
        Assert.True(loaded.Engine.TypedCriteria);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    /// <summary>From schema 10 on there is a box for each: off is somebody's choice, and it is kept.</summary>
    [Fact]
    public void Off_set_from_schema_10_on_is_kept()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings { Engine = EngineOptions.Default with { StepOutputs = false, TypedCriteria = false } }.Save(path));

        var loaded = AppSettings.Load(path);

        Assert.False(loaded.Engine.StepOutputs);
        Assert.False(loaded.Engine.TypedCriteria);
    }

    [Fact]
    public void The_switches_carry_them_both_ways()
    {
        var switches = EngineSwitches.Of(EngineOptions.Default with { StepOutputs = false, TypedCriteria = true });
        Assert.False(switches.StepOutputs);
        Assert.True(switches.TypedCriteria);

        switches.StepOutputs = true;
        switches.TypedCriteria = false;
        var engine = switches.ApplyTo(EngineOptions.Default);

        Assert.True(engine.StepOutputs);
        Assert.False(engine.TypedCriteria);
    }

    [Fact]
    public async Task The_settings_page_shows_them_and_saves_what_is_ticked()
    {
        AppSettings? saved = null;
        var folder = Directory.CreateTempSubdirectory("enactive-settings-vm-").FullName;
        try
        {
            var vm = new SettingsViewModel(new AppSettings(), s => saved = s, NoStartupEntry.Instance, editsFilesHere: true,
                writableRoots: new WritableRoots(Path.Combine(folder, "writable-roots.json")), knownWorkspaces: () => []);
            Assert.True(vm.StepOutputs);
            Assert.True(vm.TypedCriteria);

            vm.StepOutputs = false;
            await vm.SaveCommand.ExecuteAsync();

            Assert.False(saved!.Engine.StepOutputs);
            Assert.True(saved.Engine.TypedCriteria);
        }
        finally { Directory.Delete(folder, true); }
    }
}
