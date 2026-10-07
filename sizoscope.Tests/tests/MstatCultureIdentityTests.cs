using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class MstatCultureIdentityTests
{
    [Fact]
    public void Assembly_CultureAndDisplayName_RepresentNeutralAndSatelliteIdentity()
    {
        var builder = new MstatPeBuilder();
        var satellite = builder.AddAssembly("App.resources", "fr");
        builder.Type(builder.AddType("NeutralType"), 7);
        builder.Type(builder.AddType("Strings", "Resources", satellite), 11);

        using MstatData data = builder.Read();
        MstatData.MstatAssembly neutral =
            data.GetScopes().Single(assembly => assembly.Name == "Fixture.Library");
        MstatData.MstatAssembly french =
            data.GetScopes().Single(assembly => assembly.Culture == "fr");

        Assert.Equal("Fixture.Library", neutral.Name);
        Assert.Equal("", neutral.Culture);
        Assert.Equal("Fixture.Library", neutral.DisplayName);
        Assert.Equal("App.resources", french.Name);
        Assert.Equal("fr", french.Culture);
        Assert.Equal("App.resources [fr]", french.DisplayName);
    }

    [Fact]
    public void AssemblyEquality_AcrossMstats_DistinguishesCulture()
    {
        using MstatData firstFrench = BuildSatellite("fr");
        using MstatData secondFrench = BuildSatellite("fr");
        using MstatData german = BuildSatellite("de");

        MstatData.MstatAssembly first = Satellite(firstFrench);
        MstatData.MstatAssembly sameIdentity = Satellite(secondFrench);
        MstatData.MstatAssembly differentCulture = Satellite(german);

        Assert.True(first.Equals(sameIdentity));
        Assert.False(first.Equals(differentCulture));
        Assert.Equal("App.resources [fr]", first.DisplayName);
        Assert.Equal("App.resources [de]", differentCulture.DisplayName);
    }

    [Fact]
    public void TypeEquality_AcrossMstats_DistinguishesAssemblyCulture()
    {
        using MstatData firstFrench = BuildSatellite("fr");
        using MstatData secondFrench = BuildSatellite("fr");
        using MstatData german = BuildSatellite("de");

        MstatData.MstatTypeDefinition first =
            Assert.Single(Satellite(firstFrench).GetTypes());
        MstatData.MstatTypeDefinition sameIdentity =
            Assert.Single(Satellite(secondFrench).GetTypes());
        MstatData.MstatTypeDefinition differentCulture =
            Assert.Single(Satellite(german).GetTypes());

        Assert.Equal("Resources.Strings", first.ToString());
        Assert.True(first.Equals(sameIdentity));
        Assert.False(first.Equals(differentCulture));
        Assert.Equal("Resources.Strings", differentCulture.ToString());
    }

    private static MstatData BuildSatellite(string culture)
    {
        var builder = new MstatPeBuilder();
        var assembly = builder.AddAssembly("App.resources", culture);
        builder.Type(builder.AddType("Strings", "Resources", assembly), 11);
        return builder.Read();
    }

    private static MstatData.MstatAssembly Satellite(MstatData data) =>
        data.GetScopes().Single(assembly => assembly.Name == "App.resources");
}
