using Xunit;

namespace Sizoscope.Tests.Fixtures;

// Real, small inputs exercise the sealed descriptor/cache boundary without production seams.
internal sealed class OwnedGraph : IDisposable
{
    public static readonly DateTime OriginalTime = new(2001, 1, 2, 3, 4, 6, DateTimeKind.Utc);
    public static readonly DateTime ChangedTime = new(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);
    public OwnedTempDirectory Temp { get; } = new(Path.GetTempPath());
    public string Source { get; }
    public string CacheRoot { get; }
    public DependencyGraphInput Input { get; }
    public string KeyDirectory => Path.Combine(CacheRoot, Input.CacheKey);
    public string Pointer => Path.Combine(KeyDirectory, "current");
    public string Current => File.ReadAllText(Pointer);
    public string Generation(string id) => Path.Combine(KeyDirectory, id);

    public OwnedGraph(int partition = 2)
    {
        Source = InputFixtureBuilder.Plain(Temp, "graph.dgml", Bytes(partition));
        SetTimestamp(Source, OriginalTime);
        Input = new DependencyGraphInput(Source);
        CacheRoot = Temp.FilePath("cache");
    }

    public static byte[] Bytes(int partition = 2) => DgmlFixtureBuilder.Bytes(
        partition == 0 ? [] : partition == 1 ? [new GraphNode(41, "Node.One")] :
        [new GraphNode(101, "Root"), new GraphNode(73, "Nœud.二"), new GraphNode(41, "Node.One")],
        partition < 2 ? [] :
        [new GraphLink(101, 41, "later"), new GraphLink(73, 41),
            new GraphLink(73, 41, "二"), new GraphLink(41, 73, "")]);

    public Task<DependencyGraphIndex> Open(bool rebuild = false, CancellationToken token = default,
        IProgress<DependencyGraphProgress> progress = null) =>
        DependencyGraphCache.OpenAsync(Input, rebuild, token, progress, CacheRoot);

    public Task<(int Removed, int InUse)> Clear(CancellationToken token = default) =>
        DependencyGraphCache.ClearInactiveAsync(token, CacheRoot);

    public static void AssertQueries(DependencyGraphIndex store, int partition = 2)
    {
        Assert.Null(store.FindNode("missing"));
        if (partition == 0)
        {
            Assert.Null(store.FindNode("Node.One"));
            Assert.Throws<InvalidDataException>(() => store.ReadIncoming(41));
            return;
        }
        Assert.Equal(new DependencyGraphNode(41, "Node.One"), store.FindNode("Node.One"));
        if (partition == 1)
        {
            Assert.Null(store.FindNode("Nœud.二"));
            var empty = store.ReadIncoming(41);
            Assert.Empty(empty.Edges);
            Assert.Null(empty.NextOffset);
            return;
        }
        Assert.Equal(new DependencyGraphNode(73, "Nœud.二"), store.FindNode("Nœud.二"));
        Assert.Equal(new DependencyGraphNode(101, "Root"), store.GetNode(101));
        var page = store.ReadIncoming(41, pageSize: 2);
        Assert.Equal(new[] { (101, "Root", "later"), (73, "Nœud.二", (string)null) },
            page.Edges.Select(e => (e.Node.Id, e.Node.Name, e.Reason)));
        Assert.Equal(2L, page.NextOffset);
        var last = store.ReadIncoming(41, 2, 2);
        Assert.Equal(new[] { (73, "Nœud.二", "二") },
            last.Edges.Select(e => (e.Node.Id, e.Node.Name, e.Reason)));
        Assert.Null(last.NextOffset);
        var terminal = store.ReadIncoming(41, 3);
        Assert.Empty(terminal.Edges);
        Assert.Null(terminal.NextOffset);
        var other = store.ReadIncoming(73);
        Assert.Equal(new[] { (41, "Node.One", "") },
            other.Edges.Select(e => (e.Node.Id, e.Node.Name, e.Reason)));
        Assert.Null(other.NextOffset);
        var root = store.ReadIncoming(101);
        Assert.Empty(root.Edges);
        Assert.Null(root.NextOffset);
    }

    public void AssertPublished()
    {
        Assert.True(Guid.TryParseExact(Current, "N", out _));
        Assert.True(Directory.Exists(Generation(Current)));
        Assert.Equal(new[] { "counts.bin", "edges.bin", "labels.bin", "lease", "manifest.bin", "nodes.bin", "strings.bin" },
            Directory.GetFiles(Generation(Current)).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));
        AssertNoTransient();
    }

    public void AssertNoTransient()
    {
        if (!Directory.Exists(CacheRoot)) return;
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot, "*.pointer", SearchOption.AllDirectories));
    }

    public void AssertNoPublication()
    {
        Assert.False(File.Exists(Pointer));
        if (Directory.Exists(KeyDirectory))
            Assert.DoesNotContain(Directory.GetDirectories(KeyDirectory), p => Guid.TryParseExact(Path.GetFileName(p), "N", out _));
        AssertNoTransient();
    }

    public void CorruptManifest()
    {
        using var file = File.Open(Path.Combine(Generation(Current), "manifest.bin"), FileMode.Open, FileAccess.Write);
        using var writer = new BinaryWriter(file);
        writer.Write(2);
    }

    public void ChangeSource(string change)
    {
        if (change == "length")
        {
            File.AppendAllText(Source, " ");
            SetTimestamp(Source, OriginalTime);
        }
        else if (change == "timestamp") SetTimestamp(Source, ChangedTime);
        else if (change == "delete") File.Delete(Source);
        else throw new ArgumentException("Unknown source change.", nameof(change));
    }

    public static void SetTimestamp(string path, DateTime timestamp)
    {
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    public void Dispose() => Temp.Dispose();
}
