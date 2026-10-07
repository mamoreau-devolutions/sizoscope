using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class MstatDiffLifetimeTests
{
    [Fact]
    public void Diff_RemainsReadableAfterOriginalsAreDisposed()
    {
        MstatData left = BuildSingleType("Removed", 2_200_000_000L);
        MstatData right = BuildSingleType("Added", 2_300_000_000L);
        (MstatData removed, MstatData added) = MstatData.Diff(left, right);

        left.Dispose();
        right.Dispose();

        using (removed)
        using (added)
        {
            AssertSide(removed, "Removed", 2_200_000_000L);
            AssertSide(added, "Added", 2_300_000_000L);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Diff_DisposeOrderingDoesNotInvalidateSibling(bool disposeRemovedFirst)
    {
        MstatData left = BuildSingleType("Removed", 2_200_000_000L);
        MstatData right = BuildSingleType("Added", 2_300_000_000L);
        (MstatData removed, MstatData added) = MstatData.Diff(left, right);

        left.Dispose();
        right.Dispose();

        MstatData disposed = disposeRemovedFirst ? removed : added;
        MstatData survivor = disposeRemovedFirst ? added : removed;
        string survivorName = disposeRemovedFirst ? "Added" : "Removed";
        long survivorSize = disposeRemovedFirst ? 2_300_000_000L : 2_200_000_000L;

        disposed.Dispose();
        try
        {
            AssertSide(survivor, survivorName, survivorSize);
        }
        finally
        {
            survivor.Dispose();
        }
    }

    private static MstatData BuildSingleType(string name, long total)
    {
        var builder = new MstatPeBuilder();
        var type = builder.AddType(name);
        int first = checked((int)(total / 2));
        int second = checked((int)(total - first));
        builder.Type(type, first);
        builder.Method(builder.AddMethod("Body", type), second);
        return builder.Read();
    }

    private static void AssertSide(MstatData data, string expectedName, long expectedSize)
    {
        MstatData.MstatAssembly assembly = Assert.Single(data.GetScopes());
        MstatData.MstatTypeDefinition type = Assert.Single(assembly.GetTypes());
        MstatData.MstatMemberDefinition member = Assert.Single(type.GetMembers());

        Assert.Equal(expectedName, type.Name);
        Assert.Equal(expectedSize / 2, type.Size);
        Assert.Equal(expectedSize - (expectedSize / 2), member.Size);
        Assert.Equal(expectedSize, type.AggregateSize);
        Assert.Equal(expectedSize, assembly.AggregateSize);
    }
}
