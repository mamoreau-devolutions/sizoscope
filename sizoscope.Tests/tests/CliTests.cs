using System.Text;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class CliTests
{
    private static byte[] Input(int code, int gc = 0, int unwind = 0)
    {
        var b = new MstatPeBuilder();
        b.Method(b.AddMethod("Run", b.AddType("Owner")), code, gc, unwind);
        return b.Build();
    }

    private static string Report(string total) =>
        "Total accounted size difference: " + total + Environment.NewLine + Environment.NewLine;

    [Fact]
    public async Task Cli_SizeOnlyPrintsExactOrdinaryTotalWithoutDgml()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string left = InputFixtureBuilder.Plain(temp, "left.mstat", new MstatPeBuilder().Build());
        string right = InputFixtureBuilder.Plain(temp, "right.mstat", Input(11, 13, 17));
        var result = await CliProcess.Run(left, right);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(Report("41 B") + "=== New / Grown ===" + Environment.NewLine
            + "  +41 B         Fixture.Library" + Environment.NewLine + Environment.NewLine, result.Output);
    }

    [Fact]
    public async Task Cli_OutputWritesExpectedReportInOwnedDirectory()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string left = InputFixtureBuilder.Plain(temp, "left.mstat", Input(11));
        string right = InputFixtureBuilder.Plain(temp, "right.mstat", Input(23));
        string output = temp.FilePath("report.txt");
        var result = await CliProcess.Run(left, right, "--output", output);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal("", result.Output);
        Assert.Equal(Encoding.UTF8.GetBytes(Report("12 B")), File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData(11, 23, "12 B", false)]
    [InlineData(23, 11, "-12 B", false)]
    [InlineData(11, 11, "0 B", false)]
    [InlineData(11, 23, "12 B", true)]
    public async Task Cli_DiffSizeOnlyChangeReportsSignedDeltaWithoutIdentityChanges(
        int before, int after, string expected, bool outputFile)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        // Compose plain/ZIP input resolution with the real process.
        string left = InputFixtureBuilder.Plain(temp, "before.mstat", Input(before));
        string right = InputFixtureBuilder.Zip(temp, "after.zip", ("inside/after.mstat", Input(after)));
        string path = temp.FilePath("delta.txt");
        var result = outputFile
            ? await CliProcess.Run(left, right, "--output", path)
            : await CliProcess.Run(left, right);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(outputFile ? "" : Report(expected), result.Output);
        if (outputFile) Assert.Equal(Report(expected), File.ReadAllText(path));
    }

    [Fact]
    public async Task Cli_SingleFileSummaryRanksAssembliesAndTypes()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string input = InputFixtureBuilder.Zip(temp, "one.zip", ("inside/one.mstat", Input(11, 13, 17)));
        var result = await CliProcess.Run(input, "--top", "5");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        string nl = Environment.NewLine;
        Assert.Equal("Total accounted size: 41 B" + nl + nl
            + "=== Assemblies ===" + nl
            + "  41 B          100.0% Fixture.Library" + nl + nl
            + "=== Top 1 types ===" + nl
            + "  41 B          100.0% [Fixture.Library] Example.Owner" + nl + nl, result.Output);

        string output = temp.FilePath("summary.txt");
        var toFile = await CliProcess.Run(input, "--top", "0", "--output", output);
        Assert.Equal(0, toFile.ExitCode);
        Assert.Equal("", toFile.Output);
        string written = File.ReadAllText(output);
        Assert.Contains("Fixture.Library", written);
        Assert.DoesNotContain("=== Top", written);
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("-h", 0)]
    [InlineData("--top", 1)]
    [InlineData("--bogus", 1)]
    public async Task Cli_HelpSucceedsAndBadOptionsFail(string option, int exitCode)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string input = InputFixtureBuilder.Plain(temp, "one.mstat", Input(11));
        var result = option == "--top"
            ? await CliProcess.Run(input, "--top", "-3")
            : await CliProcess.Run(option);
        Assert.Equal(exitCode, result.ExitCode);
        string usage = exitCode == 0 ? result.Output : result.Error;
        Assert.Contains("Usage: sizoscope-cli <file.mstat|.zip>", usage);
        if (exitCode == 0) Assert.Equal("", result.Error);
    }

    [Fact]
    public async Task Cli_LargeDeclaredTotalsRemainPositiveAndUntruncated()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string empty = InputFixtureBuilder.Plain(temp, "empty.mstat", new MstatPeBuilder().Build());
        string huge = InputFixtureBuilder.Plain(temp, "huge.mstat",
            Input(1_100_000_000, 1_100_000_000, 1_100_000_000));
        var forward = await CliProcess.Run(empty, huge);
        Assert.Equal(0, forward.ExitCode);
        Assert.Equal("", forward.Error);
        Assert.Equal(Report("3.1 GB") + "=== New / Grown ===" + Environment.NewLine
            + "  +3.1 GB       Fixture.Library" + Environment.NewLine + Environment.NewLine, forward.Output);
        var reverse = await CliProcess.Run(huge, empty);
        Assert.Equal(0, reverse.ExitCode);
        Assert.Equal("", reverse.Error);
        Assert.Equal(Report("-3.1 GB") + "=== Removed / Shrunk ===" + Environment.NewLine
            + "  -3.1 GB       Fixture.Library" + Environment.NewLine + Environment.NewLine, reverse.Output);
        // The same identity survives: the summary alone must still carry the >Int32 delta.
        string zero = InputFixtureBuilder.Plain(temp, "zero.mstat", Input(0));
        var retained = await CliProcess.Run(zero, huge);
        Assert.Equal(0, retained.ExitCode);
        Assert.Equal("", retained.Error);
        Assert.Equal(Report("3.1 GB"), retained.Output);
    }

    [Theory]
    [InlineData("valid", false)]
    [InlineData("malformed", false)]
    [InlineData("absent", false)]
    [InlineData("valid", true)]
    [InlineData("malformed", true)]
    [InlineData("absent", true)]
    public async Task Cli_SizeOnlyPlainCompanionsDoNotOpenGraph(string companion, bool outputFile)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string left = InputFixtureBuilder.Plain(temp, "empty.mstat", new MstatPeBuilder().Build());
        string right = InputFixtureBuilder.Plain(temp, "size.mstat", Input(11, 13, 17));
        using var leftLock = LockCompanion(temp, left, companion);
        using var rightLock = LockCompanion(temp, right, companion);
        string cache = temp.FilePath("cache");
        string output = temp.FilePath("size.txt");
        var result = outputFile
            ? await CliProcess.RunWithCache(cache, left, right, "--output", output)
            : await CliProcess.RunWithCache(cache, left, right);
        string expected = "Total accounted size difference: 41 B" + Environment.NewLine + Environment.NewLine
            + "=== New / Grown ===" + Environment.NewLine
            + "  +41 B         Fixture.Library" + Environment.NewLine + Environment.NewLine;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(outputFile ? "" : expected, result.Output);
        if (outputFile) Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(output));
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public async Task Cli_SizeOnlyZipMalformedCompanionDoesNotParseGraph()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] mstat = Input(11, 13, 17);
        string left = InputFixtureBuilder.Plain(temp, "empty.mstat", new MstatPeBuilder().Build());
        string right = InputFixtureBuilder.Zip(temp, "size.zip",
            ("empty.mstat", []), ("inside/size.mstat", mstat),
            ("inside/size.scan.dgml.xml", Encoding.UTF8.GetBytes("<DirectedGraph><Nodes>")),
            ("later.mstat", Input(999)));
        using (var resolved = sizoscope.ResolvedFile.Open(right))
        using (var stream = resolved.OpenMstat())
        {
            Assert.Equal(mstat.LongLength, resolved.MstatLength);
            Assert.Equal(mstat, InputFixtureBuilder.Bytes(stream));
            Assert.True(resolved.HasDgml);
            Assert.Equal("inside/size.scan.dgml.xml", resolved.GraphInput.EntryName);
        }
        string cache = temp.FilePath("cache");
        var result = await CliProcess.RunWithCache(cache, left, right);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal("Total accounted size difference: 41 B" + Environment.NewLine + Environment.NewLine
            + "=== New / Grown ===" + Environment.NewLine
            + "  +41 B         Fixture.Library" + Environment.NewLine + Environment.NewLine, result.Output);
        Assert.False(Directory.Exists(cache));
    }

    [Theory]
    [InlineData("valid", 11, 23, "12 B", false)]
    [InlineData("malformed", 11, 23, "12 B", false)]
    [InlineData("absent", 11, 23, "12 B", false)]
    [InlineData("valid", 23, 11, "-12 B", false)]
    [InlineData("malformed", 23, 11, "-12 B", false)]
    [InlineData("absent", 23, 11, "-12 B", false)]
    [InlineData("valid", 11, 11, "0 B", false)]
    [InlineData("malformed", 11, 11, "0 B", false)]
    [InlineData("absent", 11, 11, "0 B", false)]
    [InlineData("valid", 11, 23, "12 B", true)]
    [InlineData("malformed", 11, 23, "12 B", true)]
    [InlineData("absent", 11, 23, "12 B", true)]
    public async Task Cli_SizeOnlyDiffPreservesSignedAndIdentityOnlyReports(
        string companion, int before, int after, string expected, bool outputFile)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string left = InputFixtureBuilder.Plain(temp, "before.mstat", Input(before));
        string right = InputFixtureBuilder.Plain(temp, "after.mstat", Input(after));
        using var leftLock = LockCompanion(temp, left, companion);
        using var rightLock = LockCompanion(temp, right, companion);
        string cache = temp.FilePath("cache");
        string output = temp.FilePath("diff.txt");
        var result = outputFile
            ? await CliProcess.RunWithCache(cache, left, right, "--output", output)
            : await CliProcess.RunWithCache(cache, left, right);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(outputFile ? "" : Report(expected), result.Output);
        if (outputFile) Assert.Equal(Encoding.UTF8.GetBytes(Report(expected)), File.ReadAllBytes(output));
        Assert.False(Directory.Exists(cache));
    }

    private static FileStream LockCompanion(OwnedTempDirectory temp, string mstat, string companion)
    {
        string path = Path.ChangeExtension(mstat, "scan.dgml.xml");
        Assert.Equal(temp.Path, Path.GetDirectoryName(path));
        if (companion == "absent")
        {
            Assert.False(File.Exists(path));
            return null;
        }
        File.WriteAllBytes(path, companion == "valid" ? OwnedGraph.Bytes() : Encoding.UTF8.GetBytes("<DirectedGraph><Nodes>"));
        return File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
