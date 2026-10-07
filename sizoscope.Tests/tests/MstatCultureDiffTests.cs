using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class MstatCultureDiffTests
{
    [Fact]
    public void Diff_SameNameSameCultureAssembly_MatchesWithoutResiduals()
    {
        using MstatData left = Build(("fr", 17, 0));
        using MstatData right = Build(("fr", 17, 0));
        (MstatData removed, MstatData added) = MstatData.Diff(left, right);

        using (removed)
        using (added)
        {
            Assert.Empty(NonEmptyAssemblies(removed));
            Assert.Empty(NonEmptyAssemblies(added));
            Assert.All(removed.GetScopes(), assembly => Assert.Equal(0L, assembly.AggregateSize));
            Assert.All(added.GetScopes(), assembly => Assert.Equal(0L, assembly.AggregateSize));
        }
    }

    [Fact]
    public void Diff_SameNameDifferentCultureSatellites_KeepExactRemovedAndAddedAggregates()
    {
        using MstatData left = Build(("fr", 17, 0), ("de", 19, 23));
        using MstatData right = Build(("fr", 17, 0), ("es", 29, 31));
        (MstatData removed, MstatData added) = MstatData.Diff(left, right);

        using (removed)
        using (added)
        {
            (string DisplayName, long AggregateSize)[] removedAssemblies =
                NonEmptyAssemblies(removed);
            (string DisplayName, long AggregateSize)[] addedAssemblies =
                NonEmptyAssemblies(added);

            Assert.Equal(
                new[] { ("App.resources [de]", 42L) },
                removedAssemblies);
            Assert.Equal(
                new[] { ("App.resources [es]", 60L) },
                addedAssemblies);
            Assert.Equal(42L, removedAssemblies.Sum(assembly => assembly.AggregateSize));
            Assert.Equal(60L, addedAssemblies.Sum(assembly => assembly.AggregateSize));
            Assert.DoesNotContain(
                removed.GetScopes(),
                assembly => assembly.Culture == "fr" && assembly.AggregateSize != 0);
            Assert.DoesNotContain(
                added.GetScopes(),
                assembly => assembly.Culture == "fr" && assembly.AggregateSize != 0);
        }
    }

    private static MstatData Build(
        params (string Culture, int TypeSize, int MethodSize)[] satellites)
    {
        var builder = new MstatPeBuilder();
        foreach ((string culture, int typeSize, int methodSize) in satellites)
        {
            var assembly = builder.AddAssembly("App.resources", culture);
            var type = builder.AddType("Strings", "Resources", assembly);
            builder.Type(type, typeSize);
            if (methodSize != 0)
                builder.Method(builder.AddMethod("Get", type), methodSize);
        }

        return builder.Read();
    }

    private static (string DisplayName, long AggregateSize)[] NonEmptyAssemblies(
        MstatData data) =>
        data.GetScopes()
            .Where(assembly => assembly.AggregateSize != 0)
            .Select(assembly => (assembly.DisplayName, assembly.AggregateSize))
            .OrderBy(assembly => assembly.DisplayName, StringComparer.Ordinal)
            .ToArray();
}
