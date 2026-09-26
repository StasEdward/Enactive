namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Templates;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Reported by Stas, 2026-09-07: "нажатие на кнопку Save подвешивает приложение."
///
/// <para>Not a slow save — a deadlock, with no exception and nothing in the log. The window simply
/// stopped.</para>
///
/// <para><c>AtomicWrite.Replace(path, content)</c> looked synchronous and was not: it called the
/// async overload and blocked on <c>GetAwaiter().GetResult()</c>. On a thread that has a
/// SynchronizationContext — which is to say the UI thread — <c>FileStream.DisposeAsync</c> flushes
/// the buffered bytes, and a flush that does not finish synchronously posts its continuation back to
/// that context. The context is the dispatcher. The dispatcher is blocked inside
/// <c>GetResult()</c> waiting for exactly that continuation. Nothing moves again.</para>
///
/// <para>It had never fired because nothing on the UI thread had ever written a file this way. The
/// templates editor was the first, and <c>StagingArtifactStore.Apply</c> — the Apply button on a
/// staged change — has been one unlucky flush away from it for as long as it has existed.</para>
///
/// <para>The test reproduces the UI thread rather than describing it: a single-threaded
/// SynchronizationContext whose posted work is only ever run by the same thread. If a continuation
/// is posted while that thread is blocked, it never runs, and the call never returns.</para>
/// </summary>
public sealed class SyncOverAsyncTests : IDisposable
{
    private readonly string _root;

    public SyncOverAsyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>
    /// A UI thread, near enough: one thread, a queue, and work posted to it runs only when that
    /// thread is free to run it. Blocking that thread and posting to it is the deadlock.
    /// </summary>
    private sealed class OneThreadContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Work, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>
        /// Runs <paramref name="body"/> on the one thread, with this as its context. Returns false
        /// when it did not finish inside the timeout, which for this shape means it deadlocked.
        /// </summary>
        public static bool Run(Action body, TimeSpan timeout)
        {
            var context = new OneThreadContext();
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                SetSynchronizationContext(context);
                try { body(); }
                catch (Exception ex) { failure = ex; }
            })
            { IsBackground = true };

            thread.Start();
            var finished = thread.Join(timeout);

            if (failure is not null)
                throw failure;

            return finished;
        }
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ── the write itself ────────────────────────────────────────────────────

    /// <summary>
    /// The whole defect in one assertion. Ten seconds is not "slow"; a file write that has not
    /// finished by then is not going to.
    /// </summary>
    [Fact]
    public void Writing_a_file_from_a_thread_with_a_dispatcher_returns()
    {
        var path = Path.Combine(_root, "settings.json");

        var finished = OneThreadContext.Run(
            () => AtomicWrite.Replace(path, new string('x', 200_000)),
            Patience);

        Assert.True(finished,
            "AtomicWrite.Replace did not return on a thread with a SynchronizationContext. "
            + "That is the sync-over-async deadlock: the flush posts its continuation to the "
            + "context, and the context is blocked waiting for it.");
        Assert.Equal(200_000, new FileInfo(path).Length);
    }

    /// <summary>
    /// Enough content to guarantee buffered bytes at dispose, because that is what makes the flush
    /// go asynchronous. A short string can complete synchronously and hide the bug — which is
    /// exactly how this survived: every existing caller wrote from a thread pool thread, where
    /// there is no context to post to and no deadlock to have.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4_096)]
    [InlineData(1_000_000)]
    public void Any_size_of_file_returns(int size)
    {
        var path = Path.Combine(_root, $"file-{size}.txt");

        Assert.True(
            OneThreadContext.Run(() => AtomicWrite.Replace(path, new string('y', size)), Patience),
            $"a {size}-character write did not return");
    }

    // ── and the two paths that reach it ─────────────────────────────────────

    /// <summary>
    /// Saving a template, which is the button that was pressed. It goes through the same write, and
    /// asserting on the store rather than only on AtomicWrite is what keeps the fix attached to the
    /// thing the user did.
    /// </summary>
    [Fact]
    public void Saving_a_template_from_a_thread_with_a_dispatcher_returns()
    {
        var store = new TemplateStore(_root, Path.Combine(_root, "global"));
        var template = new TaskTemplate(
            Id: "my-check",
            Name: "My Check",
            Goal: "Do the thing to {area}, thoroughly.\n" + new string('.', 20_000),
            Parameters: new[] { new TemplateParameter("area", "Area", TemplateParameterType.Text) });

        Assert.True(
            OneThreadContext.Run(() => store.Save(template, TemplateScope.Global), Patience),
            "TemplateStore.Save did not return on a thread with a SynchronizationContext.");

        Assert.Equal("My Check", store.Find("my-check")!.Name);
    }

    /// <summary>
    /// Applying a staged change — the other caller of the synchronous write, and one that has been
    /// on the UI thread since it was written. It never hung only because it was lucky.
    /// </summary>
    [Fact]
    public async Task Applying_a_staged_change_from_a_thread_with_a_dispatcher_returns()
    {
        var staging = new StagingArtifactStore(_root);
        var body = new string('z', 100_000);

        await staging.CreateAsync(
            "notes.md", Core.Artifacts.ArtifactKind.FileSet, "notes.md",
            async stream => await stream.WriteAsync(Encoding.UTF8.GetBytes(body)),
            CancellationToken.None);

        var change = staging.Changes.Single();

        ApplyResult? result = null;
        Assert.True(
            OneThreadContext.Run(() => result = staging.Apply(change.Id), Patience),
            "StagingArtifactStore.Apply did not return on a thread with a SynchronizationContext.");

        Assert.True(result!.Applied, result.Conflict);
        Assert.Equal(body, File.ReadAllText(Path.Combine(_root, "notes.md")));
    }

    // ── the guard against the shape coming back ─────────────────────────────

    /// <summary>
    /// The general rule, so the next one is caught by a build rather than by a frozen window: no
    /// synchronous method in the Workspace layer blocks on a Task.
    ///
    /// <para>Reflection cannot see <c>GetAwaiter().GetResult()</c> in a method body, so this reads
    /// the source instead. A check that cannot find the source FAILS rather than passing: a guard
    /// that quietly checks nothing is worse than no guard, and it is the shape that let every one of
    /// these defects ship.</para>
    /// </summary>
    [Fact]
    public void No_synchronous_workspace_method_blocks_on_a_task()
    {
        var source = Sources("Enactive.Workspace");
        Assert.True(source.Count > 0,
            "the source tree was not found next to the test assembly, so this guard checked nothing");

        var offenders = source
            .Where(file => File.ReadAllText(file.Value) is { } text
                           && (text.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal)
                               || text.Contains(".Wait();", StringComparison.Ordinal)))
            .Select(file => file.Key)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "These block a thread on a Task: " + string.Join(", ", offenders)
            + ". On a thread with a SynchronizationContext that deadlocks the moment the awaited "
            + "work does not complete synchronously — a hung window with no exception. Write the "
            + "synchronous path synchronously instead.");
    }

    /// <summary>The .cs files of one project, by file name, requiring the source checkout to be available.</summary>
    private static Dictionary<string, string> Sources(string project)
    {
        var root = TestRepository.Root;

        return Directory
            .GetFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetFileName(f)!, f => f);
    }
}
