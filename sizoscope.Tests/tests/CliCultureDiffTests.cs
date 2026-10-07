using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class CliCultureDiffTests
{
    [Fact]
    public async Task Cli_DiffReportsMethodBodyFoldingWithoutChangingAccountedTotal()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());

        string plain = InputFixtureBuilder.Plain(
            temp,
            "plain.mstat",
            FoldingFixture(includeAliases: false));
        string folded = InputFixtureBuilder.Plain(
            temp,
            "folded.mstat",
            FoldingFixture(includeAliases: true));

        (int exitCode, string output, string error) =
            await CliProcess.Run(plain, folded);

        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Assert.StartsWith(
            "Total accounted size difference: 0 B" + Environment.NewLine,
            output);
        Assert.Contains(
            "=== Method body folding (not part of the total) ===",
            output);
        Assert.Contains("Folded methods: 0 -> 3", output);
        Assert.Contains(
            "Estimated folding savings: 0 B -> 210 B",
            output);
    }

    [Fact]
    public async Task Cli_DiffReportsFoldingSavingsChangeWhenAliasCountIsUnchanged()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());

        string before = InputFixtureBuilder.Plain(
            temp,
            "before.mstat",
            SingleFoldFixture(bodySize: 10, aliasCount: 1));
        string after = InputFixtureBuilder.Plain(
            temp,
            "after.mstat",
            SingleFoldFixture(bodySize: 20, aliasCount: 1));

        (int exitCode, string output, string error) =
            await CliProcess.Run(before, after);

        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Assert.Contains("Folded methods: 1 -> 1", output);
        Assert.Contains(
            "Estimated folding savings: 10 B -> 20 B",
            output);
    }

    [Fact]
    public async Task Cli_DiffReportsAliasCountChangeWhenFoldingSavingsIsUnchanged()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());

        string before = InputFixtureBuilder.Plain(
            temp,
            "before.mstat",
            SingleFoldFixture(bodySize: 20, aliasCount: 1));
        string after = InputFixtureBuilder.Plain(
            temp,
            "after.mstat",
            SingleFoldFixture(bodySize: 10, aliasCount: 2));

        (int exitCode, string output, string error) =
            await CliProcess.Run(before, after);

        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Assert.Contains("Folded methods: 1 -> 2", output);
        Assert.Contains(
            "Estimated folding savings: 20 B -> 20 B",
            output);
    }

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

    private static byte[] FoldingFixture(bool includeAliases)
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        var large = builder.AddMethod("Large", owner);
        var small = builder.AddMethod("Small", owner);
        builder.Method(large, 100);
        builder.Method(small, 10);
        if (includeAliases)
        {
            builder.Folded(builder.AddMethod("LargeAliasA", owner), large);
            builder.Folded(builder.AddMethod("LargeAliasB", owner), large);
            builder.Folded(builder.AddMethod("SmallAlias", owner), small);
        }

        return builder.Build();
    }

    private static byte[] SingleFoldFixture(int bodySize, int aliasCount)
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        var kept = builder.AddMethod("Run", owner);
        builder.Method(kept, bodySize);
        for (int i = 0; i < aliasCount; i++)
            builder.Folded(builder.AddMethod($"Alias{i}", owner), kept);

        return builder.Build();
    }
}
