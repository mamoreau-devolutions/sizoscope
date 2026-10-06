using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class MstatObjectModelTests
{
    [Fact]
    public void Model_HierarchyEnumeratesExactIdentitiesAndAggregates()
    {
        using var data = ModelFixture.Create().Read();
        var assemblies = data.GetScopes().ToDictionary(a => a.Name);
        Assert.Equal(new[] { "Companion.Library", "Fixture.Library" }, assemblies.Keys.OrderBy(x => x));
        Assert.Equal(494L, assemblies["Fixture.Library"].AggregateSize);
        Assert.Equal(128L, assemblies["Companion.Library"].AggregateSize);
        var types = ModelFixture.Types(data).ToDictionary(t => t.ToString());
        Assert.Equal(new[] { "Example.Generic`1", "Example.Other", "Example.Owner", "Example.Owner.Child" },
            types.Keys.OrderBy(x => x));
        Assert.Equal((11L, 163L), (types["Example.Owner"].Size, types["Example.Owner"].AggregateSize));
        Assert.Equal((13L, 13L), (types["Example.Owner.Child"].Size, types["Example.Owner.Child"].AggregateSize));
        Assert.Equal((17L, 260L), (types["Example.Generic`1"].Size, types["Example.Generic`1"].AggregateSize));
        Assert.Equal((61L, 128L), (types["Example.Other"].Size, types["Example.Other"].AggregateSize));
        Assert.Equal("", types["Example.Owner.Child"].Namespace);
        Assert.Equal("Example", types["Example.Owner"].Namespace);
        Assert.Equal(types["Example.Owner"].Handle,
            data.MetadataReader.GetTypeReference(types["Example.Owner.Child"].Handle).ResolutionScope);
        var spec = Assert.Single(ModelFixture.Specs(data));
        Assert.Equal("Example.Generic`1<Int32>", spec.ToString());
        Assert.Equal((41L, 143L), (spec.Size, spec.AggregateSize));
        var members = ModelFixture.Members(data).ToDictionary(m => m.Name);
        Assert.Equal(new[] { "Build", "Create", "Run", "State", "Work" }, members.Keys.OrderBy(x => x));
        Assert.Equal(types["Example.Owner"], members["Run"].OwnerAsDef);
        Assert.True(members["Run"].OwnerAsSpec.Handle.IsNil);
        Assert.Equal(spec, members["Build"].OwnerAsSpec);
        Assert.True(members["Build"].OwnerAsDef.Handle.IsNil);
        Assert.Equal((47L, 100L), (members["Create"].Size, members["Create"].AggregateSize));
        var methodSpec = Assert.Single(members["Create"].GetInstantiations());
        Assert.Equal(53L, methodSpec.Size);
        Assert.Equal("Create<Int32>() : Void", methodSpec.ToString());
        var resource = Assert.Single(ModelFixture.Resources(data));
        Assert.Equal(("payload", 71L), (resource.Name, resource.Size));
        Assert.Equal(assemblies["Fixture.Library"], resource.Assembly);
        var owned = Assert.Single(types["Example.Owner"].GetFrozenObjects());
        Assert.Equal((37L, types["Example.Owner"].Handle), (owned.Size, (TypeReferenceHandle)owned.OwningEntity));
        var specOwned = Assert.Single(spec.GetFrozenObjects());
        Assert.Equal(59L, specOwned.Size);
        Assert.Equal(spec.Handle, specOwned.OwningEntity);
        Assert.Equal(spec.Handle, specOwned.InstanceType);
        var loose = Assert.Single(data.GetFrozenObjects());
        Assert.Equal(73L, loose.Size);
        Assert.True(loose.OwningEntity.IsNil);
        Assert.Equal(types["Example.Other"].Handle, loose.InstanceType);
        // Record IDs are actual SerString offsets, rather than metadata RIDs or arbitrary ordinals.
        Assert.Equal(0, types["Example.Owner"].NodeId);
        Assert.Equal(6, types["Example.Owner.Child"].NodeId);
        Assert.Equal(12, types["Example.Generic`1"].NodeId);
        Assert.Equal(20, spec.NodeId);
        Assert.Equal(35, members["State"].NodeId);
        Assert.Equal(54, methodSpec.NodeId);
        Assert.Equal(70, owned.NodeId);
        Assert.Equal(774L, data.Size);
    }

    [Theory]
    [InlineData(0, 11L)]
    [InlineData(1, 24L)]
    [InlineData(3, 63L)]
    public void Model_EmptySingletonAndMultipleChildrenHaveExactAggregates(int count, long expected)
    {
        var b = new MstatPeBuilder();
        var owner = b.AddType("Owner");
        b.Type(owner, 11);
        if (count >= 1) b.Method(b.AddMethod("First", owner), 13);
        if (count == 3)
        {
            b.Method(b.AddMethod("Second", owner), 17);
            b.Type(b.AddType("Nested", "", owner), 22);
        }
        using var data = b.Read();
        var assembly = Assert.Single(data.GetScopes());
        var type = Assert.Single(assembly.GetTypes());
        Assert.Equal(11L, type.Size);
        Assert.Equal(expected, type.AggregateSize);
        Assert.Equal(expected, assembly.AggregateSize);
        Assert.Equal(expected, data.Size);
        Assert.Equal(count == 3 ? new[] { "First", "Second" } : count == 1 ? new[] { "First" } : Array.Empty<string>(),
            type.GetMembers().Select(m => m.Name).OrderBy(x => x));
        Assert.Equal(count == 3 ? new[] { "Nested" } : Array.Empty<string>(),
            type.GetNestedTypes().Select(t => t.Name));
    }

    [Theory]
    [InlineData("assembly")]
    [InlineData("type")]
    [InlineData("member")]
    [InlineData("typespec")]
    [InlineData("methodspec")]
    [InlineData("resource")]
    [InlineData("frozen")]
    public void Model_EqualIdentitiesAcrossReorderedTokensHaveEqualHashes(string kind)
    {
        using var left = ModelFixture.Create().Read();
        using var right = ModelFixture.Create(reordered: true).Read();
        switch (kind)
        {
            case "assembly":
                EqualRows(left.GetScopes(), right.GetScopes().Where(a => a.Name != "Padding"), a => a.Name,
                    a => MetadataTokens.GetRowNumber(a.Handle)); break;
            case "type":
                EqualRows(ModelFixture.Types(left), ModelFixture.Types(right), t => t.ToString(),
                    t => MetadataTokens.GetRowNumber(t.Handle)); break;
            case "member":
                EqualRows(ModelFixture.Members(left), ModelFixture.Members(right), m => m.ToQualifiedString(),
                    m => MetadataTokens.GetRowNumber(m.Handle)); break;
            case "typespec":
                EqualRows(ModelFixture.Specs(left), ModelFixture.Specs(right), s => s.ToString(),
                    s => MetadataTokens.GetRowNumber(s.Handle)); break;
            case "methodspec":
                EqualRows(ModelFixture.MethodSpecs(left), ModelFixture.MethodSpecs(right), s => s.ToString(),
                    s => MetadataTokens.GetRowNumber(s.Handle)); break;
            case "resource":
                EqualRows(ModelFixture.Resources(left), ModelFixture.Resources(right), r => r.Name,
                    r => MetadataTokens.GetRowNumber(r.Handle), requireChangedToken: false); break;
            default:
                EqualRows(ModelFixture.Frozen(left), ModelFixture.Frozen(right), f => f.Size.ToString(),
                    f => f.NodeId); break;
        }
    }

    private static void EqualRows<T>(IEnumerable<T> left, IEnumerable<T> right, Func<T, string> key,
        Func<T, int> token, bool requireChangedToken = true) where T : struct, IEquatable<T>
    {
        var first = left.ToDictionary(key);
        var second = right.ToDictionary(key);
        Assert.NotEmpty(first);
        Assert.Equal(first.Keys.OrderBy(x => x), second.Keys.OrderBy(x => x));
        if (requireChangedToken) Assert.Contains(first.Keys, k => token(first[k]) != token(second[k]));
        foreach (string identity in first.Keys)
        {
            Assert.True(first[identity].Equals(second[identity]), identity);
            Assert.True(second[identity].Equals(first[identity]), identity);
            Assert.Equal(first[identity].GetHashCode(), second[identity].GetHashCode());
        }
    }

    [Theory]
    [InlineData("assembly")]
    [InlineData("namespace")]
    [InlineData("owner")]
    [InlineData("nested-owner")]
    [InlineData("parameter")]
    [InlineData("return")]
    [InlineData("field")]
    [InlineData("typespec")]
    [InlineData("methodspec")]
    [InlineData("resource")]
    [InlineData("frozen")]
    public void Model_DifferentOwnersOverloadsAndInstantiationArgumentsStayDistinct(string kind)
    {
        var b = new MstatPeBuilder();
        var otherAssembly = b.AddAssembly("Other.Library");
        var owner = b.AddType("Owner");
        var otherOwner = b.AddType("Other");
        var otherNs = b.AddType("Owner", "Different");
        var otherAsm = b.AddType("Owner", scope: otherAssembly);
        var child1 = b.AddType("Child", "", owner);
        var child2 = b.AddType("Child", "", otherOwner);
        foreach (var t in new[] { owner, otherOwner, otherNs, otherAsm, child1, child2 }) b.Type(t, 1);
        var method = b.AddMethod("Run", owner);
        var changedOwner = b.AddMethod("Run", otherOwner);
        var changedParam = b.AddMethod("Run", owner, parameters: new byte[] { 0x08 });
        var changedReturn = b.AddMethod("Run", owner, returnType: 0x08);
        foreach (var m in new[] { method, changedOwner, changedParam, changedReturn }) b.Method(m, 1);
        var field1 = b.AddField("State", owner);
        var field2 = b.AddField("State", owner, 0x0e);
        b.Field(field1, 1); b.Field(field2, 1);
        var spec1 = b.AddTypeSpec(owner, 0x08);
        var spec2 = b.AddTypeSpec(owner, 0x0e);
        b.Type(spec1, 1); b.Type(spec2, 1);
        var generic = b.AddMethod("Create", owner, generic: true);
        b.Method(generic, 1);
        var inst1 = b.AddMethodSpec(generic, 0x08);
        var inst2 = b.AddMethodSpec(generic, 0x0e);
        b.Method(inst1, 1); b.Method(inst2, 1);
        b.Resource("one", 1); b.Resource("two", 1);
        b.Resource("one", 1, otherAssembly);
        b.Frozen(owner, 1, b.AddName("one"));
        b.Frozen(owner, 1, b.AddName("two"));
        byte[] bytes = b.Build();
        using var stream = new MemoryStream(bytes);
        using var secondStream = new MemoryStream(bytes);
        using var data = MstatData.Read(stream, bytes.Length);
        using var secondData = MstatData.Read(secondStream, bytes.Length);
        switch (kind)
        {
            case "assembly": Unequal(new MstatData.MstatTypeDefinition(data, owner), new(secondData, otherAsm)); break;
            case "namespace": Unequal(new MstatData.MstatTypeDefinition(data, owner), new(secondData, otherNs)); break;
            case "owner": Unequal(new MstatData.MstatMemberDefinition(data, method), new(secondData, changedOwner)); break;
            case "nested-owner": Unequal(new MstatData.MstatTypeDefinition(data, child1), new(secondData, child2)); break;
            case "parameter": Unequal(new MstatData.MstatMemberDefinition(data, method), new(secondData, changedParam)); break;
            case "return": Unequal(new MstatData.MstatMemberDefinition(data, method), new(secondData, changedReturn)); break;
            case "field": Unequal(new MstatData.MstatMemberDefinition(data, field1), new(secondData, field2)); break;
            case "typespec": Unequal(new MstatData.MstatTypeSpecification(data, spec1), new(secondData, spec2)); break;
            case "methodspec": Unequal(new MstatData.MstatMethodSpecification(data, inst1), new(secondData, inst2)); break;
            case "resource":
                var resources = ModelFixture.Resources(data).ToArray();
                Assert.Equal(new[] { "Fixture.Library/one", "Fixture.Library/two", "Other.Library/one" },
                    resources.Select(r => r.Assembly.Name + "/" + r.Name).OrderBy(x => x));
                var comparisonResources = ModelFixture.Resources(secondData).ToArray();
                foreach (var resource in resources)
                    foreach (var candidate in comparisonResources)
                        Assert.Equal(resource.Name == candidate.Name && resource.Assembly.Name == candidate.Assembly.Name,
                            resource.Equals(candidate));
                break;
            default:
                var frozen = data.GetFrozenObjects().ToArray();
                Unequal(frozen[0], secondData.GetFrozenObjects().Single(f => f.NodeId != frozen[0].NodeId));
                Assert.Equal(new[] { 0, 4 }, frozen.Select(f => f.NodeId).OrderBy(x => x));
                break;
        }
    }

    private static void Unequal<T>(T left, T right) where T : struct, IEquatable<T>
    {
        Assert.False(left.Equals(right));
        Assert.False(right.Equals(left));
        Assert.Equal(2, new HashSet<T> { left, right }.Count);
    }

    [Fact]
    public void Model_InstantiationHashesAreStableAcrossFilesAndDistinguishArguments()
    {
        byte[] arguments = Enumerable.Range(0x02, 0x0e - 0x02 + 1).Select(x => (byte)x).ToArray();

        // The second file declares rows in reverse order so handles differ between files.
        (MstatData Data, TypeSpecificationHandle[] Specs, MethodSpecificationHandle[] Insts) Build(bool reverse)
        {
            var b = new MstatPeBuilder();
            var owner = b.AddType("Owner");
            b.Type(owner, 1);
            var generic = b.AddMethod("Create", owner, generic: true);
            b.Method(generic, 1);
            var order = reverse ? arguments.Reverse().ToArray() : arguments;
            var specs = new Dictionary<byte, TypeSpecificationHandle>();
            var insts = new Dictionary<byte, MethodSpecificationHandle>();
            foreach (byte a in order)
            {
                specs[a] = b.AddTypeSpec(owner, a);
                b.Type(specs[a], 1);
                insts[a] = b.AddMethodSpec(generic, a);
                b.Method(insts[a], 1);
            }
            byte[] bytes = b.Build();
            var data = MstatData.Read(new MemoryStream(bytes), bytes.Length);
            return (data, arguments.Select(a => specs[a]).ToArray(), arguments.Select(a => insts[a]).ToArray());
        }

        var left = Build(reverse: false);
        var right = Build(reverse: true);
        using (left.Data)
        using (right.Data)
        {
            Assert.NotEqual(left.Specs[0], right.Specs[0]);
            for (int i = 0; i < arguments.Length; i++)
            {
                var leftSpec = new MstatData.MstatTypeSpecification(left.Data, left.Specs[i]);
                var rightSpec = new MstatData.MstatTypeSpecification(right.Data, right.Specs[i]);
                Assert.True(leftSpec.Equals(rightSpec));
                Assert.Equal(leftSpec.GetHashCode(), rightSpec.GetHashCode());

                var leftInst = new MstatData.MstatMethodSpecification(left.Data, left.Insts[i]);
                var rightInst = new MstatData.MstatMethodSpecification(right.Data, right.Insts[i]);
                Assert.True(leftInst.Equals(rightInst));
                Assert.Equal(leftInst.GetHashCode(), rightInst.GetHashCode());
            }

            // Instantiations of one generic must not all collide into a single bucket.
            Assert.Equal(arguments.Length, left.Specs.Select(h => new MstatData.MstatTypeSpecification(left.Data, h).GetHashCode()).Distinct().Count());
            Assert.Equal(arguments.Length, left.Insts.Select(h => new MstatData.MstatMethodSpecification(left.Data, h).GetHashCode()).Distinct().Count());
        }
    }

    [Fact]
    public void Model_DisplayFormattingRepresentsRecordedIdentity()
    {
        using var data = ModelFixture.Create().Read();
        var types = ModelFixture.Types(data).ToDictionary(t => t.Name);
        Assert.Equal("Example.Owner", types["Owner"].ToString());
        Assert.Equal("Owner", types["Owner"].ToString(0));
        Assert.Equal("Example.Owner.Child", types["Child"].ToString());
        var members = ModelFixture.Members(data).ToDictionary(m => m.Name);
        Assert.Equal("Run() : Void", members["Run"].ToString());
        Assert.Equal("Example.Owner.Run() : Void", members["Run"].ToQualifiedString());
        Assert.Equal("State : Int32", members["State"].ToString());
        Assert.Equal("Create<>() : Void", members["Create"].ToString());
        Assert.Equal("Example.Generic`1<Int32>", Assert.Single(ModelFixture.Specs(data)).ToString());
        Assert.Equal("Create<Int32>() : Void", Assert.Single(ModelFixture.MethodSpecs(data)).ToString());
        Assert.Equal(new[] { "Example.Generic`1<Int32>", "Example.Other", "Example.Owner" },
            ModelFixture.Frozen(data).Select(f => f.ToString()).OrderBy(x => x));
    }
}
