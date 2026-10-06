using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class MstatDataTests
{
    [Theory]
    [InlineData("1.0.0.0", -2)]
    [InlineData("2.0.0.0", 7)]
    [InlineData("2.1.0.0", 7)]
    public void Read_SupportedVersionsDecodeExactLayout(string version, int nodeId)
    {
        var builder = new MstatPeBuilder(version);
        var type = builder.AddType("Owner");
        var method = builder.AddMethod("Run", type);
        builder.Type(type, 11, 7);
        builder.Method(method, 13, 17, 19, 23);
        builder.Blob("Metadata", 29);
        using var data = builder.Read();
        Assert.Equal(Version.Parse(version), data.MetadataReader.GetAssemblyDefinition().Version);
        var assembly = Assert.Single(data.GetScopes());
        var actualType = Assert.Single(assembly.GetTypes());
        var actualMethod = Assert.Single(actualType.GetMembers());
        Assert.Equal(11L, actualType.Size);
        Assert.Equal(49L, actualMethod.Size);
        Assert.Equal(60L, actualType.AggregateSize);
        Assert.Equal(60L, assembly.AggregateSize);
        Assert.Equal(29L, data.BlobsSize);
        Assert.Equal(89L, data.Size);
        Assert.Equal(nodeId, actualType.NodeId);
        Assert.Equal(version.StartsWith("1.") ? -2 : 23, actualMethod.NodeId);
        Assert.Equal(0L, data.UnownedFrozenObjectSize);
        Assert.Equal(version == "2.1.0.0" ? 6 : 3, data.MetadataReader.MethodDefinitions.Count);
    }

    [Fact]
    public void Read_EmptyDiagnosticPayloadsProduceZeroTotals()
    {
        using var data = new MstatPeBuilder().Read();
        Assert.Equal(0L, data.Size);
        Assert.Equal(0L, data.BlobsSize);
        Assert.Equal(0L, data.UnownedFrozenObjectSize);
        Assert.Empty(data.Blobs);
        Assert.Empty(data.GetFrozenObjects());
        var assembly = Assert.Single(data.GetScopes());
        Assert.Equal("Fixture.Library", assembly.Name);
        Assert.Equal(0L, assembly.AggregateSize);
        Assert.Empty(assembly.GetTypes());
        Assert.Empty(assembly.GetManifestResources());
    }

    [Fact]
    public void Read_CategoryTotalsIncludeOwnedAndUnownedContributions()
    {
        var b = new MstatPeBuilder();
        var owner = b.AddType("Owner");
        var nested = b.AddType("Child", "", owner);
        var method = b.AddMethod("Run", owner);
        var field = b.AddField("State", owner);
        b.Type(owner, 11);
        b.Type(nested, 13);
        b.Method(method, 17, 19, 23);
        b.Field(field, 29);
        b.Frozen(owner, 31, b.AddName("owned"), owner);
        b.Frozen(nested, 37, b.AddName("unowned"));
        b.Resource("payload", 41);
        b.Blob("Metadata", 43);
        using var data = b.Read();
        var assembly = Assert.Single(data.GetScopes());
        var type = Assert.Single(assembly.GetTypes());
        Assert.Equal(11L, type.Size);
        var child = Assert.Single(type.GetNestedTypes());
        Assert.Equal(13L, child.Size);
        Assert.Equal(13L, child.AggregateSize);
        var members = type.GetMembers().ToDictionary(m => m.Name);
        Assert.Equal(59L, members["Run"].Size);
        Assert.False(members["Run"].IsField);
        Assert.Equal(29L, members["State"].Size);
        Assert.True(members["State"].IsField);
        var owned = Assert.Single(type.GetFrozenObjects());
        Assert.Equal(31L, owned.Size);
        Assert.Equal(owner, owned.OwningEntity);
        var unowned = Assert.Single(data.GetFrozenObjects());
        Assert.Equal(37L, unowned.Size);
        Assert.True(unowned.OwningEntity.IsNil);
        Assert.Equal(nested, unowned.InstanceType);
        var resource = Assert.Single(assembly.GetManifestResources());
        Assert.Equal("payload", resource.Name);
        Assert.Equal(41L, resource.Size);
        Assert.Equal(143L, type.AggregateSize);
        Assert.Equal(184L, assembly.AggregateSize);
        Assert.Equal(37L, data.UnownedFrozenObjectSize);
        Assert.Equal(43L, data.BlobsSize);
        Assert.Equal(264L, data.Size);
    }

    [Fact]
    public void Read_RecognizedBlobsAreRenamedAndUnknownBlobsExcluded()
    {
        var b = new MstatPeBuilder();
        string[] names = { "Metadata", "ExternalReferencesTable", "NativeLayoutInfo",
            "InterfaceDispatchCellSection", "InterfaceDispatchMap", "UnboxingStub", "PInvokeMethodFixup",
            "ReflectionInvokeMap", "TypeMetadataMap", "ReflectionFieldMap" };
        foreach (string name in names) b.Blob(name, 7);
        b.Blob("Metadata", 0);
        b.Blob("Unknown", 1234);
        using var data = b.Read();
        string[] expected = { "Reflection and stack trace metadata", "Reflection and type loader support table",
            "Runtime type loader support data", "Interface method dispatch cells", "Interface method dispatch maps",
            "Unboxing stubs", "P/invoke method fixups", "Reflection method invoke map",
            "Reflection type metadata map", "Reflection field access map" };
        Assert.Equal(expected.OrderBy(x => x), data.Blobs.Where(x => x.Size == 7).Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(("Reflection and stack trace metadata", 0L), Assert.Single(data.Blobs, x => x.Size == 0));
        Assert.Equal(11, data.Blobs.Count());
        Assert.Equal(70L, data.BlobsSize);
        Assert.Equal(70L, data.Size);
    }

    [Fact]
    public async Task Read_NamesOffsetsResolveExactNodeIdentities()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var b = new MstatPeBuilder();
        int first = b.AddName("Node.One");
        int second = b.AddName("Nœud.二");
        var owner = b.AddType("Owner");
        b.Type(owner, 11, first);
        b.Frozen(owner, 13, second, owner);
        byte[] bytes = b.Build();
        // Pin the fixture's offsets/encoding independently, not through the graph index.
        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            var reader = pe.GetSectionData(".names").GetReader();
            Assert.Equal(0, first);
            Assert.Equal(9, second);
            Assert.Equal("Node.One", reader.ReadSerializedString());
            Assert.Equal("Nœud.二", reader.ReadSerializedString());
        }
        string path = InputFixtureBuilder.Plain(temp, "app.scan.dgml.xml", Encoding.UTF8.GetBytes(
            "<DirectedGraph><Nodes><Node Id=\"41\" Label=\"Node.One\"/><Node Id=\"73\" Label=\"Nœud.二\"/></Nodes></DirectedGraph>"));
        using var stream = new MemoryStream(bytes);
        using var data = MstatData.Read(stream, bytes.Length, new DependencyGraphInput(path));
        // Loading is only setup for the existing GetNodeForId contract, not a session/cache test.
        await data.Graph.LoadAsync(cacheRoot: temp.FilePath("cache"));
        var type = Assert.Single(Assert.Single(data.GetScopes()).GetTypes());
        var frozen = Assert.Single(type.GetFrozenObjects());
        Assert.Equal(first, type.NodeId);
        Assert.Equal(second, frozen.NodeId);
        var typeNode = data.GetNodeForId(type.NodeId, out string typeName);
        var frozenNode = data.GetNodeForId(frozen.NodeId, out string frozenName);
        Assert.Equal("Node.One", typeName);
        Assert.Equal("Nœud.二", frozenName);
        Assert.Equal((41, "Node.One"), (typeNode.Id, typeNode.Name));
        Assert.Equal((73, "Nœud.二"), (frozenNode.Id, frozenNode.Name));
    }

    [Fact]
    public void Read_ThreeMethodComponentsWidenBeforeAddition()
    {
        var b = new MstatPeBuilder();
        var type = b.AddType("Owner");
        b.Method(b.AddMethod("Huge", type), 1_100_000_000, 1_100_000_000, 1_100_000_000);
        using var data = b.Read();
        var assembly = Assert.Single(data.GetScopes());
        var owner = Assert.Single(assembly.GetTypes());
        var method = Assert.Single(owner.GetMembers());
        Assert.Equal(3_300_000_000L, method.Size);
        Assert.True(method.Size > int.MaxValue);
        Assert.Equal(3_300_000_000L, method.AggregateSize);
        Assert.Equal(0L, owner.Size);
        Assert.Equal(3_300_000_000L, owner.AggregateSize);
        Assert.Equal(3_300_000_000L, assembly.AggregateSize);
        Assert.Equal(3_300_000_000L, data.Size);
    }

    [Theory]
    [InlineData(1_073_741_823, 1_073_741_824, 2_147_483_647L)]
    [InlineData(1_073_741_824, 1_073_741_824, 2_147_483_648L)]
    [InlineData(1_100_000_000, 1_100_000_000, 2_200_000_000L)]
    public void Read_CategoryAndBlobAccumulationUsesInt64(int first, int second, long expected)
    {
        var b = new MstatPeBuilder();
        var type1 = b.AddType("One");
        var type2 = b.AddType("Two");
        b.Type(type1, first);
        b.Type(type2, second);
        b.Method(b.AddMethod("M", type1), first);
        b.Method(b.AddMethod("M", type2), second);
        b.Blob("Metadata", first);
        b.Blob("ExternalReferencesTable", second);
        using var data = b.Read();
        var assembly = Assert.Single(data.GetScopes());
        var types = assembly.GetTypes().ToArray();
        Assert.Equal(expected, types.Sum(t => t.Size));
        Assert.Equal(expected, types.SelectMany(t => t.GetMembers()).Sum(m => m.Size));
        Assert.Equal(2 * expected, assembly.AggregateSize);
        Assert.Equal(expected, data.BlobsSize);
        Assert.Equal(3 * expected, data.Size);
        Assert.Equal(2L * first, types.Single(t => t.Name == "One").AggregateSize);
        Assert.Equal(2L * second, types.Single(t => t.Name == "Two").AggregateSize);
    }

    [Fact]
    public void Dispose_ReleasesOwnedInputAfterAssertions()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var b = new MstatPeBuilder();
        b.Type(b.AddType("Owner"), 11);
        string path = InputFixtureBuilder.Plain(temp, "app.mstat", b.Build());
        using (var stream = File.OpenRead(path))
        using (var data = MstatData.Read(stream, stream.Length))
            Assert.Equal(11L, data.Size);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Read_NamesOffsetsGraphAvailabilityTransitionsPreserveIdentities()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var b = new MstatPeBuilder();
        int first = b.AddName("Node.One");
        int second = b.AddName("Nœud.二");
        var owner = b.AddType("Owner");
        b.Type(owner, 11, first);
        b.Frozen(owner, 13, second, owner);
        byte[] bytes = b.Build();
        string path = InputFixtureBuilder.Plain(temp, "names.dgml",
            DgmlFixtureBuilder.Bytes(new[] { new GraphNode(41, "Node.One"), new GraphNode(73, "Nœud.二") },
                Array.Empty<GraphLink>()));
        using var stream = new MemoryStream(bytes);
        using var data = MstatData.Read(stream, bytes.Length, new DependencyGraphInput(path));
        Assert.Equal(DependencyGraphState.NotRequested, data.Graph.State);
        Assert.Null(data.Graph.Error);
        Assert.True(data.DgmlSupported);
        Assert.False(data.DgmlAvailable);
        Assert.Throws<InvalidOperationException>(() => data.Graph.Store);

        await data.Graph.LoadAsync(cacheRoot: temp.FilePath("cache"));

        Assert.Equal(DependencyGraphState.Ready, data.Graph.State);
        Assert.Null(data.Graph.Error);
        Assert.True(data.DgmlAvailable);
        Assert.Equal(0, first);
        Assert.Equal(9, second);
        var type = Assert.Single(Assert.Single(data.GetScopes()).GetTypes());
        var frozen = Assert.Single(type.GetFrozenObjects());
        Assert.Equal((11L, 0), (type.Size, type.NodeId));
        Assert.Equal((13L, 9), (frozen.Size, frozen.NodeId));
        Assert.Equal(24L, type.AggregateSize);
        Assert.Equal(24L, data.Size);
        var typeNode = data.GetNodeForId(type.NodeId, out string typeName);
        var frozenNode = data.GetNodeForId(frozen.NodeId, out string frozenName);
        Assert.Equal("Node.One", typeName);
        Assert.Equal("Nœud.二", frozenName);
        Assert.Equal((41, "Node.One"), (typeNode.Id, typeNode.Name));
        Assert.Equal((73, "Nœud.二"), (frozenNode.Id, frozenNode.Name));
    }

    [Fact]
    public void Read_SizeOnlyUnchangedGraphDoesNotOpenOrLoad()
    {
        using var graph = new OwnedGraph();
        using var exclusive = File.Open(graph.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var data = ReadSizeModel(graph.Input);
        AssertSizeModelAndDiff(data);
        Assert.Equal(DependencyGraphState.NotRequested, data.Graph.State);
        Assert.Null(data.Graph.Error);
        Assert.Null(data.Graph.Progress);
        Assert.True(data.DgmlSupported);
        Assert.False(data.DgmlAvailable);
        Assert.Throws<InvalidOperationException>(() => data.Graph.Store);
        Assert.False(Directory.Exists(graph.CacheRoot));
    }

    [Fact]
    public async Task Read_SizeOnlyMalformedGraphDefersXmlFailure()
    {
        using var graph = new OwnedGraph();
        File.WriteAllBytes(graph.Source, Encoding.UTF8.GetBytes("<DirectedGraph><Nodes>"));
        var input = new DependencyGraphInput(graph.Source);
        MstatData data;
        using (var exclusive = File.Open(graph.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            data = ReadSizeModel(input);
            AssertSizeModelAndDiff(data);
            Assert.Equal(DependencyGraphState.NotRequested, data.Graph.State);
            Assert.Null(data.Graph.Error);
            Assert.Null(data.Graph.Progress);
            Assert.False(data.DgmlAvailable);
            Assert.False(Directory.Exists(graph.CacheRoot));
        }
        using (data)
        {
            Task load = data.Graph.LoadAsync(cacheRoot: graph.CacheRoot);
            await load;
            Assert.Equal(TaskStatus.RanToCompletion, load.Status);
            Assert.Equal(DependencyGraphState.Failed, data.Graph.State);
            Assert.IsType<System.Xml.XmlException>(data.Graph.Error);
            Assert.False(data.DgmlAvailable);
            Assert.Throws<InvalidOperationException>(() => data.Graph.Store);
            AssertSizeModelAndDiff(data);
            Assert.Empty(Directory.GetFileSystemEntries(graph.CacheRoot, "current", SearchOption.AllDirectories));
            graph.AssertNoTransient();
        }
    }

    [Fact]
    public void Read_SizeOnlyAbsentGraphRemainsUsableAndUnavailable()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] bytes = ModelFixture.Create().Build();
        string source = InputFixtureBuilder.Plain(temp, "absent.mstat", bytes);
        using var resolved = sizoscope.ResolvedFile.Open(source);
        Assert.False(resolved.HasDgml);
        Assert.Null(resolved.GraphInput);
        using var stream = resolved.OpenMstat();
        using var data = MstatData.Read(stream, resolved.MstatLength);
        AssertSizeModelAndDiff(data);
        Assert.Equal(DependencyGraphState.Unavailable, data.Graph.State);
        Assert.Null(data.Graph.Error);
        Assert.Null(data.Graph.Progress);
        Assert.False(data.DgmlAvailable);
        Assert.Throws<InvalidOperationException>(() => { _ = data.Graph.LoadAsync(cacheRoot: temp.FilePath("cache")); });
        Assert.False(Directory.Exists(temp.FilePath("cache")));
    }

    [Fact]
    public async Task Read_SizeOnlyChangedDescriptorDefersValidationUntilLoad()
    {
        using var graph = new OwnedGraph();
        graph.ChangeSource("timestamp");
        using var data = ReadSizeModel(graph.Input);
        AssertSizeModelAndDiff(data);
        Assert.Equal(DependencyGraphState.NotRequested, data.Graph.State);
        Assert.Null(data.Graph.Error);
        Assert.Null(data.Graph.Progress);
        Assert.False(data.DgmlAvailable);
        Assert.False(Directory.Exists(graph.CacheRoot));
        Task load = data.Graph.LoadAsync(cacheRoot: graph.CacheRoot);
        await load;
        Assert.Equal(TaskStatus.RanToCompletion, load.Status);
        Assert.Equal(DependencyGraphState.Failed, data.Graph.State);
        Assert.Equal("The dependency input changed. Reload the MSTAT to analyze the matching build.",
            Assert.IsType<InvalidDataException>(data.Graph.Error).Message);
        AssertSizeModelAndDiff(data);
        graph.AssertNoPublication();
    }

    [Theory]
    [InlineData("1.0.0.0", false, -2, -2, 3)]
    [InlineData("2.0.0.0", true, 7, 23, 3)]
    [InlineData("2.1.0.0", true, 7, 23, 6)]
    public async Task Read_DgmlSupportAndAvailabilityFollowVersionAndReady(
        string version, bool supported, int typeId, int methodId, int diagnostics)
    {
        using var graph = new OwnedGraph();
        var builder = new MstatPeBuilder(version);
        var owner = builder.AddType("Owner");
        builder.Type(owner, 11, 7);
        builder.Method(builder.AddMethod("Run", owner), 13, 17, 19, 23);
        builder.Blob("Metadata", 29);
        byte[] bytes = builder.Build();
        using var stream = new MemoryStream(bytes);
        using var data = MstatData.Read(stream, bytes.Length, graph.Input);
        Assert.Equal(supported, data.DgmlSupported);
        Assert.False(data.DgmlAvailable);
        Assert.Equal(DependencyGraphState.NotRequested, data.Graph.State);
        Assert.Null(data.Graph.Error);
        Assert.Null(data.Graph.Progress);
        await data.Graph.LoadAsync(cacheRoot: graph.CacheRoot);
        Assert.Equal(DependencyGraphState.Ready, data.Graph.State);
        Assert.Null(data.Graph.Error);
        Assert.Equal(supported, data.DgmlAvailable);
        var assembly = Assert.Single(data.GetScopes());
        var type = Assert.Single(assembly.GetTypes());
        var method = Assert.Single(type.GetMembers());
        Assert.Equal((11L, 60L, typeId), (type.Size, type.AggregateSize, type.NodeId));
        Assert.Equal((49L, methodId), (method.Size, method.NodeId));
        Assert.Equal(60L, assembly.AggregateSize);
        Assert.Equal(29L, data.BlobsSize);
        Assert.Equal(89L, data.Size);
        Assert.Equal(diagnostics, data.MetadataReader.MethodDefinitions.Count);
        OwnedGraph.AssertQueries(data.Graph.Store);
    }

    [Fact]
    public async Task GraphNames_PresentAndAbsentMembershipAndMissingGraphNode()
    {
        using var graph = new OwnedGraph(1);
        var builder = new MstatPeBuilder();
        int first = builder.AddName("Node.One");
        int second = builder.AddName("Nœud.二");
        Assert.Equal(0, first);
        Assert.Equal(9, second);
        var owner = builder.AddType("Owner");
        builder.Type(owner, 11, first);
        builder.Frozen(owner, 13, second, owner);
        byte[] bytes = builder.Build();
        using var stream = new MemoryStream(bytes);
        using var data = MstatData.Read(stream, bytes.Length, graph.Input);
        Assert.Throws<InvalidOperationException>(() => data.ContainsNamedNode("Node.One"));
        Assert.Throws<InvalidOperationException>(() => data.GetNodeForId(first, out _));
        await data.Graph.LoadAsync(cacheRoot: graph.CacheRoot);
        Assert.Equal(DependencyGraphState.Ready, data.Graph.State);
        Assert.True(data.ContainsNamedNode("Node.One"));
        Assert.False(data.ContainsNamedNode("Nœud.二"));
        Assert.False(data.ContainsNamedNode("absent"));
        var present = data.GetNodeForId(first, out string presentName);
        Assert.Equal("Node.One", presentName);
        Assert.Equal((41, "Node.One"), (present.Id, present.Name));
        Assert.Null(data.GetNodeForId(second, out string missingName));
        Assert.Equal("Nœud.二", missingName);
        Assert.Empty(data.Graph.Store.ReadIncoming(41).Edges);
        var type = Assert.Single(Assert.Single(data.GetScopes()).GetTypes());
        Assert.Equal((11L, 0), (type.Size, type.NodeId));
        Assert.Equal((13L, 9), (Assert.Single(type.GetFrozenObjects()).Size, Assert.Single(type.GetFrozenObjects()).NodeId));
        Assert.Equal(24L, data.Size);
    }

    private static MstatData ReadSizeModel(DependencyGraphInput input)
    {
        byte[] bytes = ModelFixture.Create().Build();
        using var stream = new MemoryStream(bytes);
        return MstatData.Read(stream, bytes.Length, input);
    }

    private static void AssertSizeModelAndDiff(MstatData data)
    {
        Assert.Equal(774L, data.Size);
        Assert.Equal(79L, data.BlobsSize);
        Assert.Equal(73L, data.UnownedFrozenObjectSize);
        Assert.Equal(("Reflection and stack trace metadata", 79L), Assert.Single(data.Blobs));
        var scopes = data.GetScopes().ToDictionary(a => a.Name);
        Assert.Equal(494L, scopes["Fixture.Library"].AggregateSize);
        Assert.Equal(128L, scopes["Companion.Library"].AggregateSize);
        var types = ModelFixture.Types(data).ToDictionary(t => t.Name);
        Assert.Equal((11L, 163L), (types["Owner"].Size, types["Owner"].AggregateSize));
        Assert.Equal((13L, 13L), (types["Child"].Size, types["Child"].AggregateSize));
        Assert.Equal((17L, 260L), (types["Generic`1"].Size, types["Generic`1"].AggregateSize));
        Assert.Equal((61L, 128L), (types["Other"].Size, types["Other"].AggregateSize));
        Assert.Equal((41L, 143L), (Assert.Single(ModelFixture.Specs(data)).Size, Assert.Single(ModelFixture.Specs(data)).AggregateSize));
        Assert.Equal(new long[] { 31, 43, 47, 67, 71 }, ModelFixture.Members(data).Select(m => m.Size).OrderBy(s => s));
        Assert.Equal(53L, Assert.Single(ModelFixture.MethodSpecs(data)).Size);
        Assert.Equal(new long[] { 37, 59, 73 }, ModelFixture.Frozen(data).Select(f => f.Size).OrderBy(s => s));
        Assert.Equal(71L, Assert.Single(ModelFixture.Resources(data)).Size);
        using var reordered = ModelFixture.Create(reordered: true).Read();
        var (removed, added) = MstatData.Diff(data, reordered);
        using (removed)
        using (added)
        {
            Assert.Equal(0L, reordered.Size - data.Size);
            Assert.Same(data.Graph, removed.Graph);
            foreach (var side in new[] { removed, added })
            {
                Assert.Empty(ModelFixture.Types(side));
                Assert.Empty(ModelFixture.Members(side));
                Assert.Empty(ModelFixture.Specs(side));
                Assert.Empty(ModelFixture.MethodSpecs(side));
                Assert.Empty(ModelFixture.Resources(side));
                Assert.Empty(ModelFixture.Frozen(side));
                Assert.All(side.GetScopes(), a => Assert.Equal(0L, a.AggregateSize));
            }
        }
    }
}
