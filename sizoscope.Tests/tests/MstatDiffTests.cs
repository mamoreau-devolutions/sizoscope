using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class MstatDiffTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("identical")]
    [InlineData("reordered")]
    public void Diff_IdenticalAndReorderedIdentitySetsProduceNoChanges(string mode)
    {
        MstatPeBuilder Create(bool reorder)
        {
            var b = new MstatPeBuilder(padding: reorder);
            if (mode == "empty") return b;
            var owner = b.AddType("Owner");
            var second = reorder ? b.AddMethod("Second", owner) : default;
            var first = b.AddMethod("First", owner);
            if (!reorder) second = b.AddMethod("Second", owner);
            b.Type(owner, 11);
            b.Method(first, 13); b.Method(second, 17);
            b.Field(b.AddField("Field", owner), 19);
            b.Resource("payload", 23);
            if (reorder) b.AddName("padding");
            b.Frozen(owner, 29, b.AddName("object"), owner);
            return b;
        }
        using var left = Create(false).Read();
        using var right = Create(mode == "reordered").Read();
        var (removed, added) = MstatData.Diff(left, right);
        using (removed)
        using (added)
        {
            AssertEmptyChanges(removed);
            AssertEmptyChanges(added);
        }
    }

    [Fact]
    public void Diff_SizeOnlyChangesDoNotBecomeAddedOrRemovedIdentities()
    {
        MstatPeBuilder Create(int size, bool padding)
        {
            var b = new MstatPeBuilder(padding: padding);
            var owner = b.AddType("Owner");
            b.Type(owner, size);
            b.Method(b.AddMethod("Run", owner), size);
            b.Field(b.AddField("Field", owner), size);
            b.Resource("payload", size);
            if (padding) b.AddName("padding");
            b.Frozen(owner, size, b.AddName("owned"), owner);
            b.Frozen(owner, size, b.AddName("unowned"));
            return b;
        }
        using var left = Create(11, false).Read();
        using var right = Create(23, true).Read();
        Assert.Equal(66L, left.Size);
        Assert.Equal(138L, right.Size);
        Assert.Equal(72L, right.Size - left.Size);
        var (removed, added) = MstatData.Diff(left, right);
        using (removed)
        using (added)
        {
            AssertEmptyChanges(removed);
            AssertEmptyChanges(added);
        }
    }

    [Fact]
    public void Diff_AdditionsRemovalsAndReverseDirectionReturnExactSets()
    {
        var l = new MstatPeBuilder();
        var sharedL = l.AddType("Shared");
        var old = l.AddType("Old");
        l.Type(sharedL, 11); l.Type(old, 31);
        l.Method(l.AddMethod("Retained", sharedL), 13);
        l.Method(l.AddMethod("Gone", sharedL), 17);
        l.Method(l.AddMethod("Run", old), 37);
        var r = new MstatPeBuilder(padding: true);
        var sharedR = r.AddType("Shared");
        var addedType = r.AddType("New");
        var other = r.AddType("Other");
        r.Type(sharedR, 11); r.Type(addedType, 41); r.Type(other, 47);
        r.Method(r.AddMethod("Retained", sharedR), 13);
        r.Method(r.AddMethod("Added", sharedR), 19);
        r.Method(r.AddMethod("AlsoAdded", sharedR), 21);
        r.Method(r.AddMethod("Run", addedType), 43);
        r.Method(r.AddMethod("Run", other), 53);
        using var left = l.Read();
        using var right = r.Read();
        Check(left, right, reverse: false);
        Check(right, left, reverse: true);

        static void Check(MstatData first, MstatData second, bool reverse)
        {
            var (removed, added) = MstatData.Diff(first, second);
            using (removed)
            using (added)
            {
                var oldSide = reverse ? added : removed;
                var newSide = reverse ? removed : added;
                Assert.Equal(new[] { "Example.Old", "Example.Shared" },
                    ModelFixture.Types(oldSide).Select(t => t.ToString()).OrderBy(x => x));
                Assert.Equal(new[] { "Example.Old.Run() : Void", "Example.Shared.Gone() : Void" },
                    ModelFixture.Members(oldSide).Select(m => m.ToQualifiedString()).OrderBy(x => x));
                Assert.Equal(85L, oldSide.GetScopes().Single(a => a.Name == "Fixture.Library").AggregateSize);
                Assert.Equal(17L, ModelFixture.Types(oldSide).Single(t => t.Name == "Shared").AggregateSize);
                Assert.Equal(0L, ModelFixture.Types(oldSide).Single(t => t.Name == "Shared").Size);
                Assert.Equal(new[] { "Example.New", "Example.Other", "Example.Shared" },
                    ModelFixture.Types(newSide).Select(t => t.ToString()).OrderBy(x => x));
                Assert.Equal(new[] { "Example.New.Run() : Void", "Example.Other.Run() : Void",
                        "Example.Shared.Added() : Void", "Example.Shared.AlsoAdded() : Void" },
                    ModelFixture.Members(newSide).Select(m => m.ToQualifiedString()).OrderBy(x => x));
                Assert.Equal(224L, newSide.GetScopes().Single(a => a.Name == "Fixture.Library").AggregateSize);
                Assert.Equal(84L, ModelFixture.Types(newSide).Single(t => t.Name == "New").AggregateSize);
                Assert.Equal(100L, ModelFixture.Types(newSide).Single(t => t.Name == "Other").AggregateSize);
                Assert.Equal(40L, ModelFixture.Types(newSide).Single(t => t.Name == "Shared").AggregateSize);
            }
        }
    }

    [Fact]
    public void Diff_SameNamesWithDifferentOwnersAndOverloadsRemainDistinct()
    {
        var l = new MstatPeBuilder();
        var alphaL = l.AddType("Alpha");
        l.Type(alphaL, 11); l.Method(l.AddMethod("Run", alphaL), 13);
        var r = new MstatPeBuilder(padding: true);
        var alphaR = r.AddType("Alpha");
        var beta = r.AddType("Beta");
        r.Type(alphaR, 11); r.Type(beta, 19);
        r.Method(r.AddMethod("Run", alphaR, parameters: new byte[] { 0x08 }), 17);
        r.Method(r.AddMethod("Run", beta), 23);
        using var left = l.Read();
        using var right = r.Read();
        var (removed, added) = MstatData.Diff(left, right);
        using (removed)
        using (added)
        {
            Assert.Equal(new[] { "Example.Alpha.Run() : Void" },
                ModelFixture.Members(removed).Select(m => m.ToQualifiedString()));
            Assert.Equal(new[] { "Example.Alpha.Run(Int32) : Void", "Example.Beta.Run() : Void" },
                ModelFixture.Members(added).Select(m => m.ToQualifiedString()).OrderBy(x => x));
            Assert.Equal(13L, Assert.Single(removed.GetScopes()).AggregateSize);
            Assert.Equal(59L, added.GetScopes().Single(a => a.Name == "Fixture.Library").AggregateSize);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diff_SpecResourceAndFrozenIdentitiesSurviveTokenReordering(bool changedArguments)
    {
        using var left = ModelFixture.Create().Read();
        using var right = ModelFixture.Create(reordered: true, argument: changedArguments ? (byte)0x0e : (byte)0x08,
            resourceName: "newpayload", frozenName: "NewLoose").Read();
        var (removed, added) = MstatData.Diff(left, right);
        using (removed)
        using (added)
        {
            Assert.Equal(("payload", 71L), Resource(removed));
            Assert.Equal(("newpayload", 71L), Resource(added));
            Assert.Equal(73L, Assert.Single(removed.GetFrozenObjects()).Size);
            Assert.Equal(73L, Assert.Single(added.GetFrozenObjects()).Size);
            Assert.False(Assert.Single(removed.GetFrozenObjects()).Equals(Assert.Single(added.GetFrozenObjects())));
            Assert.Equal(73L, removed.UnownedFrozenObjectSize);
            Assert.Equal(73L, added.UnownedFrozenObjectSize);
            if (!changedArguments)
            {
                Assert.Empty(ModelFixture.Specs(removed));
                Assert.Empty(ModelFixture.Specs(added));
                Assert.Empty(ModelFixture.Members(removed));
                Assert.Empty(ModelFixture.Members(added));
                Assert.Equal(71L, removed.GetScopes().Single(a => a.Name == "Fixture.Library").AggregateSize);
                Assert.Equal(71L, added.GetScopes().Single(a => a.Name == "Fixture.Library").AggregateSize);
            }
            else
            {
                Assert.Equal("Example.Generic`1<Int32>", Assert.Single(ModelFixture.Specs(removed)).ToString());
                Assert.Equal("Example.Generic`1<String>", Assert.Single(ModelFixture.Specs(added)).ToString());
                Assert.Equal(new[] { "Build", "Create" }, ModelFixture.Members(removed).Select(m => m.Name).OrderBy(x => x));
                Assert.Equal(new[] { "Build", "Create" }, ModelFixture.Members(added).Select(m => m.Name).OrderBy(x => x));
                Assert.Equal("Create<Int32>() : Void", Assert.Single(ModelFixture.MethodSpecs(removed)).ToString());
                Assert.Equal("Create<String>() : Void", Assert.Single(ModelFixture.MethodSpecs(added)).ToString());
                Assert.Equal(267L, removed.GetScopes().Single(a => a.Name == "Fixture.Library").AggregateSize);
                Assert.Equal(267L, added.GetScopes().Single(a => a.Name == "Fixture.Library").AggregateSize);
            }
        }
        static (string, long) Resource(MstatData data)
        {
            var resource = Assert.Single(ModelFixture.Resources(data));
            return (resource.Name, resource.Size);
        }
    }

    private static void AssertEmptyChanges(MstatData data)
    {
        Assert.Empty(ModelFixture.Types(data));
        Assert.Empty(ModelFixture.Members(data));
        Assert.Empty(ModelFixture.Specs(data));
        Assert.Empty(ModelFixture.MethodSpecs(data));
        Assert.Empty(ModelFixture.Resources(data));
        Assert.Empty(ModelFixture.Frozen(data));
        Assert.All(data.GetScopes(), a => Assert.Equal(0L, a.AggregateSize));
        Assert.Equal(0L, data.UnownedFrozenObjectSize);
    }

    [Fact]
    public async Task Diff_RetainsLiveSessionsAndImagesAfterOriginalsDisposed()
    {
        using var leftGraph = new OwnedGraph();
        using var rightGraph = new OwnedGraph();
        using var left = ReadLiveModel("Old", "Gone", leftGraph.Input);
        using var right = ReadLiveModel("New", "Added", rightGraph.Input);
        Assert.Equal(41L, left.Size);
        Assert.Equal(41L, right.Size);
        var leftSession = left.Graph;
        var rightSession = right.Graph;
        var (removed, added) = MstatData.Diff(left, right);
        using (removed)
        using (added)
        {
            Assert.Same(leftSession, removed.Graph);
            Assert.Same(rightSession, added.Graph);
            Assert.NotSame(removed.Graph, added.Graph);
            Assert.Equal(DependencyGraphState.NotRequested, removed.Graph.State);
            Assert.Equal(DependencyGraphState.NotRequested, added.Graph.State);
            AssertLiveDiffModel(removed, "Old", "Gone");
            AssertLiveDiffModel(added, "New", "Added");
            left.Dispose();
            right.Dispose();
            // All subsequent model/name access is through retained diff owners, never freed originals.
            AssertLiveDiffModel(removed, "Old", "Gone");
            AssertLiveDiffModel(added, "New", "Added");
            await removed.Graph.LoadAsync(cacheRoot: leftGraph.CacheRoot);
            await added.Graph.LoadAsync(cacheRoot: rightGraph.CacheRoot);
            Assert.Equal(DependencyGraphState.Ready, leftSession.State);
            Assert.Equal(DependencyGraphState.Ready, rightSession.State);
            var leftStore = removed.Graph.Store;
            var rightStore = added.Graph.Store;
            OwnedGraph.AssertQueries(leftStore);
            OwnedGraph.AssertQueries(rightStore);
            AssertLiveNames(removed);
            AssertLiveNames(added);
            Assert.Equal((0, 1), await leftGraph.Clear());
            Assert.Equal((0, 1), await rightGraph.Clear());
            removed.Dispose();
            added.Dispose();
            Assert.Throws<ObjectDisposedException>(() => leftStore.FindNode("Node.One"));
            Assert.Throws<ObjectDisposedException>(() => rightStore.ReadIncoming(41));
            Assert.Throws<ObjectDisposedException>(() => { _ = leftSession.LoadAsync(cacheRoot: leftGraph.CacheRoot); });
            Assert.Throws<ObjectDisposedException>(() => rightSession.Retain());
            Assert.Equal((1, 0), await leftGraph.Clear());
            Assert.Equal((1, 0), await rightGraph.Clear());
            Assert.False(File.Exists(leftGraph.Pointer));
            Assert.False(File.Exists(rightGraph.Pointer));
        }
    }

    [Fact]
    public async Task Diff_ObservesOriginalSessionBecomingReadyWithoutRecreation()
    {
        using var leftGraph = new OwnedGraph();
        using var rightGraph = new OwnedGraph();
        using var left = ReadLiveModel("Old", "Gone", leftGraph.Input);
        using var right = ReadLiveModel("New", "Added", rightGraph.Input);
        var (removed, added) = MstatData.Diff(left, right);
        using (removed)
        using (added)
        {
            Assert.Same(left.Graph, removed.Graph);
            Assert.Same(right.Graph, added.Graph);
            Assert.Equal(DependencyGraphState.NotRequested, removed.Graph.State);
            AssertLiveDiffModel(removed, "Old", "Gone");
            AssertLiveDiffModel(added, "New", "Added");
            await left.Graph.LoadAsync(cacheRoot: leftGraph.CacheRoot);
            Assert.Equal(DependencyGraphState.Ready, removed.Graph.State);
            Assert.Same(left.Graph.Store, removed.Graph.Store);
            Assert.Equal(DependencyGraphState.NotRequested, added.Graph.State);
            Assert.True(removed.DgmlAvailable);
            Assert.False(added.DgmlAvailable);
            OwnedGraph.AssertQueries(removed.Graph.Store);
            AssertLiveNames(removed);
            AssertLiveDiffModel(removed, "Old", "Gone");
            AssertLiveDiffModel(added, "New", "Added");
            Assert.Equal(41L, left.Size);
            Assert.Equal(41L, right.Size);
        }
        // Original remains an independent owner after both diff owners leave scope.
        OwnedGraph.AssertQueries(left.Graph.Store);
        var store = left.Graph.Store;
        Assert.Equal((0, 1), await leftGraph.Clear());
        left.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.FindNode("Node.One"));
        Assert.Equal((1, 0), await leftGraph.Clear());
    }

    [Fact]
    public async Task RetainBacking_IdempotentLeaseReleasePreservesIndependentDiffOwner()
    {
        using var leftGraph = new OwnedGraph();
        using var rightGraph = new OwnedGraph();
        using var left = ReadLiveModel("Old", "Gone", leftGraph.Input);
        using var right = ReadLiveModel("New", "Added", rightGraph.Input);
        using var backing = left.RetainBacking();
        var (removed, added) = MstatData.Diff(left, right);
        using (removed)
        using (added)
        {
            var session = removed.Graph;
            Assert.Same(left.Graph, session);
            left.Dispose();
            right.Dispose();
            backing.Dispose();
            backing.Dispose();
            AssertLiveDiffModel(removed, "Old", "Gone");
            AssertLiveDiffModel(added, "New", "Added");
            await session.LoadAsync(cacheRoot: leftGraph.CacheRoot);
            AssertLiveNames(removed);
            var store = session.Store;
            OwnedGraph.AssertQueries(store);
            Assert.Equal((0, 1), await leftGraph.Clear());
            removed.Dispose();
            Assert.Throws<ObjectDisposedException>(() => store.FindNode("Node.One"));
            Assert.Throws<ObjectDisposedException>(() => session.Retain());
            Assert.Equal((1, 0), await leftGraph.Clear());
            Assert.False(File.Exists(leftGraph.Pointer));
        }
    }

    private static MstatData ReadLiveModel(string typeName, string methodName, DependencyGraphInput input)
    {
        var builder = new MstatPeBuilder();
        int first = builder.AddName("Node.One");
        int second = builder.AddName("Nœud.二");
        Assert.Equal(0, first);
        Assert.Equal(9, second);
        var owner = builder.AddType(typeName);
        builder.Type(owner, 11, first);
        builder.Method(builder.AddMethod(methodName, owner), 17, nodeId: first);
        builder.Frozen(owner, 13, second, owner);
        byte[] bytes = builder.Build();
        using var stream = new MemoryStream(bytes);
        return MstatData.Read(stream, bytes.Length, input);
    }

    private static void AssertLiveDiffModel(MstatData data, string typeName, string methodName)
    {
        var assembly = Assert.Single(data.GetScopes());
        Assert.Equal("Fixture.Library", assembly.Name);
        Assert.Equal(41L, assembly.AggregateSize);
        var type = Assert.Single(ModelFixture.Types(data));
        Assert.Equal("Example." + typeName, type.ToString());
        Assert.Equal((11L, 41L, 0), (type.Size, type.AggregateSize, type.NodeId));
        var member = Assert.Single(ModelFixture.Members(data));
        Assert.Equal("Example." + typeName + "." + methodName + "() : Void", member.ToQualifiedString());
        Assert.Equal((17L, 0), (member.Size, member.NodeId));
        var frozen = Assert.Single(ModelFixture.Frozen(data));
        Assert.Equal((13L, 9), (frozen.Size, frozen.NodeId));
        Assert.Empty(ModelFixture.Specs(data));
        Assert.Empty(ModelFixture.Resources(data));
        Assert.Empty(data.GetFrozenObjects());
    }

    private static void AssertLiveNames(MstatData data)
    {
        var type = Assert.Single(ModelFixture.Types(data));
        var frozen = Assert.Single(ModelFixture.Frozen(data));
        var first = data.GetNodeForId(type.NodeId, out string firstName);
        var second = data.GetNodeForId(frozen.NodeId, out string secondName);
        Assert.Equal("Node.One", firstName);
        Assert.Equal("Nœud.二", secondName);
        Assert.Equal((41, "Node.One"), (first.Id, first.Name));
        Assert.Equal((73, "Nœud.二"), (second.Id, second.Name));
        Assert.True(data.ContainsNamedNode("Node.One"));
        Assert.False(data.ContainsNamedNode("missing"));
    }
}
