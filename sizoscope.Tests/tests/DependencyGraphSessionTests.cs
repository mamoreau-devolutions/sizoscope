using System.Collections.Concurrent;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class DependencyGraphSessionTests
{
    [Fact]
    public async Task LoadAsync_IoFailureIsReportedAndRetryRecovers()
    {
        using var graph = new OwnedGraph();
        using var session = new DependencyGraphSession(graph.Input);
        string blocked = graph.Temp.FilePath("blocked-cache");
        File.WriteAllBytes(blocked, [9, 0, 21]);

        Task failed = session.LoadAsync(cacheRoot: blocked);
        await failed;

        Assert.Equal(TaskStatus.RanToCompletion, failed.Status);
        Assert.Equal(DependencyGraphState.Failed, session.State);
        Assert.IsAssignableFrom<IOException>(session.Error);
        Assert.Throws<InvalidOperationException>(() => session.Store);
        Assert.Equal(new byte[] { 9, 0, 21 }, File.ReadAllBytes(blocked));
        graph.AssertNoPublication();
        await session.LoadAsync(cacheRoot: graph.CacheRoot);
        Assert.Equal(DependencyGraphState.Ready, session.State);
        Assert.Null(session.Error);
        OwnedGraph.AssertQueries(session.Store);
        graph.AssertPublished();
    }

    [Fact]
    public void Constructor_InitialStateAndUnavailableLoadContract()
    {
        using var graph = new OwnedGraph();
        using var session = new DependencyGraphSession(graph.Input);
        Assert.Equal(DependencyGraphState.NotRequested, session.State);
        Assert.Null(session.Error);
        Assert.Null(session.Progress);
        Assert.Throws<InvalidOperationException>(() => session.Store);
        using var unavailable = new DependencyGraphSession(null);
        Assert.Equal(DependencyGraphState.Unavailable, unavailable.State);
        Assert.Null(unavailable.Error);
        Assert.Null(unavailable.Progress);
        Assert.Throws<InvalidOperationException>(() => unavailable.Store);
        var error = Assert.Throws<InvalidOperationException>(() => { _ = unavailable.LoadAsync(cacheRoot: graph.CacheRoot); });
        Assert.Equal("The companion dependency graph was not found.", error.Message);
        graph.AssertNoPublication();
    }

    [Fact]
    public async Task LoadAsync_ReportsLoadingThenReadyWithExactStore()
    {
        using var graph = new OwnedGraph();
        using var session = new DependencyGraphSession(graph.Input);
        var events = new ConcurrentQueue<(DependencyGraphState State, string Stage)>();
        session.Changed += () => events.Enqueue((session.State, session.Progress?.Stage));
        using var gate = new SessionProgressGate(session);
        Task load = session.LoadAsync(cacheRoot: graph.CacheRoot);
        try
        {
            await gate.Reached;
            Assert.Equal(DependencyGraphState.Loading, session.State);
            Assert.Null(session.Error);
            Assert.Equal(new DependencyGraphProgress("Sorting node directory", 3, 4, graph.Input.Length), session.Progress);
            Assert.Throws<InvalidOperationException>(() => session.Store);
        }
        finally { gate.Release(); await load; }
        Assert.Equal(TaskStatus.RanToCompletion, load.Status);
        Assert.Equal(DependencyGraphState.Ready, session.State);
        Assert.Null(session.Error);
        Assert.Equal(new DependencyGraphProgress("Checksumming index", 0, 0, 0), session.Progress);
        Assert.Contains((DependencyGraphState.Loading, "Sorting node directory"), events);
        Assert.Equal(DependencyGraphState.Ready, events.Last().State);
        Assert.Contains("Index complete", events.Select(e => e.Stage));
        OwnedGraph.AssertQueries(session.Store);
        graph.AssertPublished();
    }

    [Fact]
    public async Task LoadAsync_CanceledCompletesSuccessfullyAndRetryClearsAttemptState()
    {
        using var graph = new OwnedGraph();
        using var session = new DependencyGraphSession(graph.Input);
        int armed = 1;
        session.Changed += () =>
        {
            if (session.Progress?.Stage == "Sorting node directory" && Interlocked.Exchange(ref armed, 0) == 1)
                session.Cancel();
        };
        Task first = session.LoadAsync(cacheRoot: graph.CacheRoot);
        await first;
        Assert.Equal(TaskStatus.RanToCompletion, first.Status);
        Assert.Equal(DependencyGraphState.Canceled, session.State);
        Assert.Null(session.Error);
        Assert.NotNull(session.Progress);
        graph.AssertNoPublication();
        await RetryWithResetWitness(session, graph.CacheRoot, rebuild: false);
        Assert.Equal(DependencyGraphState.Ready, session.State);
        Assert.Null(session.Error);
        OwnedGraph.AssertQueries(session.Store);
        graph.AssertPublished();
    }

    [Fact]
    public async Task LoadAsync_FailedCompletesSuccessfullyAndRebuildRetryClearsError()
    {
        using var graph = new OwnedGraph();
        using (var cold = await graph.Open()) OwnedGraph.AssertQueries(cold);
        string id = graph.Current;
        graph.CorruptManifest();
        using var session = new DependencyGraphSession(graph.Input);
        Task first = session.LoadAsync(cacheRoot: graph.CacheRoot);
        await first;
        Assert.Equal(TaskStatus.RanToCompletion, first.Status);
        Assert.Equal(DependencyGraphState.Failed, session.State);
        Assert.Equal("Incompatible dependency index. Rebuild dependencies.",
            Assert.IsType<InvalidDataException>(session.Error).Message);
        Assert.Throws<InvalidOperationException>(() => session.Store);
        Assert.Equal(id, graph.Current);
        await RetryWithResetWitness(session, graph.CacheRoot, rebuild: true);
        Assert.Equal(DependencyGraphState.Ready, session.State);
        Assert.Null(session.Error);
        Assert.NotEqual(id, graph.Current);
        OwnedGraph.AssertQueries(session.Store);
    }

    // Hold an unrelated build's budget externally, not from the callback. This makes
    // retry's initial null progress/error observable without betting on Task.Yield scheduling.
    private static async Task RetryWithResetWitness(DependencyGraphSession session, string cacheRoot, bool rebuild)
    {
        using var blockerGraph = new OwnedGraph();
        using var blocker = new DependencyGraphSession(blockerGraph.Input);
        using var gate = new SessionProgressGate(blocker);
        Task blockingLoad = blocker.LoadAsync(cacheRoot: blockerGraph.CacheRoot);
        Task retry = null;
        try
        {
            await gate.Reached;
            retry = session.LoadAsync(rebuild, cacheRoot);
            Assert.Equal(DependencyGraphState.Loading, session.State);
            Assert.Null(session.Error);
            Assert.Null(session.Progress);
        }
        finally
        {
            gate.Release();
            await blockingLoad;
            if (retry != null) await retry;
        }
        Assert.Equal(TaskStatus.RanToCompletion, retry.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_OverlappingCallsReturnSameTask(bool secondRebuild)
    {
        using var graph = new OwnedGraph();
        using var session = new DependencyGraphSession(graph.Input);
        using var gate = new SessionProgressGate(session);
        Task first = session.LoadAsync(cacheRoot: graph.CacheRoot);
        Task second = null;
        try
        {
            await gate.Reached;
            second = session.LoadAsync(secondRebuild, graph.CacheRoot);
            Assert.Equal(DependencyGraphState.Loading, session.State);
            Assert.Same(first, second);
        }
        finally
        {
            gate.Release();
            await first;
            if (second != null) await second;
        }
        Assert.Equal(DependencyGraphState.Ready, session.State);
        Assert.Null(session.Error);
        Assert.Single(Directory.GetDirectories(graph.KeyDirectory));
        OwnedGraph.AssertQueries(session.Store);
        graph.AssertPublished();
    }

    [Fact]
    public async Task LoadAsync_ReadyNoOpPreservesTaskStoreAndGeneration()
    {
        using var graph = new OwnedGraph();
        using var session = new DependencyGraphSession(graph.Input);
        await session.LoadAsync(cacheRoot: graph.CacheRoot);
        var store = session.Store;
        string id = graph.Current;
        int events = 0;
        session.Changed += () => events++;
        Task noOp = session.LoadAsync(cacheRoot: graph.CacheRoot);
        Assert.Same(Task.CompletedTask, noOp);
        session.Cancel();
        Assert.Equal(0, events);
        Assert.Equal(DependencyGraphState.Ready, session.State);
        Assert.Null(session.Error);
        Assert.Same(store, session.Store);
        Assert.Equal(id, graph.Current);
        OwnedGraph.AssertQueries(store);
    }

    [Fact]
    public async Task LoadAsync_ReadyRebuildReplacesAndDisposesPreviousStore()
    {
        using var graph = new OwnedGraph();
        using var session = new DependencyGraphSession(graph.Input);
        await session.LoadAsync(cacheRoot: graph.CacheRoot);
        var previous = session.Store;
        string id = graph.Current;
        await session.LoadAsync(rebuild: true, cacheRoot: graph.CacheRoot);
        Assert.Equal(DependencyGraphState.Ready, session.State);
        Assert.Null(session.Error);
        Assert.NotSame(previous, session.Store);
        Assert.NotEqual(id, graph.Current);
        Assert.Throws<ObjectDisposedException>(() => previous.FindNode("Node.One"));
        OwnedGraph.AssertQueries(session.Store);
        Assert.Equal((1, 1), await graph.Clear());
        Assert.False(Directory.Exists(graph.Generation(id)));
    }

    [Fact]
    public async Task LoadAsync_CanceledRebuildDoesNotPrematurelyDisposePreviousStore()
    {
        using var graph = new OwnedGraph();
        using var session = new DependencyGraphSession(graph.Input);
        await session.LoadAsync(cacheRoot: graph.CacheRoot);
        var previous = session.Store;
        string id = graph.Current;
        int armed = 1;
        session.Changed += () =>
        {
            if (session.Progress?.Stage == "Sorting node directory" && Interlocked.Exchange(ref armed, 0) == 1)
                session.Cancel();
        };
        Task rebuild = session.LoadAsync(rebuild: true, cacheRoot: graph.CacheRoot);
        await rebuild;
        Assert.Equal(TaskStatus.RanToCompletion, rebuild.Status);
        Assert.Equal(DependencyGraphState.Canceled, session.State);
        Assert.Null(session.Error);
        Assert.Same(previous, session.Store);
        Assert.Equal(id, graph.Current);
        OwnedGraph.AssertQueries(previous);
        graph.AssertNoTransient();
        await session.LoadAsync(rebuild: true, cacheRoot: graph.CacheRoot);
        Assert.Equal(DependencyGraphState.Ready, session.State);
        Assert.Null(session.Error);
        Assert.NotSame(previous, session.Store);
        Assert.NotEqual(id, graph.Current);
        Assert.Throws<ObjectDisposedException>(() => previous.FindNode("Node.One"));
        OwnedGraph.AssertQueries(session.Store);
        Assert.Equal((1, 1), await graph.Clear());
    }

    [Fact]
    public async Task LoadAsync_FailedRebuildRetainsPreviouslyLoadedStore()
    {
        using var graph = new OwnedGraph();
        using var session = new DependencyGraphSession(graph.Input);
        await session.LoadAsync(cacheRoot: graph.CacheRoot);
        var previous = session.Store;
        string id = graph.Current;
        graph.ChangeSource("timestamp");
        Task rebuild = session.LoadAsync(rebuild: true, cacheRoot: graph.CacheRoot);
        await rebuild;
        Assert.Equal(TaskStatus.RanToCompletion, rebuild.Status);
        Assert.Equal(DependencyGraphState.Failed, session.State);
        Assert.Equal("The dependency input changed. Reload the MSTAT to analyze the matching build.",
            Assert.IsType<InvalidDataException>(session.Error).Message);
        Assert.Same(previous, session.Store);
        Assert.Equal(id, graph.Current);
        OwnedGraph.AssertQueries(previous);
        graph.AssertNoTransient();
    }

    [Fact]
    public async Task Retain_ReturnsSameSessionAndSurvivesFirstOwnerRelease()
    {
        using var graph = new OwnedGraph();
        var session = new DependencyGraphSession(graph.Input);
        Assert.Same(session, session.Retain());
        int owners = 2;
        try
        {
            using var gate = new SessionProgressGate(session);
            Task load = session.LoadAsync(cacheRoot: graph.CacheRoot);
            try
            {
                await gate.Reached;
                session.Dispose();
                owners--;
                Assert.Equal(DependencyGraphState.Loading, session.State);
            }
            finally { gate.Release(); await load; }
            Assert.Equal(TaskStatus.RanToCompletion, load.Status);
            Assert.Equal(DependencyGraphState.Ready, session.State);
            var store = session.Store;
            OwnedGraph.AssertQueries(store);
            session.Dispose();
            owners--;
            AssertDisposed(session, store, graph.CacheRoot);
            Assert.Equal((1, 0), await graph.Clear());
            Assert.False(File.Exists(graph.Pointer));
        }
        finally { while (owners-- > 0) session.Dispose(); }
    }

    [Fact]
    public async Task Dispose_LastOwnerCancelsLoadingEvenWithOperationLease()
    {
        using var graph = new OwnedGraph();
        var session = new DependencyGraphSession(graph.Input);
        using var operation = session.RetainOperation();
        bool ownerReleased = false;
        try
        {
            using var gate = new SessionProgressGate(session);
            Task load = session.LoadAsync(cacheRoot: graph.CacheRoot);
            try
            {
                await gate.Reached;
                session.Dispose();
                ownerReleased = true;
            }
            finally { gate.Release(); await load; }
            Assert.Equal(TaskStatus.RanToCompletion, load.Status);
            Assert.Equal(DependencyGraphState.Canceled, session.State);
            Assert.Null(session.Error);
            graph.AssertNoPublication();
            operation.Dispose();
            operation.Dispose();
            Assert.Throws<ObjectDisposedException>(() => session.Retain());
            Assert.Throws<ObjectDisposedException>(() => session.RetainOperation());
            Assert.Throws<ObjectDisposedException>(() => { _ = session.LoadAsync(cacheRoot: graph.CacheRoot); });
            Assert.Equal((0, 0), await graph.Clear());
        }
        finally { if (!ownerReleased) session.Dispose(); }
    }

    [Fact]
    public async Task RetainOperation_IdempotentLeaseReleaseDoesNotReleaseAnotherOwner()
    {
        using var graph = new OwnedGraph();
        var session = new DependencyGraphSession(graph.Input);
        session.Retain();
        int owners = 2;
        try
        {
            await session.LoadAsync(cacheRoot: graph.CacheRoot);
            var store = session.Store;
            using var operation = session.RetainOperation();
            operation.Dispose();
            operation.Dispose();
            OwnedGraph.AssertQueries(store);
            session.Dispose();
            owners--;
            Assert.Same(store, session.Store);
            OwnedGraph.AssertQueries(session.Store);
            session.Dispose();
            owners--;
            AssertDisposed(session, store, graph.CacheRoot);
            Assert.Equal((1, 0), await graph.Clear());
        }
        finally { while (owners-- > 0) session.Dispose(); }
    }

    private static void AssertDisposed(DependencyGraphSession session, DependencyGraphIndex store, string cacheRoot)
    {
        Assert.Throws<ObjectDisposedException>(() => store.FindNode("Node.One"));
        Assert.Throws<ObjectDisposedException>(() => store.ReadIncoming(41));
        Assert.Throws<ObjectDisposedException>(() => session.Retain());
        Assert.Throws<ObjectDisposedException>(() => session.RetainOperation());
        Assert.Throws<ObjectDisposedException>(() => { _ = session.LoadAsync(cacheRoot: cacheRoot); });
    }
}
