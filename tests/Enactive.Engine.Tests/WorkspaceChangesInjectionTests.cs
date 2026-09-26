namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Xunit;

public sealed class WorkspaceChangesInjectionTests
{
    [Theory]
    [InlineData("quick_action")]
    [InlineData("task")]
    public async Task The_engine_uses_and_disposes_the_injected_snapshot_session(string disposition)
    {
        using var fx = new EngineFixture();
        var factory = new RecordingFactory();
        fx.WorkspaceChangesOverride = factory;
        var provider = new FakeChatProvider(Turn.Says(
            "{\"disposition\":\"" + disposition + "\",\"title\":\"inspect\",\"steps\":[\"inspect\"]}"))
            { WhenExhausted = Turn.Says("done") };
        await fx.RunAsync(fx.Build(provider), "inspect");
        Assert.Equal(fx.Root, factory.Root);
        Assert.NotNull(factory.Session);
        Assert.Equal(2, factory.Session.Takes); // one start boundary, one terminal boundary
        Assert.Equal(1, factory.Session.PathComparisons);
        Assert.Equal(0, factory.Session.FullComparisons);
        Assert.True(factory.Session.Disposed);
    }

    private sealed class RecordingFactory : IWorkspaceChangesFactory
    {
        public string? Root;
        public Session? Session;
        public IWorkspaceChanges Create(string root)
        {
            Root = root;
            return Session = new Session();
        }
    }
    private sealed class Session : IWorkspaceChanges
    {
        public int Takes;
        public int PathComparisons;
        public int FullComparisons;
        public bool Disposed;
        public Task<WorkspaceSnapshot?> TakeAsync(CancellationToken ct)
        {
            Takes++;
            return Task.FromResult<WorkspaceSnapshot?>(new WorkspaceSnapshot(null,
                new Dictionary<string, (long, long, string?)>()));
        }
        public Task<IReadOnlySet<string>?> PathsAsync(WorkspaceSnapshot snapshot, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>());
        public Task<IReadOnlyList<FileChange>?> CompareAsync(WorkspaceSnapshot before, WorkspaceSnapshot after, CancellationToken ct)
        {
            FullComparisons++;
            return Task.FromResult<IReadOnlyList<FileChange>?>([]);
        }
        public Task<IReadOnlyList<FileChange>?> ComparePathsAsync(WorkspaceSnapshot before, WorkspaceSnapshot after, CancellationToken ct)
        {
            PathComparisons++;
            return Task.FromResult<IReadOnlyList<FileChange>?>([]);
        }
        public void Dispose() => Disposed = true;
    }
}
