using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class MstatDataRegressionTests
{
    [Fact]
    public void Read_ThreeMethodComponentsWidenBeforeAddition()
    {
        var builder = new MstatPeBuilder();
        var ownerHandle = builder.AddType("Owner");
        builder.Method(
            builder.AddMethod("Huge", ownerHandle),
            1_100_000_000,
            1_100_000_000,
            1_100_000_000);

        using MstatData data = builder.Read();
        MstatData.MstatAssembly assembly = Assert.Single(data.GetScopes());
        MstatData.MstatTypeDefinition owner = Assert.Single(assembly.GetTypes());
        MstatData.MstatMemberDefinition method = Assert.Single(owner.GetMembers());

        Assert.IsType<long>(method.Size);
        Assert.IsType<long>(method.AggregateSize);
        Assert.IsType<long>(owner.Size);
        Assert.IsType<long>(owner.AggregateSize);
        Assert.IsType<long>(assembly.AggregateSize);
        Assert.IsType<long>(data.Size);
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
    public void Read_CategoryAndBlobAccumulationUsesInt64(
        int first,
        int second,
        long expected)
    {
        var builder = new MstatPeBuilder();
        var typeOne = builder.AddType("One");
        var typeTwo = builder.AddType("Two");
        builder.Type(typeOne, first);
        builder.Type(typeTwo, second);
        builder.Method(builder.AddMethod("M", typeOne), first);
        builder.Method(builder.AddMethod("M", typeTwo), second);
        builder.Blob("Metadata", first);
        builder.Blob("ExternalReferencesTable", second);

        using MstatData data = builder.Read();
        MstatData.MstatAssembly assembly = Assert.Single(data.GetScopes());
        MstatData.MstatTypeDefinition[] types = assembly.GetTypes().ToArray();
        (string Name, long Size)[] blobs = data.Blobs.ToArray();

        Assert.All(types, type =>
        {
            Assert.IsType<long>(type.Size);
            Assert.IsType<long>(type.AggregateSize);
        });
        Assert.All(types.SelectMany(type => type.GetMembers()), member =>
        {
            Assert.IsType<long>(member.Size);
            Assert.IsType<long>(member.AggregateSize);
        });
        Assert.All(blobs, blob => Assert.IsType<long>(blob.Size));
        Assert.IsType<long>(data.BlobsSize);
        Assert.Equal(expected, types.Sum(type => type.Size));
        Assert.Equal(expected, types.SelectMany(type => type.GetMembers()).Sum(member => member.Size));
        Assert.Equal(expected, blobs.Sum(blob => blob.Size));
        Assert.Equal(2 * expected, assembly.AggregateSize);
        Assert.Equal(expected, data.BlobsSize);
        Assert.Equal(3 * expected, data.Size);
        Assert.Equal(2L * first, types.Single(type => type.Name == "One").AggregateSize);
        Assert.Equal(2L * second, types.Single(type => type.Name == "Two").AggregateSize);
    }

    [Fact]
    public void Read_MethodSpecificationSizeAndAggregatesUseInt64()
    {
        var builder = new MstatPeBuilder();
        var ownerHandle = builder.AddType("Owner");
        var definitionHandle = builder.AddMethod("Create", ownerHandle, generic: true);
        var specificationHandle = builder.AddMethodSpec(definitionHandle, 0x08);
        builder.Method(
            specificationHandle,
            1_100_000_000,
            1_100_000_000,
            1_100_000_000);

        using MstatData data = builder.Read();
        MstatData.MstatAssembly assembly = Assert.Single(data.GetScopes());
        MstatData.MstatTypeDefinition owner = Assert.Single(assembly.GetTypes());
        MstatData.MstatMemberDefinition definition = Assert.Single(owner.GetMembers());
        MstatData.MstatMethodSpecification specification =
            Assert.Single(definition.GetInstantiations());

        Assert.Equal(specificationHandle, specification.Handle);
        Assert.IsType<long>(specification.Size);
        Assert.Equal(3_300_000_000L, specification.Size);
        Assert.IsType<long>(definition.AggregateSize);
        Assert.Equal(3_300_000_000L, definition.AggregateSize);
        Assert.Equal(3_300_000_000L, owner.AggregateSize);
        Assert.Equal(3_300_000_000L, assembly.AggregateSize);
        Assert.Equal(3_300_000_000L, data.Size);
    }

    [Fact]
    public void Read_TypeSpecificationSizeAndAggregatesUseInt64()
    {
        var builder = new MstatPeBuilder();
        var definitionHandle = builder.AddType("Generic`1");
        var specificationHandle = builder.AddTypeSpec(definitionHandle, 0x08);
        builder.Type(specificationHandle, 1_100_000_000);
        builder.Method(
            builder.AddMethod("Build", specificationHandle),
            1_100_000_000);

        using MstatData data = builder.Read();
        MstatData.MstatAssembly assembly = Assert.Single(data.GetScopes());
        MstatData.MstatTypeDefinition definition = Assert.Single(assembly.GetTypes());
        MstatData.MstatTypeSpecification specification =
            Assert.Single(definition.GetTypeSpecifications());
        MstatData.MstatMemberDefinition member = Assert.Single(specification.GetMembers());

        Assert.Equal(specificationHandle, specification.Handle);
        Assert.IsType<long>(specification.Size);
        Assert.IsType<long>(specification.AggregateSize);
        Assert.Equal(1_100_000_000L, specification.Size);
        Assert.Equal(2_200_000_000L, specification.AggregateSize);
        Assert.Equal(1_100_000_000L, member.Size);
        Assert.Equal(2_200_000_000L, definition.AggregateSize);
        Assert.Equal(2_200_000_000L, assembly.AggregateSize);
        Assert.Equal(2_200_000_000L, data.Size);
    }

    [Theory]
    [InlineData("fields", 2_200_000_000L, 2_200_000_000L, 0L)]
    [InlineData("resources", 0L, 2_200_000_000L, 0L)]
    [InlineData("owned-frozen", 2_200_000_000L, 2_200_000_000L, 0L)]
    [InlineData("unowned-frozen", 0L, 0L, 2_200_000_000L)]
    public void Read_OtherCategoryAccumulationUsesInt64(
        string category,
        long ownerSize,
        long assemblySize,
        long unownedSize)
    {
        var builder = new MstatPeBuilder();
        var ownerHandle = builder.AddType("Owner");
        builder.Type(ownerHandle, 0);

        switch (category)
        {
            case "fields":
                builder.Field(builder.AddField("First", ownerHandle), 1_100_000_000);
                builder.Field(builder.AddField("Second", ownerHandle), 1_100_000_000);
                break;
            case "resources":
                builder.Resource("first", 1_100_000_000);
                builder.Resource("second", 1_100_000_000);
                break;
            case "owned-frozen":
                builder.Frozen(ownerHandle, 1_100_000_000, builder.AddName("one"), ownerHandle);
                builder.Frozen(ownerHandle, 1_100_000_000, builder.AddName("two"), ownerHandle);
                break;
            case "unowned-frozen":
                builder.Frozen(ownerHandle, 1_100_000_000, builder.AddName("one"));
                builder.Frozen(ownerHandle, 1_100_000_000, builder.AddName("two"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(category));
        }

        using MstatData data = builder.Read();
        MstatData.MstatAssembly assembly = Assert.Single(data.GetScopes());
        MstatData.MstatTypeDefinition owner = Assert.Single(assembly.GetTypes());

        Assert.IsType<long>(data.UnownedFrozenObjectSize);
        Assert.Equal(ownerSize, owner.AggregateSize);
        Assert.Equal(assemblySize, assembly.AggregateSize);
        Assert.Equal(unownedSize, data.UnownedFrozenObjectSize);
        Assert.Equal(2_200_000_000L, data.Size);

        if (category == "fields")
        {
            MstatData.MstatMemberDefinition[] fields = owner.GetMembers().ToArray();
            Assert.Equal(new[] { "First", "Second" }, fields.Select(field => field.Name).OrderBy(name => name));
            Assert.All(fields, field =>
            {
                Assert.True(field.IsField);
                Assert.IsType<long>(field.Size);
                Assert.IsType<long>(field.AggregateSize);
                Assert.Equal(1_100_000_000L, field.Size);
                Assert.Equal(1_100_000_000L, field.AggregateSize);
            });
            Assert.Equal(2_200_000_000L, fields.Sum(field => field.Size));
        }
        else if (category == "resources")
        {
            MstatData.MstatManifestResource[] resources =
                assembly.GetManifestResources().ToArray();
            Assert.Equal(new[] { "first", "second" }, resources.Select(resource => resource.Name).OrderBy(name => name));
            Assert.All(resources, resource =>
            {
                Assert.IsType<long>(resource.Size);
                Assert.Equal(1_100_000_000L, resource.Size);
                Assert.Equal(assembly, resource.Assembly);
            });
            Assert.Equal(2_200_000_000L, resources.Sum(resource => resource.Size));
        }
        else
        {
            MstatData.MstatFrozenObject[] frozen =
                (category == "owned-frozen"
                    ? owner.GetFrozenObjects()
                    : data.GetFrozenObjects()).ToArray();
            Assert.Equal(new[] { 0, 4 }, frozen.Select(item => item.NodeId).OrderBy(id => id));
            Assert.All(frozen, item =>
            {
                Assert.IsType<long>(item.Size);
                Assert.Equal(1_100_000_000L, item.Size);
                Assert.Equal(ownerHandle, item.InstanceType);
                Assert.Equal(category == "owned-frozen", !item.OwningEntity.IsNil);
            });
            Assert.Equal(2_200_000_000L, frozen.Sum(item => item.Size));
        }
    }

    [Fact]
    public void Read_ShortInputThrowsEndOfStreamException()
    {
        var builder = new MstatPeBuilder();
        builder.Type(builder.AddType("Owner"), 11);
        byte[] complete = builder.Build();
        using var truncated = new MemoryStream(complete[..^1], writable: false);

        Assert.Throws<EndOfStreamException>(
            () => MstatData.Read(truncated, complete.LongLength));
    }

    [Theory]
    [InlineData(-1L, typeof(ArgumentOutOfRangeException))]
    [InlineData(2_147_483_648L, typeof(OverflowException))]
    public void Read_InvalidLengthFailsBeforeReading(long length, Type expectedException)
    {
        using var stream = new ThrowIfReadStream();

        Exception exception = Assert.Throws(
            expectedException,
            () => MstatData.Read(stream, length));

        Assert.Equal(expectedException, exception.GetType());
        Assert.False(stream.WasRead);
    }

    [Fact]
    public void Dispose_ReleasesOwnedInputAfterAssertions()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var builder = new MstatPeBuilder();
        builder.Type(builder.AddType("Owner"), 11);
        string path = InputFixtureBuilder.Plain(temp, "app.mstat", builder.Build());

        using (FileStream stream = File.OpenRead(path))
        using (MstatData data = MstatData.Read(stream, stream.Length))
        {
            Assert.Equal(11L, data.Size);
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    private sealed class ThrowIfReadStream : Stream
    {
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            throw new InvalidOperationException("The stream must not be read.");
        }

        public override int Read(Span<byte> buffer)
        {
            WasRead = true;
            throw new InvalidOperationException("The stream must not be read.");
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
