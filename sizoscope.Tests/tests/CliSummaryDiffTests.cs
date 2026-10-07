using System.Text;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class CliSummaryDiffTests
{
    [Fact]
    public async Task SingleFileSummaryRanksAssembliesAndTypesFromZipAndHonorsOutput()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string input = InputFixtureBuilder.Zip(
            temp,
            "app.zip",
            ("nested/app.mstat", SummaryFixture()));

        (int exitCode, string output, string error) =
            await CliProcess.Run(input, "--top", "1");

        string nl = Environment.NewLine;
        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Assert.StartsWith("Total accounted size: 60 B" + nl + nl, output);
        Assert.Contains("60 B          100.0% Fixture.Library", output);
        Assert.Contains("=== Top 1 types ===", output);
        Assert.Contains("40 B           66.7% [Fixture.Library] Example.Large", output);
        Assert.DoesNotContain("Example.Small", output);

        string report = temp.FilePath("summary.txt");
        (exitCode, output, error) =
            await CliProcess.Run(input, "--top", "0", "--output", report);

        Assert.Equal(0, exitCode);
        Assert.Equal("", output);
        Assert.Equal("", error);
        Assert.DoesNotContain("=== Top", File.ReadAllText(report));
    }

    [Fact]
    public async Task TwoFileDiffReconcilesRetainedAssemblyAndBlobChanges()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string before = InputFixtureBuilder.Plain(
            temp,
            "before.mstat",
            RetainedFixture(methodSize: 300, blobSize: 50));
        string after = InputFixtureBuilder.Zip(
            temp,
            "after.zip",
            ("after.mstat", RetainedFixture(methodSize: 100, blobSize: 70)));

        (int exitCode, string output, string error) =
            await CliProcess.Run(before, after);

        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Assert.StartsWith(
            "Total accounted size difference: -180 B" + Environment.NewLine,
            output);
        Assert.Contains("Blobs: 50 B -> 70 B (+20 B)", output);
        Assert.Contains(
            "=== Net change by assembly (includes size changes of items present in both inputs) ===",
            output);
        Assert.Matches(@"-200 B\s+-200 B\s+Fixture\.Library", output);
        Assert.DoesNotContain("=== Removed / Shrunk ===", output);
    }

    [Theory]
    [InlineData(10, 25, "+15 B")]
    [InlineData(25, 10, "-15 B")]
    public async Task TwoFileDiffExplainsRetainedUnownedFrozenObjectChange(
        int beforeSize,
        int afterSize,
        string expectedDelta)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string before = InputFixtureBuilder.Plain(
            temp,
            "before.mstat",
            FrozenFixture(beforeSize));
        string after = InputFixtureBuilder.Plain(
            temp,
            "after.mstat",
            FrozenFixture(afterSize));

        (int exitCode, string output, string error) =
            await CliProcess.Run(before, after);

        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Assert.Contains(
            $"Unowned frozen objects: {beforeSize} B -> {afterSize} B ({expectedDelta})",
            output);
        Assert.Contains($"Retained frozen objects: {expectedDelta}", output);
        Assert.DoesNotContain("New frozen objects", output);
        Assert.DoesNotContain("Removed frozen objects", output);
    }

    [Fact]
    public async Task TwoFileDiffKeepsSignedInt64TotalsAndOutputCompatibility()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string before = InputFixtureBuilder.Plain(
            temp,
            "before.mstat",
            RetainedFixture(methodSize: 0, blobSize: 0));
        string after = InputFixtureBuilder.Plain(
            temp,
            "after.mstat",
            RetainedFixture(methodSize: 1_600_000_000, blobSize: 1_600_000_000));
        string report = temp.FilePath("diff.txt");

        (int exitCode, string output, string error) =
            await CliProcess.Run(before, after, "--output", report);

        Assert.Equal(0, exitCode);
        Assert.Equal("", output);
        Assert.Equal("", error);
        string written = File.ReadAllText(report);
        Assert.StartsWith(
            "Total accounted size difference: 3.0 GB" + Environment.NewLine,
            written);
        Assert.Contains("+1.5 GB", written);
        Assert.DoesNotContain("-1.0 GB", written);
    }

    [Fact]
    public async Task HelpIsConciseAndInvalidTopFails()
    {
        (int exitCode, string output, string error) =
            await CliProcess.Run("--help");

        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Assert.Contains("Usage: sizoscope-cli <file.mstat|.zip>", output);
        Assert.Contains("--top <n>", output);
        Assert.DoesNotContain("why", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("metadata", output, StringComparison.OrdinalIgnoreCase);

        (exitCode, output, error) =
            await CliProcess.Run("input.mstat", "--top", "-1");
        Assert.Equal(1, exitCode);
        Assert.Equal("", output);
        Assert.StartsWith("Invalid --top value: -1", error);
    }

    private static byte[] SummaryFixture()
    {
        var builder = new MstatPeBuilder();
        var small = builder.AddType("Small");
        var large = builder.AddType("Large");
        builder.Type(small, 20);
        builder.Type(large, 40);
        return builder.Build();
    }

    private static byte[] RetainedFixture(int methodSize, int blobSize)
    {
        var builder = new MstatPeBuilder();
        var owner = builder.AddType("Owner");
        builder.Method(builder.AddMethod("Run", owner), methodSize);
        builder.Blob("Metadata", blobSize);
        return builder.Build();
    }

    private static byte[] FrozenFixture(int size)
    {
        var builder = new MstatPeBuilder();
        var type = builder.AddType("Frozen");
        builder.Frozen(type, size, builder.AddName("retained"));
        return builder.Build();
    }
}
