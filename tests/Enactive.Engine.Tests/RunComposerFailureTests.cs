namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Diagnostics;
using Enactive.Core.Intents;
using Enactive.Core.Storage;
using Enactive.Settings;
using Enactive.Workspace;
using Xunit;

// The store is selected through process environment variables; no other test may observe the broken configuration.
[Collection("processes")]
public sealed class RunComposerFailureTests
{
    [Fact]
    public async Task A_memory_store_creation_failure_releases_the_workspace()
    {
        using var fx = new EngineFixture();
        using var http = new HttpClient();
        using var log = new LogHub();
        var engine = EngineComposition.Build(new AppSettings
        {
            Providers = [new ProviderConfig { Id = "fixture", BaseUrl = "http://localhost:1", Models = ["fixture"] }],
            Workers = [new WorkerConfig { Id = "fixture", Role = "fixture", Model = "fixture/fixture" }]
        }, http, log);
        var request = new RunRequest(fx.Workspace, "answer", IntentSource.CommandBar) { Defaults = new(2) };
        var oldStore = Environment.GetEnvironmentVariable("ENACTIVE_STORE");
        var oldConnection = Environment.GetEnvironmentVariable("ENACTIVE_MYSQL");
        try
        {
            Environment.SetEnvironmentVariable("ENACTIVE_STORE", "mysql");
            Environment.SetEnvironmentVariable("ENACTIVE_MYSQL", null);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunComposer.ComposeAsync(engine, request, new ParkingDecisionHandler(), CancellationToken.None));
            Assert.Contains("requires the ENACTIVE_MYSQL", error.Message);

            // A new OS handle, not a reentrant take by this process: another host must be able to start now.
            using var other = new FileStream(WorkspaceLease.PathFor(fx.Root), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ENACTIVE_STORE", oldStore);
            Environment.SetEnvironmentVariable("ENACTIVE_MYSQL", oldConnection);
        }
    }
}
