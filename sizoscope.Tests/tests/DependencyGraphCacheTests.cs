using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class DependencyGraphCacheTests
{
    [Theory]
    [InlineData(32, false)]
    [InlineData(64, false)]
    [InlineData(65, true)]
    public async Task OpenAsync_BoundsPointerBeforeReadingAndRebuildRecovers(int length, bool rejected)
    {
        using var graph = new OwnedGraph();
        using (var cold = await graph.Open()) OwnedGraph.AssertQueries(cold);
        string id = graph.Current;
        File.WriteAllText(graph.Pointer, id + new string(' ', length - id.Length));

        if (rejected)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => graph.Open());
            Assert.Equal("Invalid dependency cache pointer. Rebuild dependencies.", error.Message);
            graph.AssertNoTransient();
            using var rebuilt = await graph.Open(rebuild: true);
            Assert.NotEqual(id, graph.Current);
            OwnedGraph.AssertQueries(rebuilt);
        }
        else
        {
            using var warm = await graph.Open();
            OwnedGraph.AssertQueries(warm);
            Assert.Equal(id, graph.Current.Trim());
            Assert.Single(Directory.GetDirectories(graph.KeyDirectory));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OpenAsync_ColdBuildPublishesCompleteQueryableIndex(int partition)
    {
        using var graph = new OwnedGraph(partition);
        using var store = await graph.Open();
        OwnedGraph.AssertQueries(store, partition);
        graph.AssertPublished();
    }

    [Fact]
    public async Task OpenAsync_WarmReuseDoesNotOpenSourceOrImport()
    {
        using var graph = new OwnedGraph();
        using (var cold = await graph.Open()) OwnedGraph.AssertQueries(cold);
        string generation = graph.Current;
        var stages = new List<string>();
        using var exclusive = File.Open(graph.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var warm = await graph.Open(progress: new SynchronousProgress<DependencyGraphProgress>(p => stages.Add(p.Stage)));
        OwnedGraph.AssertQueries(warm);
        Assert.Equal(generation, graph.Current);
        Assert.Equal(Enumerable.Repeat("Verifying cached index", 5), stages);
        graph.AssertPublished();
    }

    [Fact]
    public async Task OpenAsync_RebuildPreservesLeasedOldGeneration()
    {
        using var graph = new OwnedGraph();
        using var old = await graph.Open();
        string oldId = graph.Current;
        using var current = await graph.Open(rebuild: true);
        Assert.NotEqual(oldId, graph.Current);
        Assert.True(Directory.Exists(graph.Generation(oldId)));
        OwnedGraph.AssertQueries(old);
        OwnedGraph.AssertQueries(current);
        graph.AssertPublished();
    }

    [Fact]
    public async Task ClearInactiveAsync_CountsGenerationsNotReaders()
    {
        using var graph = new OwnedGraph();
        using var first = await graph.Open();
        using var second = await graph.Open();
        string id = graph.Current;
        Assert.Equal((0, 1), await graph.Clear());
        Assert.Equal(id, graph.Current);
        OwnedGraph.AssertQueries(first);
        OwnedGraph.AssertQueries(second);
        first.Dispose();
        Assert.Equal((0, 1), await graph.Clear());
        OwnedGraph.AssertQueries(second);
        second.Dispose();
        Assert.Equal((1, 0), await graph.Clear());
        Assert.False(Directory.Exists(graph.Generation(id)));
        Assert.False(File.Exists(graph.Pointer));
        Assert.Equal((0, 0), await graph.Clear());
    }

    [Fact]
    public async Task ClearInactiveAsync_RebuiltGenerationsRespectIndependentLeases()
    {
        using var graph = new OwnedGraph();
        using var old = await graph.Open();
        string oldId = graph.Current;
        using var current = await graph.Open(rebuild: true);
        string currentId = graph.Current;
        Assert.Equal((0, 2), await graph.Clear());
        OwnedGraph.AssertQueries(old);
        OwnedGraph.AssertQueries(current);
        old.Dispose();
        Assert.Equal((1, 1), await graph.Clear());
        Assert.False(Directory.Exists(graph.Generation(oldId)));
        Assert.Equal(currentId, graph.Current);
        OwnedGraph.AssertQueries(current);
        current.Dispose();
        Assert.Equal((1, 0), await graph.Clear());
        Assert.False(Directory.Exists(graph.Generation(currentId)));
        Assert.False(File.Exists(graph.Pointer));
        Assert.Equal((0, 0), await graph.Clear());
    }

    [Fact]
    public async Task ClearInactiveAsync_AbsentEmptyAndUnownedPathsAreUntouched()
    {
        using var graph = new OwnedGraph();
        Assert.Equal((0, 0), await graph.Clear());
        Assert.False(Directory.Exists(graph.CacheRoot));
        Directory.CreateDirectory(graph.CacheRoot);
        Assert.Equal((0, 0), await graph.Clear());
        // Include valid-looking generations under invalid/unowned keys, and ignored entries under an owned key.
        string id = "0123456789abcdef0123456789abcdef";
        string[] directories =
        [
            Path.Combine(graph.CacheRoot, "not-a-key", id),
            Path.Combine(graph.CacheRoot, new string('A', 64), id),
            Path.Combine(graph.KeyDirectory, id + ".tmp"),
            Path.Combine(graph.KeyDirectory, "not-a-generation")
        ];
        foreach (string directory in directories)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "sentinel"), [7, 0, 255, 19]);
        }
        File.WriteAllText(Path.Combine(graph.KeyDirectory, "owner"), "Sizoscope dependency graph cache v1");
        Assert.Equal((0, 0), await graph.Clear());
        foreach (string directory in directories)
            Assert.Equal(new byte[] { 7, 0, 255, 19 }, File.ReadAllBytes(Path.Combine(directory, "sentinel")));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("timestamp")]
    [InlineData("delete")]
    public async Task OpenAsync_SourceChangedBeforeWorkRejectsWithoutPublication(string change)
    {
        using var graph = new OwnedGraph();
        graph.ChangeSource(change);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => graph.Open());
        Assert.Equal("The dependency input changed. Reload the MSTAT to analyze the matching build.", error.Message);
        graph.AssertNoPublication();
    }

    [Theory]
    [InlineData("length")]
    [InlineData("timestamp")]
    public async Task OpenAsync_WarmVerificationRechecksChangedSource(string change)
    {
        using var graph = new OwnedGraph();
        using (var cold = await graph.Open()) OwnedGraph.AssertQueries(cold);
        string id = graph.Current;
        int mutations = 0;
        var progress = new SynchronousProgress<DependencyGraphProgress>(p =>
        {
            if (p.Stage == "Verifying cached index" && mutations++ == 0) graph.ChangeSource(change);
        });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => graph.Open(progress: progress));
        Assert.Equal("The dependency input changed. Reload the MSTAT to analyze the matching build.", error.Message);
        Assert.Equal(5, mutations);
        Assert.Equal(id, graph.Current);
        Assert.Single(Directory.GetDirectories(graph.KeyDirectory));
        graph.AssertNoTransient();
    }

    [Fact]
    public async Task OpenAsync_CorruptManifestRejectsThenExplicitRebuildRecovers()
    {
        using var graph = new OwnedGraph();
        using (var cold = await graph.Open()) OwnedGraph.AssertQueries(cold);
        string id = graph.Current;
        graph.CorruptManifest();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => graph.Open());
        Assert.Equal("Incompatible dependency index. Rebuild dependencies.", error.Message);
        Assert.Equal(id, graph.Current);
        graph.AssertNoTransient();
        using var recovered = await graph.Open(rebuild: true);
        Assert.NotEqual(id, graph.Current);
        OwnedGraph.AssertQueries(recovered);
        graph.AssertPublished();
    }

    [Fact]
    public async Task OpenAsync_SameLengthCorruptDataRejectsThenExplicitRebuildRecovers()
    {
        using var graph = new OwnedGraph();
        using (var cold = await graph.Open()) OwnedGraph.AssertQueries(cold);
        string id = graph.Current;
        string path = Path.Combine(graph.Generation(id), "strings.bin");
        byte[] bytes = File.ReadAllBytes(path);
        long length = bytes.LongLength;
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Equal(length, new FileInfo(path).Length);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => graph.Open());
        Assert.Equal("Corrupt dependency index (strings.bin). Rebuild dependencies.", error.Message);
        Assert.Equal(id, graph.Current);
        graph.AssertNoTransient();
        using var recovered = await graph.Open(rebuild: true);
        Assert.NotEqual(id, graph.Current);
        OwnedGraph.AssertQueries(recovered);
        graph.AssertPublished();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_ColdCancellationCleansStagingAndReleasesBuildBudget(bool preCanceled)
    {
        using var graph = new OwnedGraph();
        using var cancellation = new CancellationTokenSource();
        bool reached = false;
        if (preCanceled) cancellation.Cancel();
        var progress = new SynchronousProgress<DependencyGraphProgress>(p =>
        {
            if (p.Stage == "Sorting node directory")
            {
                reached = true;
                cancellation.Cancel();
            }
        });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            graph.Open(token: cancellation.Token, progress: progress));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(!preCanceled, reached);
        graph.AssertNoPublication();
        using var retry = await graph.Open();
        OwnedGraph.AssertQueries(retry);
        graph.AssertPublished();
    }

    [Fact]
    public async Task ClearInactiveAsync_CancellationIsNotReportedAsSuccessfulCleanup()
    {
        using var graph = new OwnedGraph();
        using (var cold = await graph.Open()) OwnedGraph.AssertQueries(cold);
        string sentinel = Path.Combine(graph.CacheRoot, "sentinel");
        File.WriteAllBytes(sentinel, [9, 0, 21]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => graph.Clear(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal((1, 0), await graph.Clear());
        Assert.Equal(new byte[] { 9, 0, 21 }, File.ReadAllBytes(sentinel));
        Assert.False(File.Exists(graph.Pointer));
    }

    [Fact]
    public async Task OpenAsync_ColdTimestampChangeRejectsAfterBuild()
    {
        using var graph = new OwnedGraph();
        // Probe exactly the descriptor's sharing mode before relying on metadata mutation in the hook.
        using (var reader = graph.Input.Open())
        {
            OwnedGraph.SetTimestamp(graph.Source, OwnedGraph.ChangedTime);
            OwnedGraph.SetTimestamp(graph.Source, OwnedGraph.OriginalTime);
        }
        bool mutated = false;
        var progress = new SynchronousProgress<DependencyGraphProgress>(p =>
        {
            if (p.Stage != "Sorting node directory") return;
            OwnedGraph.SetTimestamp(graph.Source, OwnedGraph.ChangedTime);
            mutated = true;
        });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => graph.Open(progress: progress));
        Assert.True(mutated);
        Assert.Equal("The dependency input changed. Reload the MSTAT to analyze the matching build.", error.Message);
        graph.AssertNoPublication();
    }
}
