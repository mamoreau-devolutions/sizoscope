using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class MstatFinalGateTests
{
    [Fact]
    public void Read_MethodSpecComponentsWidenBeforeAddition()
    {
        var b = new MstatPeBuilder();
        var ownerHandle = b.AddType("Owner");
        var definitionHandle = b.AddMethod("Create", ownerHandle, generic: true);
        var specHandle = b.AddMethodSpec(definitionHandle, 0x08);
        b.Method(specHandle, 1_100_000_000, 1_100_000_000, 1_100_000_000);

        using var data = b.Read();
        var assembly = Assert.Single(data.GetScopes());
        var owner = Assert.Single(assembly.GetTypes());
        var definition = Assert.Single(owner.GetMembers());
        var spec = Assert.Single(definition.GetInstantiations());
        Assert.Equal(specHandle, spec.Handle);
        Assert.Equal("Create<Int32>() : Void", spec.ToString());
        Assert.Equal(3_300_000_000L, spec.Size);
        Assert.Equal(definitionHandle, definition.Handle);
        Assert.Equal(0L, definition.Size);
        Assert.Equal(3_300_000_000L, definition.AggregateSize);
        Assert.Equal(ownerHandle, owner.Handle);
        Assert.Equal(0L, owner.Size);
        Assert.Equal(3_300_000_000L, owner.AggregateSize);
        Assert.Equal(3_300_000_000L, assembly.AggregateSize);
        Assert.Equal(3_300_000_000L, data.Size);
    }

    [Theory]
    [InlineData("fields", 2_200_000_000L, 2_200_000_000L, 0L)]
    [InlineData("resources", 0L, 2_200_000_000L, 0L)]
    [InlineData("owned-frozen", 2_200_000_000L, 2_200_000_000L, 0L)]
    [InlineData("unowned-frozen", 0L, 0L, 2_200_000_000L)]
    public void Read_OtherCategoryAccumulationUsesInt64(string category, long ownerSize, long assemblySize, long unownedSize)
    {
        var b = new MstatPeBuilder();
        var ownerHandle = b.AddType("Owner");
        b.Type(ownerHandle, 0);
        switch (category)
        {
            case "fields":
                b.Field(b.AddField("First", ownerHandle), 1_100_000_000);
                b.Field(b.AddField("Second", ownerHandle), 1_100_000_000);
                break;
            case "resources":
                b.Resource("first", 1_100_000_000);
                b.Resource("second", 1_100_000_000);
                break;
            case "owned-frozen":
                b.Frozen(ownerHandle, 1_100_000_000, b.AddName("one"), ownerHandle);
                b.Frozen(ownerHandle, 1_100_000_000, b.AddName("two"), ownerHandle);
                break;
            case "unowned-frozen":
                b.Frozen(ownerHandle, 1_100_000_000, b.AddName("one"));
                b.Frozen(ownerHandle, 1_100_000_000, b.AddName("two"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(category));
        }

        using var data = b.Read();
        var assembly = Assert.Single(data.GetScopes());
        var owner = Assert.Single(assembly.GetTypes());
        Assert.Equal(0L, owner.Size);
        Assert.Equal(ownerSize, owner.AggregateSize);
        Assert.Equal(assemblySize, assembly.AggregateSize);
        Assert.Equal(unownedSize, data.UnownedFrozenObjectSize);
        Assert.Equal(0L, data.BlobsSize);
        Assert.Equal(2_200_000_000L, data.Size);
        if (category == "fields")
        {
            var fields = owner.GetMembers().ToArray();
            Assert.Equal(new[] { "First", "Second" }, fields.Select(f => f.Name).OrderBy(x => x));
            Assert.Equal(2, fields.Select(f => f.Handle).Distinct().Count());
            Assert.All(fields, f =>
            {
                Assert.True(f.IsField);
                Assert.Equal(owner, f.OwnerAsDef);
                Assert.Equal(1_100_000_000L, f.Size);
                Assert.Equal(1_100_000_000L, f.AggregateSize);
            });
            Assert.Equal(2_200_000_000L, fields.Sum(f => f.Size));
        }
        else if (category == "resources")
        {
            var resources = assembly.GetManifestResources().ToArray();
            Assert.Equal(new[] { "first", "second" }, resources.Select(r => r.Name).OrderBy(x => x));
            Assert.Equal(2, resources.Select(r => r.Handle).Distinct().Count());
            Assert.All(resources, r =>
            {
                Assert.Equal(assembly, r.Assembly);
                Assert.Equal(1_100_000_000L, r.Size);
            });
            Assert.Equal(2_200_000_000L, resources.Sum(r => r.Size));
        }
        else
        {
            var frozen = (category == "owned-frozen" ? owner.GetFrozenObjects() : data.GetFrozenObjects()).ToArray();
            Assert.Equal(new[] { 0, 4 }, frozen.Select(f => f.NodeId).OrderBy(x => x));
            Assert.Equal(2, frozen.Select(f => f.Handle).Distinct().Count());
            Assert.All(frozen, f =>
            {
                Assert.Equal(ownerHandle, f.InstanceType);
                Assert.Equal(1_100_000_000L, f.Size);
                if (category == "owned-frozen") Assert.Equal(ownerHandle, f.OwningEntity);
                else Assert.True(f.OwningEntity.IsNil);
            });
            Assert.Equal(2_200_000_000L, frozen.Sum(f => f.Size));
            if (category == "owned-frozen") Assert.Empty(data.GetFrozenObjects());
            else Assert.Empty(owner.GetFrozenObjects());
        }
    }

    [Fact]
    public void Model_SameArityParameterTypesStayDistinct()
    {
        var l = new MstatPeBuilder();
        var leftOwner = l.AddType("Owner");
        var intHandle = l.AddMethod("Run", leftOwner, parameters: new byte[] { 0x08 });
        l.Method(intHandle, 13);
        var r = new MstatPeBuilder(padding: true);
        var rightOwner = r.AddType("Owner");
        var stringHandle = r.AddMethod("Run", rightOwner, parameters: new byte[] { 0x0e });
        r.Method(stringHandle, 17);
        using var left = l.Read();
        using var right = r.Read();
        var intMethod = Assert.Single(Assert.Single(Assert.Single(left.GetScopes()).GetTypes()).GetMembers());
        var stringMethod = Assert.Single(Assert.Single(right.GetScopes(), a => a.Name == "Fixture.Library").GetTypes()).GetMembers().Single();

        Assert.Equal("Example.Owner.Run(Int32) : Void", intMethod.ToQualifiedString());
        Assert.Equal("Example.Owner.Run(String) : Void", stringMethod.ToQualifiedString());
        Assert.True(intMethod.OwnerAsDef.Equals(stringMethod.OwnerAsDef));
        Assert.True(stringMethod.OwnerAsDef.Equals(intMethod.OwnerAsDef));
        Assert.Equal(new byte[] { 0x00, 0x01, 0x01, 0x08 },
            left.MetadataReader.GetBlobBytes(left.MetadataReader.GetMemberReference(intHandle).Signature));
        Assert.Equal(new byte[] { 0x00, 0x01, 0x01, 0x0e },
            right.MetadataReader.GetBlobBytes(right.MetadataReader.GetMemberReference(stringHandle).Signature));
        Assert.False(intMethod.Equals(stringMethod));
        Assert.False(stringMethod.Equals(intMethod));
    }

    [Fact]
    public void Diff_SameArityParameterTypesRemainDistinct()
    {
        var l = new MstatPeBuilder();
        var leftOwner = l.AddType("Owner");
        l.Type(leftOwner, 11);
        l.Method(l.AddMethod("Run", leftOwner, parameters: new byte[] { 0x08 }), 13);
        var r = new MstatPeBuilder(padding: true);
        var rightOwner = r.AddType("Owner");
        r.Type(rightOwner, 11);
        r.Method(r.AddMethod("Run", rightOwner, parameters: new byte[] { 0x0e }), 17);
        using var left = l.Read();
        using var right = r.Read();
        Check(left, right, "Example.Owner.Run(Int32) : Void", 13L, "Example.Owner.Run(String) : Void", 17L);
        Check(right, left, "Example.Owner.Run(String) : Void", 17L, "Example.Owner.Run(Int32) : Void", 13L);

        static void Check(MstatData first, MstatData second, string removedName, long removedSize, string addedName, long addedSize)
        {
            var (removed, added) = MstatData.Diff(first, second);
            using (removed)
            using (added)
            {
                AssertSide(removed, removedName, removedSize);
                AssertSide(added, addedName, addedSize);
            }
        }

        static void AssertSide(MstatData data, string name, long size)
        {
            var assembly = Assert.Single(data.GetScopes(), a => a.Name == "Fixture.Library");
            Assert.All(data.GetScopes().Where(a => a.Name != "Fixture.Library"), a => Assert.Empty(a.GetTypes()));
            var owner = Assert.Single(assembly.GetTypes());
            var method = Assert.Single(owner.GetMembers());
            Assert.Equal(name, method.ToQualifiedString());
            Assert.Equal(size, method.Size);
            Assert.Equal(size, method.AggregateSize);
            Assert.Equal(0L, owner.Size); // The unchanged type's own bytes are not a removed/added identity.
            Assert.Equal(size, owner.AggregateSize);
            Assert.Equal(size, assembly.AggregateSize);
            Assert.Empty(owner.GetNestedTypes());
            Assert.Empty(owner.GetTypeSpecifications());
            Assert.Empty(owner.GetFrozenObjects());
            Assert.Empty(method.GetInstantiations());
            Assert.Empty(assembly.GetManifestResources());
            Assert.Empty(data.GetFrozenObjects());
        }
    }

    [Fact]
    public void Model_GenericTypeSpecsWithDifferentNamespacesStayDistinct()
    {
        var l = new MstatPeBuilder();
        var leftDefinition = l.AddType("Generic`1", "Example");
        var leftSpec = l.AddTypeSpec(leftDefinition, 0x08);
        l.Type(leftSpec, 11);
        var r = new MstatPeBuilder();
        var rightDefinition = r.AddType("Generic`1", "Different");
        var rightSpec = r.AddTypeSpec(rightDefinition, 0x08);
        r.Type(rightSpec, 13);
        using var left = l.Read();
        using var right = r.Read();
        var leftAssembly = Assert.Single(left.GetScopes());
        var rightAssembly = Assert.Single(right.GetScopes());
        var example = Assert.Single(Assert.Single(leftAssembly.GetTypes()).GetTypeSpecifications());
        var different = Assert.Single(Assert.Single(rightAssembly.GetTypes()).GetTypeSpecifications());
        Assert.Equal("Fixture.Library", leftAssembly.Name);
        Assert.Equal("Fixture.Library", rightAssembly.Name);
        Assert.True(leftAssembly.Equals(rightAssembly));
        Assert.Equal("Example.Generic`1<Int32>", example.ToString());
        Assert.Equal("Different.Generic`1<Int32>", different.ToString());
        // Identical coded indices/argument bytes across readers: only the referenced namespace differs.
        Assert.Equal(new byte[] { 0x15, 0x12, 0x05, 0x01, 0x08 },
            left.MetadataReader.GetBlobBytes(left.MetadataReader.GetTypeSpecification(leftSpec).Signature));
        Assert.Equal(new byte[] { 0x15, 0x12, 0x05, 0x01, 0x08 },
            right.MetadataReader.GetBlobBytes(right.MetadataReader.GetTypeSpecification(rightSpec).Signature));
        Assert.False(example.Equals(different));
        Assert.False(different.Equals(example));
    }

    [Theory]
    [InlineData(-1, 0x15, 24L, 35L)]
    [InlineData(0, 0x16, 25L, 36L)]
    [InlineData(1, 0x17, 26L, 37L)]
    [InlineData(2, 0x18, 27L, 38L)]
    [InlineData(3, 0x19, 28L, 39L)]
    [InlineData(4, 0x1a, 29L, 40L)]
    [InlineData(5, 0x1b, 30L, 41L)]
    [InlineData(6, 0x1c, 31L, 42L)]
    [InlineData(7, 0x1d, 32L, 43L)]
    [InlineData(8, 0x1e, 33L, 44L)]
    public void Read_CompactIntegerOpcodesDecodeExactValues(int code, int opcode, long methodSize, long totalSize)
    {
        var b = new MstatPeBuilder();
        var ownerHandle = b.AddType("Owner");
        var methodHandle = b.AddMethod("Run", ownerHandle);
        b.TypeWithCompactConstants(ownerHandle, 11, nodeId: 0);
        b.MethodWithCompactConstants(methodHandle, code, 17, 8, nodeId: 0);
        byte[] bytes = b.Build();

        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            var metadata = pe.GetMetadataReader();
            var types = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition).Single(m => metadata.GetString(m.Name) == "Types");
            var methods = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition).Single(m => metadata.GetString(m.Name) == "Methods");
            Assert.Equal(new byte[] { 0xd0, 0x01, 0x00, 0x00, 0x01, 0x1f, 0x0b, 0x16 },
                pe.GetMethodBody(types.RelativeVirtualAddress).GetILBytes());
            Assert.Equal(new byte[] { 0xd0, 0x01, 0x00, 0x00, 0x0a, (byte)opcode, 0x1f, 0x11, 0x1e, 0x16 },
                pe.GetMethodBody(methods.RelativeVirtualAddress).GetILBytes());
        }
        using var stream = new MemoryStream(bytes);
        using var data = MstatData.Read(stream, bytes.Length);
        var assembly = Assert.Single(data.GetScopes());
        var owner = Assert.Single(assembly.GetTypes());
        var method = Assert.Single(owner.GetMembers());
        Assert.Equal(11L, owner.Size);
        Assert.Equal(0, owner.NodeId);
        Assert.Equal(methodSize, method.Size);
        Assert.Equal(methodSize, method.AggregateSize);
        Assert.Equal(0, method.NodeId);
        Assert.Equal(totalSize, owner.AggregateSize);
        Assert.Equal(totalSize, assembly.AggregateSize);
        Assert.Equal(totalSize, data.Size);
    }

    [Fact]
    public void Read_NegativeShortIntegerOperandIsSignExtended()
    {
        var builder = new MstatPeBuilder();
        var ownerHandle = builder.AddType("Owner");
        var methodHandle = builder.AddMethod("Run", ownerHandle);
        builder.TypeWithCompactConstants(ownerHandle, 11, nodeId: 0);
        builder.MethodWithCompactConstants(methodHandle, -2, 17, 8, nodeId: 0);
        byte[] bytes = builder.Build();

        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            var metadata = pe.GetMetadataReader();
            var methods = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
                .Single(method => metadata.GetString(method.Name) == "Methods");
            Assert.Equal(new byte[] { 0xd0, 0x01, 0x00, 0x00, 0x0a, 0x1f, 0xfe, 0x1f, 0x11, 0x1e, 0x16 },
                pe.GetMethodBody(methods.RelativeVirtualAddress).GetILBytes());
        }
        using var stream = new MemoryStream(bytes);
        using var data = MstatData.Read(stream, bytes.Length);
        var assembly = Assert.Single(data.GetScopes());
        var owner = Assert.Single(assembly.GetTypes());
        var method = Assert.Single(owner.GetMembers());
        Assert.Equal(11L, owner.Size);
        Assert.Equal(0, owner.NodeId);
        Assert.Equal(23L, method.Size);
        Assert.Equal(23L, method.AggregateSize);
        Assert.Equal(0, method.NodeId);
        Assert.Equal(34L, owner.AggregateSize);
        Assert.Equal(34L, assembly.AggregateSize);
        Assert.Equal(34L, data.Size);
    }
}
