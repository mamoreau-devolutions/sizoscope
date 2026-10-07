using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class CliCultureDiffTests
{
    [Fact]
    public async Task Cli_DiffSatelliteAssemblies_ShowsCultureAndExactRemovedAggregate()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string before = InputFixtureBuilder.Plain(
            temp,
            "before.mstat",
            Satellites(includeGerman: true));
        string after = InputFixtureBuilder.Plain(
            temp,
            "after.mstat",
            Satellites(includeGerman: false));

        (int exitCode, string output, string error) =
            await CliProcess.Run(before, after);

        string expected =
            "Total accounted size difference: -200 B" + Environment.NewLine
            + Environment.NewLine
            + "=== Removed / Shrunk ===" + Environment.NewLine
            + "  -200 B        App.resources [de]" + Environment.NewLine
            + Environment.NewLine;
        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Assert.Equal(expected, output);
        Assert.DoesNotContain("App.resources [fr]", output);
    }

    private static byte[] Satellites(bool includeGerman)
    {
        var builder = new MstatPeBuilder();
        var french = builder.AddAssembly("App.resources", "fr");
        builder.Method(
            builder.AddMethod("Get", builder.AddType("Strings", "Resources", french)),
            100);
        if (includeGerman)
        {
            var german = builder.AddAssembly("App.resources", "de");
            builder.Method(
                builder.AddMethod("Get", builder.AddType("Strings", "Resources", german)),
                200);
        }

        return builder.Build();
    }
}
