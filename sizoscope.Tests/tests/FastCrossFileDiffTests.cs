using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class FastCrossFileDiffTests
{
    [Fact]
    public void ReorderedGenericSignaturesMatchAcrossFiles()
    {
        using MstatData left = GenericFixture(reordered: false, argument: 0x08);
        using MstatData right = GenericFixture(reordered: true, argument: 0x08);
        (MstatData removed, MstatData added) = MstatData.Diff(left, right);

        using (removed)
        using (added)
        {
            Assert.Empty(TypeSpecifications(removed));
            Assert.Empty(TypeSpecifications(added));
            Assert.Empty(MethodSpecifications(removed));
            Assert.Empty(MethodSpecifications(added));
            Assert.All(removed.GetScopes(), assembly => Assert.Equal(0L, assembly.AggregateSize));
            Assert.All(added.GetScopes(), assembly => Assert.Equal(0L, assembly.AggregateSize));
        }
    }

    [Fact]
    public void DifferentGenericArgumentsRemainDistinctAcrossFiles()
    {
        using MstatData left = GenericFixture(reordered: false, argument: 0x08);
        using MstatData right = GenericFixture(reordered: true, argument: 0x0e);
        (MstatData removed, MstatData added) = MstatData.Diff(left, right);

        using (removed)
        using (added)
        {
            Assert.Single(TypeSpecifications(removed));
            Assert.Single(TypeSpecifications(added));
            Assert.Single(MethodSpecifications(removed));
            Assert.Single(MethodSpecifications(added));
        }
    }

    [Fact]
    public void BoundedArraySignaturesCompareAllSizesAndLowerBounds()
    {
        using MstatData left = ArrayFixture(reordered: false, sizes: [4, 5], lowerBounds: [0, 1]);
        using MstatData same = ArrayFixture(reordered: true, sizes: [4, 5], lowerBounds: [0, 1]);
        using MstatData changed = ArrayFixture(reordered: true, sizes: [4, 6], lowerBounds: [0, 1]);

        (MstatData removed, MstatData added) = MstatData.Diff(left, same);
        using (removed)
        using (added)
        {
            Assert.Empty(TypeSpecifications(removed));
            Assert.Empty(TypeSpecifications(added));
        }

        (removed, added) = MstatData.Diff(left, changed);
        using (removed)
        using (added)
        {
            Assert.Single(TypeSpecifications(removed));
            Assert.Single(TypeSpecifications(added));
        }
    }

    private static MstatData GenericFixture(bool reordered, byte argument)
    {
        var builder = new MstatPeBuilder();
        if (reordered)
            builder.AddType("Padding");
        var definition = builder.AddType("Generic`1");
        var typeSpec = builder.AddTypeSpec(definition, argument);
        var method = builder.AddMethod("Create", definition, generic: true);
        var methodSpec = builder.AddMethodSpec(method, argument);
        builder.Type(typeSpec, 11);
        builder.Method(methodSpec, 13);
        return builder.Read();
    }

    private static MstatData ArrayFixture(
        bool reordered,
        int[] sizes,
        int[] lowerBounds)
    {
        var builder = new MstatPeBuilder();
        if (reordered)
            builder.AddType("Padding");
        var element = builder.AddType("Element");
        var array = builder.AddArrayTypeSpec(element, rank: 2, sizes, lowerBounds);
        builder.Type(array, 17);
        return builder.Read();
    }

    private static MstatData.MstatTypeSpecification[] TypeSpecifications(MstatData data) =>
        data.GetScopes()
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetTypeSpecifications())
            .ToArray();

    private static MstatData.MstatMethodSpecification[] MethodSpecifications(MstatData data) =>
        data.GetScopes()
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMembers())
            .SelectMany(method => method.GetInstantiations())
            .ToArray();
}
