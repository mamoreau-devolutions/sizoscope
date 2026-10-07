using System.Text;
using System.Xml;
using sizoscope;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class DependencyGraphIndexTests
{
    private static readonly GraphNode[] Pair = { new(1, "source"), new(2, "target") };

    private static string Build(OwnedTempDirectory temp, IEnumerable<GraphNode> nodes,
        IEnumerable<GraphLink> links, int buffer = 2)
    {
        string directory = temp.FilePath("index");
        using var xml = new MemoryStream(DgmlFixtureBuilder.Bytes(nodes, links));
        DependencyGraphIndex.Build(xml, directory, sortBufferRecords: buffer);
        return directory;
    }

    private static (int Id, string Name, string Reason)[] Values(DependencyGraphPage page) =>
        page.Edges.Select(edge => (edge.Node.Id, edge.Node.Name, edge.Reason)).ToArray();

    [Fact]
    public void FindNode_HashCollisionStillComparesFullLabelsAndPreservesLastDefinition()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string directory = Build(temp,
            [new GraphNode(1, "desired"), new GraphNode(3, "desired"), new GraphNode(2, "different")],
            []);
        string labels = Path.Combine(directory, "labels.bin");
        long hash = Enumerable.Range(0, 3)
            .Where(i => IndexBinaryFixture.ReadField(labels, i, IndexBinaryFixture.ValueField) == 3)
            .Select(i => IndexBinaryFixture.ReadField(labels, i, 0))
            .Single();
        string nodes = Path.Combine(directory, "nodes.bin");
        long first = IndexBinaryFixture.ReadField(nodes, 0, IndexBinaryFixture.ValueField);
        long different = IndexBinaryFixture.ReadField(nodes, 1, IndexBinaryFixture.ValueField);
        long last = IndexBinaryFixture.ReadField(nodes, 2, IndexBinaryFixture.ValueField);
        // Force the lookup bucket to contain a later, different full label.
        using (var writer = new BinaryWriter(File.Open(labels, FileMode.Create, FileAccess.Write)))
        {
            new DependencyGraphIndex.Record(hash, 1, first, 0).Write(writer);
            new DependencyGraphIndex.Record(hash, 3, last, 1).Write(writer);
            new DependencyGraphIndex.Record(hash, 2, different, 2).Write(writer);
        }
        using var index = new DependencyGraphIndex(directory);

        Assert.Equal(new DependencyGraphNode(3, "desired"), index.FindNode("desired"));
        Assert.Equal(new DependencyGraphNode(2, "different"), index.GetNode(2));
        Assert.Equal(new DependencyGraphNode(1, "desired"), index.GetNode(1));
    }

    [Fact]
    public void Build_EmptyGraphReopensWithNoLookupResults()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, Array.Empty<GraphNode>(), Array.Empty<GraphLink>());
        using var index = new DependencyGraphIndex(path);
        Assert.Null(index.FindNode(""));
        Assert.Null(index.FindNode("absent"));
        Assert.Throws<InvalidDataException>(() => index.GetNode(0));
    }

    [Fact]
    public void Build_SparseUnorderedIdsAndForwardReferencesResolveExactly()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var nodes = new GraphNode[] { new(int.MaxValue, "last"), new(901, "middle"), new(0, "zero") };
        using var xml = new MemoryStream(DgmlFixtureBuilder.Bytes(nodes,
            new GraphLink[] { new(901, int.MaxValue, "forward"), new(0, 901, "zero source") }, linksFirst: true));
        string directory = temp.FilePath("index");
        DependencyGraphIndex.Build(xml, directory, sortBufferRecords: 1);
        using var index = new DependencyGraphIndex(directory);
        foreach (var node in nodes)
        {
            Assert.Equal(new DependencyGraphNode(node.Id, node.Name), index.GetNode(node.Id));
            Assert.Equal(new DependencyGraphNode(node.Id, node.Name), index.FindNode(node.Name));
        }
        Assert.Equal(new[] { (901, "middle", "forward") }, Values(index.ReadIncoming(int.MaxValue)));
        Assert.Equal(new[] { (0, "zero", "zero source") }, Values(index.ReadIncoming(901)));
        Assert.Empty(index.ReadIncoming(0).Edges);
    }

    [Theory]
    [InlineData("Exact", 3)]
    [InlineData("exact", null)]
    [InlineData("EXACT", null)]
    [InlineData("absent", null)]
    [InlineData("Exact ", null)]
    [InlineData("", 7)]
    [InlineData("λ雪🚀", 11)]
    [InlineData("é", 13)]
    [InlineData("e\u0301", null)]
    public void FindNode_UsesOrdinalExactMatchAndReturnsNullForAbsence(string name, int? id)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, new GraphNode[] { new(3, "Exact"), new(7, ""), new(11, "λ雪🚀"), new(13, "é") },
            Array.Empty<GraphLink>());
        using var index = new DependencyGraphIndex(path);
        if (id.HasValue) Assert.Equal(new DependencyGraphNode(id.Value, name), index.FindNode(name));
        else Assert.Null(index.FindNode(name));
    }

    [Fact]
    public void FindNode_RepeatedLabelsChooseLastDeclaration()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, new GraphNode[] { new(9, "repeat"), new(42, "repeat"), new(2, "repeat") },
            Array.Empty<GraphLink>(), buffer: 1);
        using var index = new DependencyGraphIndex(path);
        Assert.Equal(new DependencyGraphNode(2, "repeat"), index.FindNode("repeat"));
        Assert.Equal(new DependencyGraphNode(42, "repeat"), index.GetNode(42));
        Assert.Equal(new DependencyGraphNode(9, "repeat"), index.GetNode(9));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void GetNode_AbsentIdThrowsInvalidDataException(int id)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var index = new DependencyGraphIndex(Build(temp, Pair, Array.Empty<GraphLink>()));
        Assert.Throws<InvalidDataException>(() => index.GetNode(id));
        Assert.Throws<InvalidDataException>(() => index.ReadIncoming(id));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("http://schemas.microsoft.com/vs/2009/dgml", false)]
    [InlineData("http://schemas.microsoft.com/vs/2009/dgml", true)]
    public void Build_EntitiesNonAsciiNamespacesAndPairedTagsDecodeExactly(string ns, bool paired)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        const string name = "雪 & <A> \"Ω\" '🚀'";
        const string reason = "référence & <B> \"λ\" '理由'";
        using var xml = new MemoryStream(DgmlFixtureBuilder.Bytes(
            new GraphNode[] { new(8, name), new(2, "root") },
            new GraphLink[] { new(8, 2, reason) }, ns, paired));
        string path = temp.FilePath("index");
        DependencyGraphIndex.Build(xml, path, sortBufferRecords: 1);
        using var index = new DependencyGraphIndex(path);
        Assert.Equal(new DependencyGraphNode(8, name), index.GetNode(8));
        Assert.Equal(new DependencyGraphNode(8, name), index.FindNode(name));
        Assert.Equal(new[] { (8, name, reason) }, Values(index.ReadIncoming(2)));
        Assert.Null(index.ReadIncoming(2).NextOffset);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("plain")]
    [InlineData("zip")]
    public void Build_PlainAndZipCompanionStreamsProduceEquivalentQueries(string kind)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] bytes = DgmlFixtureBuilder.Bytes(Pair, new GraphLink[] { new(1, 2, "理由") });
        string directory = temp.FilePath("index");
        if (kind == "direct")
        {
            string file = InputFixtureBuilder.Plain(temp, "graph.dgml", bytes);
            using var xml = File.OpenRead(file);
            DependencyGraphIndex.Build(xml, directory, sortBufferRecords: 2);
        }
        else
        {
            string path = kind == "zip"
                ? InputFixtureBuilder.Zip(temp, "app.zip", ("app.mstat", new byte[] { 7 }),
                    ("app.scan.dgml.xml", bytes))
                : InputFixtureBuilder.Plain(temp, "app.mstat", new byte[] { 7 });
            if (kind == "plain") File.WriteAllBytes(Path.ChangeExtension(path, "scan.dgml.xml"), bytes);
            using var resolved = ResolvedFile.Open(path);
            using var xml = resolved.OpenDgml();
            DependencyGraphIndex.Build(xml, directory, sortBufferRecords: 2);
        }
        using var index = new DependencyGraphIndex(directory);
        Assert.Equal(new DependencyGraphNode(2, "target"), index.FindNode("target"));
        Assert.Equal(new[] { (1, "source", "理由") }, Values(index.ReadIncoming(2)));
        Assert.Null(index.ReadIncoming(2).NextOffset);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_TinySortBuffersPreserveAllRecordsAcrossMultipleMergePasses(int buffer)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        // 67 nodes/labels and 121 edges force >16 runs at BOTH capacities (merge fan-in is 16).
        var nodes = Enumerable.Range(0, 67).Reverse().Select(i => new GraphNode(i * 17, "label-" + i % 7)).ToArray();
        var links = Enumerable.Range(0, 121)
            .Select(i => new GraphLink(((120 - i / 2) % 67) * 17, 34, "repeat-" + i / 2 % 5)).ToArray();
        string path = Build(temp, nodes, links, buffer);
        using var index = new DependencyGraphIndex(path);
        foreach (var node in nodes) Assert.Equal(new DependencyGraphNode(node.Id, node.Name), index.GetNode(node.Id));
        for (int i = 0; i < 7; i++)
            Assert.Equal(new DependencyGraphNode(i * 17, "label-" + i), index.FindNode("label-" + i));
        Assert.Equal(links.Select(link => (link.Source, "label-" + link.Source / 17 % 7, link.Reason)).ToArray(),
            Values(index.ReadIncoming(34)));
        Assert.Null(index.ReadIncoming(34).NextOffset);
        Assert.Empty(index.ReadIncoming(0).Edges);
    }

    [Fact]
    public void ReadIncoming_PreservesRepeatedEdgesAndDeclarationOrder()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, new GraphNode[] { new(9, "nine"), new(2, "two"), new(5, "five") },
            new GraphLink[] { new(9, 5, "a"), new(2, 5, "b"), new(9, 5, "a"), new(9, 5, "c"), new(2, 9, "other") });
        using var index = new DependencyGraphIndex(path);
        Assert.Equal(new[] { (9, "nine", "a"), (2, "two", "b"), (9, "nine", "a"), (9, "nine", "c") },
            Values(index.ReadIncoming(5)));
        Assert.Equal(new[] { (2, "two", "other") }, Values(index.ReadIncoming(9)));
    }

    [Fact]
    public void ReadIncoming_PreservesMissingAndEmptyReasons()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, Pair, new GraphLink[] { new(1, 2), new(1, 2, ""), new(1, 2, "why"), new(1, 2, "雪 & <") });
        using var index = new DependencyGraphIndex(path);
        Assert.Equal(new[] { (1, "source", (string)null), (1, "source", ""), (1, "source", "why"), (1, "source", "雪 & <") },
            Values(index.ReadIncoming(2)));
    }

    [Fact]
    public void ReadIncoming_SharedBranchesAndCyclesRemainInAdjacency()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, new GraphNode[] { new(0, "root"), new(1, "left"), new(2, "right"), new(3, "shared") },
            new GraphLink[] { new(1, 0, "left"), new(2, 0, "right"), new(3, 1, "shared-left"),
                new(3, 2, "shared-right"), new(0, 3, "cycle"), new(3, 3, "self") });
        using var index = new DependencyGraphIndex(path);
        Assert.Equal(new[] { (1, "left", "left"), (2, "right", "right") }, Values(index.ReadIncoming(0)));
        Assert.Equal(new[] { (3, "shared", "shared-left") }, Values(index.ReadIncoming(1)));
        Assert.Equal(new[] { (3, "shared", "shared-right") }, Values(index.ReadIncoming(2)));
        Assert.Equal(new[] { (0, "root", "cycle"), (3, "shared", "self") }, Values(index.ReadIncoming(3)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(200)]
    public void ReadIncoming_AllPagesMatchCompleteExpectedSequence(int pageSize)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var links = Enumerable.Range(0, 413).Select(i => new GraphLink(i % 2 + 1, 2, "why-" + i % 9)).ToArray();
        using var index = new DependencyGraphIndex(Build(temp, Pair, links));
        var actual = new List<(int, string, string)>();
        long offset = 0;
        while (true)
        {
            var page = index.ReadIncoming(2, offset, pageSize);
            int expectedLength = Math.Min(pageSize, 413 - checked((int)offset));
            Assert.Equal(expectedLength, page.Edges.Count);
            long next = offset + expectedLength;
            Assert.Equal(next < 413 ? (long?)next : null, page.NextOffset);
            actual.AddRange(Values(page));
            if (next == 413) break;
            offset = page.NextOffset.Value;
        }
        Assert.Equal(links.Select(link => (link.Source, link.Source == 1 ? "source" : "target", link.Reason)), actual);
        var defaultPage = index.ReadIncoming(2);
        Assert.Equal(200, defaultPage.Edges.Count);
        Assert.Equal(200L, defaultPage.NextOffset);
    }

    [Theory]
    [InlineData(0, 2, 2)]
    [InlineData(2, 2, 4)]
    [InlineData(4, 1, -1)]
    [InlineData(5, 0, -1)]
    public void ReadIncoming_OffsetBoundariesReturnCorrectSuffixAndContinuation(long offset, int count, long next)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var links = Enumerable.Range(0, 5).Select(i => new GraphLink(1, int.MaxValue, "edge-" + i)).ToArray();
        using var index = new DependencyGraphIndex(Build(temp,
            new GraphNode[] { new(1, "source"), new(int.MaxValue, "max") }, links));
        var page = index.ReadIncoming(int.MaxValue, offset, 2);
        Assert.Equal(links.Skip((int)offset).Take(count).Select(link => (1, "source", link.Reason)), Values(page));
        Assert.Equal(next < 0 ? (long?)null : next, page.NextOffset);
        Assert.Empty(index.ReadIncoming(1).Edges);
        Assert.Null(index.ReadIncoming(1).NextOffset);
    }

    [Fact]
    public void ReadIncoming_BeyondEndRejectsRatherThanWrappingOffset()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var index = new DependencyGraphIndex(Build(temp, Pair, new GraphLink[] { new(1, 2, "one") }));
        foreach (long offset in new[] { 2L, long.MaxValue })
            Assert.Equal("offset", Assert.Throws<ArgumentOutOfRangeException>(() => index.ReadIncoming(2, offset)).ParamName);
        Assert.Equal("offset", Assert.Throws<ArgumentOutOfRangeException>(() => index.ReadIncoming(1, 1)).ParamName);
    }

    [Theory]
    [InlineData(-1, 200)]
    [InlineData(0, -1)]
    [InlineData(0, 0)]
    [InlineData(0, 201)]
    [InlineData(0, 202)]
    public void ReadIncoming_InvalidArgumentsReportCurrentGuard(long offset, int pageSize)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var index = new DependencyGraphIndex(Build(temp, Pair, Array.Empty<GraphLink>()));
        Assert.Equal("offset", Assert.Throws<ArgumentOutOfRangeException>(() => index.ReadIncoming(2, offset, pageSize)).ParamName);

        using var populatedTemp = new OwnedTempDirectory(Path.GetTempPath());
        using var populated = new DependencyGraphIndex(Build(populatedTemp, Pair, new GraphLink[] { new(1, 2, "one") }));
        Assert.Equal(new[] { (1, "source", "one") }, Values(populated.ReadIncoming(2, 0, 200)));
        Assert.Equal("offset", Assert.Throws<ArgumentOutOfRangeException>(
            () => populated.ReadIncoming(2, offset, pageSize)).ParamName);
    }

    [Fact]
    public void Dispose_IsIdempotentAndQueriesThrowObjectDisposedException()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var index = new DependencyGraphIndex(Build(temp, Pair, new GraphLink[] { new(1, 2, "one") }));
        index.Dispose();
        index.Dispose();
        Assert.Throws<ObjectDisposedException>(() => index.FindNode("source"));
        Assert.Throws<ObjectDisposedException>(() => index.GetNode(1));
        Assert.Throws<ObjectDisposedException>(() => index.ReadIncoming(2));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("duplicate-no-links")]
    [InlineData("source")]
    [InlineData("target")]
    public void Build_DuplicateIdsAndDanglingReferencesThrowInvalidDataException(string kind)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var nodes = kind.StartsWith("duplicate", StringComparison.Ordinal)
            ? new GraphNode[] { new(1, "a"), new(2, "b"), new(1, "c") } : Pair;
        var links = kind == "duplicate-no-links" ? Array.Empty<GraphLink>()
            : new GraphLink[] { new(kind == "source" ? 99 : 1, kind == "target" ? 99 : 2, "edge") };
        Assert.Throws<InvalidDataException>(() => Build(temp, nodes, links));
        Assert.False(File.Exists(temp.FilePath("index/counts.bin")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("+1")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public void Build_NodeIdValidationRejectsMissingAndInvalidValues(string value)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string attribute = value == null ? "" : $" Id=\"{value}\"";
        using var xml = DgmlFixtureBuilder.Open($"<DirectedGraph><Nodes><Node{attribute} Label=\"ok\"/></Nodes></DirectedGraph>");
        Assert.Throws<InvalidDataException>(() => DependencyGraphIndex.Build(xml, temp.FilePath("index"), sortBufferRecords: 2));
    }

    [Theory]
    [InlineData("Source", null)]
    [InlineData("Source", "")]
    [InlineData("Source", "-1")]
    [InlineData("Source", " 1")]
    [InlineData("Source", "1 ")]
    [InlineData("Source", "+1")]
    [InlineData("Source", "abc")]
    [InlineData("Source", "2147483648")]
    [InlineData("Target", null)]
    [InlineData("Target", "")]
    [InlineData("Target", "-1")]
    [InlineData("Target", " 1")]
    [InlineData("Target", "1 ")]
    [InlineData("Target", "+1")]
    [InlineData("Target", "abc")]
    [InlineData("Target", "2147483648")]
    public void Build_LinkEndpointValidationRejectsMissingAndInvalidValues(string endpoint, string value)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string valid = endpoint == "Source" ? " Target=\"1\"" : " Source=\"1\"";
        string invalid = value == null ? "" : $" {endpoint}=\"{value}\"";
        using var xml = DgmlFixtureBuilder.Open("<DirectedGraph><Nodes><Node Id=\"1\" Label=\"ok\"/></Nodes>" +
            $"<Links><Link{valid}{invalid}/></Links></DirectedGraph>");
        Assert.Throws<InvalidDataException>(() => DependencyGraphIndex.Build(xml, temp.FilePath("index"), sortBufferRecords: 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_MissingLabelIsRejectedButEmptyLabelIsAccepted(bool present)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = temp.FilePath("index");
        using var xml = DgmlFixtureBuilder.Open($"<DirectedGraph><Nodes><Node Id=\"1\"{(present ? " Label=\"\"" : "")}/></Nodes></DirectedGraph>");
        if (!present)
            Assert.Throws<InvalidDataException>(() => DependencyGraphIndex.Build(xml, path, sortBufferRecords: 2));
        else
        {
            DependencyGraphIndex.Build(xml, path, sortBufferRecords: 2);
            using var index = new DependencyGraphIndex(path);
            Assert.Equal(new DependencyGraphNode(1, ""), index.GetNode(1));
            Assert.Equal(new DependencyGraphNode(1, ""), index.FindNode(""));
        }
    }

    [Theory]
    [InlineData("<DirectedGraph><Nodes></DirectedGraph>", true)]
    [InlineData("", true)]
    [InlineData("<!DOCTYPE DirectedGraph [<!ENTITY x 'a'>]><DirectedGraph/>", true)]
    [InlineData("<Other/>", false)]
    public void Build_XmlAndRootFailuresHaveRecordedExceptions(string content, bool xmlFailure)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var xml = DgmlFixtureBuilder.Open(content);
        Action build = () => DependencyGraphIndex.Build(xml, temp.FilePath("index"), sortBufferRecords: 2);
        if (xmlFailure) Assert.Throws<XmlException>(build);
        else Assert.Throws<InvalidDataException>(build);
        Assert.False(File.Exists(temp.FilePath("index/counts.bin")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1048577)]
    [InlineData(-1)]
    public void Build_SortBufferBoundsAreValidated(int buffer)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var xml = DgmlFixtureBuilder.Open("<DirectedGraph/>");
        Assert.Equal("sortBufferRecords", Assert.Throws<ArgumentOutOfRangeException>(
            () => DependencyGraphIndex.Build(xml, temp.FilePath("index"), sortBufferRecords: buffer)).ParamName);
        Assert.False(Directory.Exists(temp.FilePath("index")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(262144)]
    [InlineData(1048576)]
    public void Build_ValidSortBufferBoundsProduceExactQueries(int buffer)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var index = new DependencyGraphIndex(Build(temp, Pair, new GraphLink[] { new(1, 2, "one") }, buffer));
        Assert.Equal(new[] { (1, "source", "one") }, Values(index.ReadIncoming(2)));
        Assert.Equal(new DependencyGraphNode(1, "source"), index.FindNode("source"));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("read")]
    [InlineData("Sorting node directory")]
    [InlineData("Sorting dependencies")]
    [InlineData("Sorting labels")]
    [InlineData("Validating references")]
    public void Build_CancellationBeforeAndDuringReadDoesNotCompleteIndex(string stage)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var cancellation = new CancellationTokenSource();
        byte[] bytes = DgmlFixtureBuilder.Bytes(Pair, new GraphLink[] { new(1, 2, "one") });
        using var xml = stage == "read" ? new CancellingReadStream(bytes, cancellation) : new MemoryStream(bytes);
        bool reached = false;
        var progress = new SynchronousProgress<DependencyGraphProgress>(value =>
        {
            if (value.Stage == stage) { reached = true; cancellation.Cancel(); }
        });
        if (stage == "before") cancellation.Cancel();
        string path = temp.FilePath("index");
        var error = Assert.Throws<OperationCanceledException>(() =>
            DependencyGraphIndex.Build(xml, path, cancellation.Token, progress, sortBufferRecords: 2));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        if (stage == "read") Assert.Equal(1, ((CancellingReadStream)xml).Reads);
        else if (stage != "before") Assert.True(reached, "The synchronous cancellation stage must actually execute.");
        Assert.False(File.Exists(Path.Combine(path, "counts.bin")));
    }

    [Fact]
    public void ReadIncoming_CancelledTokenThrowsOperationCanceledException()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var index = new DependencyGraphIndex(Build(temp, Pair, new GraphLink[] { new(1, 2, "one") }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => index.ReadIncoming(2, token: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("nodes.bin")]
    [InlineData("edges.bin")]
    [InlineData("labels.bin")]
    [InlineData("counts-truncated")]
    [InlineData("negative-nodes")]
    [InlineData("negative-edges")]
    [InlineData("count-mismatch")]
    public void Reopen_UnsupportedFormatAndInconsistentLengthsAreRejected(string corruption)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, Pair, new GraphLink[] { new(1, 2, "one") });
        string counts = Path.Combine(path, "counts.bin");
        if (corruption == "version") IndexBinaryFixture.PatchInt32(counts, 0, 2);
        else if (corruption == "negative-nodes") IndexBinaryFixture.PatchField(counts, 0, 4, -1);
        else if (corruption == "negative-edges") IndexBinaryFixture.PatchField(counts, 0, 12, -1);
        else if (corruption == "count-mismatch") IndexBinaryFixture.PatchField(counts, 0, 4, 3);
        else
        {
            string file = corruption == "counts-truncated" ? counts : Path.Combine(path, corruption);
            using var stream = File.Open(file, FileMode.Open, FileAccess.Write);
            stream.SetLength(corruption == "counts-truncated" ? 11 : stream.Length - 1);
        }
        Action reopen = () => { using var index = new DependencyGraphIndex(path); };
        if (corruption == "counts-truncated") Assert.Throws<EndOfStreamException>(reopen);
        else Assert.Throws<InvalidDataException>(reopen);
        // Failure must release its readers and lease too, not obstruct owned cleanup.
        foreach (string file in Directory.EnumerateFiles(path))
        {
            using var exclusive = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("negative-length")]
    [InlineData("over-limit")]
    [InlineData("truncated")]
    [InlineData("negative-offset")]
    [InlineData("past-end-offset")]
    public void Queries_InvalidReferencedStringEncodingIsRejected(string corruption)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, Pair, new GraphLink[] { new(1, 2, "one") });
        string strings = Path.Combine(path, "strings.bin");
        string nodes = Path.Combine(path, "nodes.bin");
        long offset = IndexBinaryFixture.ReadField(nodes, 0, IndexBinaryFixture.ValueField);
        if (corruption == "utf8")
        {
            using var stream = File.Open(strings, FileMode.Open, FileAccess.Write);
            stream.Position = offset + 4;
            stream.WriteByte(0xff);
        }
        else if (corruption.EndsWith("offset", StringComparison.Ordinal))
        {
            long bad = corruption == "negative-offset" ? -1 : new FileInfo(strings).Length - 3;
            IndexBinaryFixture.PatchField(nodes, 0, IndexBinaryFixture.ValueField, bad);
        }
        else IndexBinaryFixture.PatchInt32(strings, offset, corruption switch
        {
            "negative-length" => -1,
            "over-limit" => 8 * 1024 * 1024 + 1,
            _ => checked((int)new FileInfo(strings).Length)
        });
        using var index = new DependencyGraphIndex(path);
        Action get = () => index.GetNode(1);
        Action incoming = () => index.ReadIncoming(2);
        if (corruption == "utf8")
        {
            Assert.Throws<DecoderFallbackException>(get);
            Assert.Throws<DecoderFallbackException>(() => index.FindNode("source"));
            Assert.Throws<DecoderFallbackException>(incoming);
        }
        else
        {
            Assert.Throws<InvalidDataException>(get);
            Assert.Throws<InvalidDataException>(incoming);
            if (!corruption.EndsWith("offset", StringComparison.Ordinal))
                Assert.Throws<InvalidDataException>(() => index.FindNode("source"));
        }
    }

    [Fact]
    public void SparseStrings_OffsetsBeyondTwoGiBResolveNodesLabelsAndReasons()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        SparseFileSupport.VerifyCapability(temp.FilePath("sparse-probe.bin"));
        const string sourceName = "源 Ω🚀";
        const string targetName = "目标 & café";
        const string reason = "依存 → λ <理由>";
        string path = Build(temp, new GraphNode[] { new(1, sourceName), new(2, targetName) },
            new GraphLink[] { new(1, 2, reason) });
        const long sourceOffset = 2_147_487_744L;
        const long targetOffset = sourceOffset + 4096;
        const long reasonOffset = targetOffset + 4096;
        string strings = Path.Combine(path, "strings.bin");
        long expectedLength;
        using (var stream = new FileStream(strings, FileMode.Open, FileAccess.ReadWrite))
        {
            SparseFileSupport.MarkSparse(stream);
            Assert.InRange(SparseFileSupport.AllocatedBytes(stream), 0L, 1024 * 1024L);
            IndexBinaryFixture.WriteString(stream, sourceOffset, sourceName);
            IndexBinaryFixture.WriteString(stream, targetOffset, targetName);
            expectedLength = IndexBinaryFixture.WriteString(stream, reasonOffset, reason);
            Assert.Equal(expectedLength, stream.Length);
            Assert.True(stream.Length > int.MaxValue);
            Assert.InRange(SparseFileSupport.AllocatedBytes(stream), 1L, 1024 * 1024L);
        }
        string nodes = Path.Combine(path, "nodes.bin");
        string labels = Path.Combine(path, "labels.bin");
        string edges = Path.Combine(path, "edges.bin");
        IndexBinaryFixture.PatchField(nodes, 0, IndexBinaryFixture.ValueField, sourceOffset);
        IndexBinaryFixture.PatchField(nodes, 1, IndexBinaryFixture.ValueField, targetOffset);
        for (int record = 0; record < 2; record++)
        {
            long id = IndexBinaryFixture.ReadField(labels, record, IndexBinaryFixture.ValueField);
            Assert.Contains(id, new long[] { 1, 2 });
            IndexBinaryFixture.PatchField(labels, record, IndexBinaryFixture.DataField, id == 1 ? sourceOffset : targetOffset);
            Assert.True(IndexBinaryFixture.ReadField(labels, record, IndexBinaryFixture.DataField) > int.MaxValue);
        }
        IndexBinaryFixture.PatchField(edges, 0, IndexBinaryFixture.DataField, reasonOffset);
        Assert.Equal(sourceOffset, IndexBinaryFixture.ReadField(nodes, 0, IndexBinaryFixture.ValueField));
        Assert.Equal(targetOffset, IndexBinaryFixture.ReadField(nodes, 1, IndexBinaryFixture.ValueField));
        Assert.Equal(reasonOffset, IndexBinaryFixture.ReadField(edges, 0, IndexBinaryFixture.DataField));
        Assert.Equal(expectedLength, new FileInfo(strings).Length);
        using var index = new DependencyGraphIndex(path);
        Assert.Equal(new DependencyGraphNode(1, sourceName), index.GetNode(1));
        Assert.Equal(new DependencyGraphNode(2, targetName), index.GetNode(2));
        Assert.Equal(new DependencyGraphNode(1, sourceName), index.FindNode(sourceName));
        Assert.Equal(new DependencyGraphNode(2, targetName), index.FindNode(targetName));
        Assert.Equal(new[] { (1, sourceName, reason) }, Values(index.ReadIncoming(2)));
        Assert.Null(index.ReadIncoming(2).NextOffset);
    }

    [Fact]
    public void Build_NumericEntitiesAndNonemptyPairedTagsDecodeOnlyDirectRecords()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var xml = DgmlFixtureBuilder.Open("""
            <DirectedGraph xmlns="http://schemas.microsoft.com/vs/2009/dgml">
              <!-- A nested Node/Link is metadata, not a declaration. -->
              <Nodes>
                <Node Id="7" Label="&#x96EA; &amp; &#937;"><Node Id="99" Label="nested"/></Node>
                <Node Id="2" Label="target"><Property Name="p" Value="v"/></Node>
              </Nodes>
              <Links>
                <Link Source="7" Target="2" Reason="&#x1F680; &lt; why"><Link Source="99" Target="2"/></Link>
              </Links>
            </DirectedGraph>
            """);
        string path = temp.FilePath("index");
        DependencyGraphIndex.Build(xml, path, sortBufferRecords: 1);
        using var index = new DependencyGraphIndex(path);
        Assert.Equal(new DependencyGraphNode(7, "雪 & Ω"), index.GetNode(7));
        Assert.Equal(new DependencyGraphNode(7, "雪 & Ω"), index.FindNode("雪 & Ω"));
        Assert.Equal(new[] { (7, "雪 & Ω", "🚀 < why") }, Values(index.ReadIncoming(2)));
        Assert.Null(index.FindNode("nested"));
        Assert.Throws<InvalidDataException>(() => index.GetNode(99));
    }

    [Fact]
    public void Build_ReopenAfterDisposalPreservesExactQueries()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, Pair, new GraphLink[] { new(1, 2, "first"), new(1, 2), new(1, 2, "") });
        for (int open = 0; open < 2; open++)
        {
            using var index = new DependencyGraphIndex(path);
            Assert.Equal(new DependencyGraphNode(1, "source"), index.GetNode(1));
            Assert.Equal(new DependencyGraphNode(2, "target"), index.FindNode("target"));
            var first = index.ReadIncoming(2, pageSize: 2);
            Assert.Equal(new[] { (1, "source", "first"), (1, "source", (string)null) }, Values(first));
            Assert.Equal(2L, first.NextOffset);
            var final = index.ReadIncoming(2, first.NextOffset.Value, 2);
            Assert.Equal(new[] { (1, "source", "") }, Values(final));
            Assert.Null(final.NextOffset);
        }
    }

    [Fact]
    public void Build_NonemptyOutputIsRejectedWithoutOverwritingOwnedSentinel()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = temp.FilePath("index");
        Directory.CreateDirectory(path);
        string sentinel = Path.Combine(path, "sentinel.bin");
        File.WriteAllBytes(sentinel, new byte[] { 3, 1, 4 });
        using var xml = DgmlFixtureBuilder.Open("<DirectedGraph/>");
        Assert.Throws<IOException>(() => DependencyGraphIndex.Build(xml, path, sortBufferRecords: 2));
        Assert.Equal(new byte[] { 3, 1, 4 }, File.ReadAllBytes(sentinel));
        Assert.Equal(new[] { sentinel }, Directory.GetFiles(path));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("negative-length")]
    [InlineData("over-limit")]
    public void ReadIncoming_InvalidReasonEncodingIsRejectedWithoutDamagingNodeLookup(string corruption)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = Build(temp, Pair, new GraphLink[] { new(1, 2, "why") });
        long offset = IndexBinaryFixture.ReadField(Path.Combine(path, "edges.bin"), 0, IndexBinaryFixture.DataField);
        string strings = Path.Combine(path, "strings.bin");
        if (corruption == "utf8")
        {
            using var stream = File.Open(strings, FileMode.Open, FileAccess.Write);
            stream.Position = offset + 4;
            stream.WriteByte(0xff);
        }
        else IndexBinaryFixture.PatchInt32(strings, offset, corruption == "negative-length" ? -1 : 8 * 1024 * 1024 + 1);
        using var index = new DependencyGraphIndex(path);
        Assert.Equal(new DependencyGraphNode(1, "source"), index.GetNode(1));
        Assert.Equal(new DependencyGraphNode(2, "target"), index.FindNode("target"));
        if (corruption == "utf8") Assert.Throws<DecoderFallbackException>(() => index.ReadIncoming(2));
        else Assert.Throws<InvalidDataException>(() => index.ReadIncoming(2));
    }
}
