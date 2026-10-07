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

    // Size-only change of an item present in both inputs: reconciled in the net-change section.
    private static string SizeOnlyReport(string total) => total == "0 B" ? Report(total) : Report(total)
        + "=== Net change by assembly (includes size changes of items present in both inputs) ===" + Environment.NewLine
        + $"  {"Net",-12} {"Retained items",-14} Assembly" + Environment.NewLine
        + $"  {(total.StartsWith('-') ? total : "+" + total),-12} {(total.StartsWith('-') ? total : "+" + total),-14} Fixture.Library" + Environment.NewLine
        + Environment.NewLine;

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
        Assert.Equal(Encoding.UTF8.GetBytes(SizeOnlyReport("12 B")), File.ReadAllBytes(output));
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
        Assert.Equal(outputFile ? "" : SizeOnlyReport(expected), result.Output);
        if (outputFile) Assert.Equal(SizeOnlyReport(expected), File.ReadAllText(path));
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
    public async Task Cli_SatelliteAssembliesShowCultureAndDiffSeparately()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] Satellites(int fr, int de)
        {
            var b = new MstatPeBuilder();
            b.Method(b.AddMethod("Get", b.AddType("Res", "R", b.AddAssembly("App.resources", "fr"))), fr);
            if (de > 0)
                b.Method(b.AddMethod("Get", b.AddType("Res", "R", b.AddAssembly("App.resources", "de"))), de);
            return b.Build();
        }
        string before = InputFixtureBuilder.Plain(temp, "before.mstat", Satellites(100, 200));
        string after = InputFixtureBuilder.Plain(temp, "after.mstat", Satellites(100, 0));

        var summary = await CliProcess.Run(before);
        Assert.Equal(0, summary.ExitCode);
        Assert.Contains("App.resources [fr]", summary.Output);
        Assert.Contains("App.resources [de]", summary.Output);

        var diff = await CliProcess.Run(before, after);
        Assert.Equal(0, diff.ExitCode);
        Assert.Equal("", diff.Error);
        Assert.Equal(Report("-200 B") + "=== Removed / Shrunk ===" + Environment.NewLine
            + "  -200 B        App.resources [de]" + Environment.NewLine + Environment.NewLine, diff.Output);
    }

    [Fact]
    public async Task Cli_ReportsMethodBodyFoldingGroupedByKeptBody()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] Build(bool fold)
        {
            var b = new MstatPeBuilder("2.2.0.0");
            var lib = b.AddType("Lib");
            var big = b.AddMethod("Big", lib);
            var small = b.AddMethod("Small", lib);
            b.Method(big, 100);
            b.Method(small, 10);
            if (fold)
            {
                b.Folded(b.AddMethod("BigAliasA", lib), big);
                b.Folded(b.AddMethod("BigAliasB", lib), big);
                b.Folded(b.AddMethod("SmallAlias", lib), small);
            }
            return b.Build();
        }
        string plain = InputFixtureBuilder.Plain(temp, "plain.mstat", Build(fold: false));
        string folded = InputFixtureBuilder.Plain(temp, "folded.mstat", Build(fold: true));

        byte[] foldedBytes = Build(fold: true);
        using (var data = MstatData.Read(new MemoryStream(foldedBytes), foldedBytes.Length))
        {
            Assert.Equal(3, data.DeduplicatedAliasCount);
            Assert.Equal(2, data.DeduplicatedBodyCount);
            Assert.Equal(210, data.DeduplicatedSavedSize);
            Assert.Equal(110, data.Size);
            var bigBody = data.GetDeduplicatedBodies().Single(x => x.Name.Contains(".Big("));
            Assert.Equal(2, bigBody.AliasCount);
            Assert.Equal(200, bigBody.SavedSize);
            Assert.Equal(2, bigBody.GetAliasNames().Count(n => n.Contains("BigAlias")));
        }

        var summary = await CliProcess.Run(folded);
        Assert.Equal(0, summary.ExitCode);
        Assert.Equal("", summary.Error);
        Assert.Contains("3 methods folded into 2 kept bodies, estimated 210 B saved", summary.Output);
        int bigLine = summary.Output.IndexOf("Example.Lib.Big(", StringComparison.Ordinal);
        int smallLine = summary.Output.IndexOf("Example.Lib.Small(", StringComparison.Ordinal);
        Assert.True(bigLine > 0 && smallLine > bigLine, summary.Output);

        var plainSummary = await CliProcess.Run(plain);
        Assert.DoesNotContain("folding", plainSummary.Output);

        var diff = await CliProcess.Run(plain, folded);
        Assert.Equal(0, diff.ExitCode);
        Assert.Equal("", diff.Error);
        Assert.StartsWith(Report("0 B"), diff.Output);
        Assert.Contains("Folded methods: 0 -> 3", diff.Output);
        Assert.Contains("Estimated folding savings: 0 B -> 210 B", diff.Output);
    }

    [Fact]
    public async Task Cli_DiffReportsSizeChangesOfItemsPresentInBothInputs()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] Build(int code, int blob)
        {
            var b = new MstatPeBuilder();
            var lib = b.AddType("Lib");
            b.Method(b.AddMethod("Run", lib), code);
            b.Blob("Metadata", blob);
            return b.Build();
        }
        string before = InputFixtureBuilder.Plain(temp, "before.mstat", Build(300, 50));
        string after = InputFixtureBuilder.Plain(temp, "after.mstat", Build(100, 70));

        var diff = await CliProcess.Run(before, after);
        Assert.Equal(0, diff.ExitCode);
        Assert.Equal("", diff.Error);
        Assert.Contains("Blobs: 50 B -> 70 B (+20 B)", diff.Output);
        Assert.Contains("=== Net change by assembly", diff.Output);
        Assert.Matches(@"-200 B\s+-200 B\s+Fixture", diff.Output);
        Assert.DoesNotContain("=== Removed / Shrunk ===", diff.Output);
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
        Assert.Equal(SizeOnlyReport("3.1 GB"), retained.Output);
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
        Assert.Equal(outputFile ? "" : SizeOnlyReport(expected), result.Output);
        if (outputFile) Assert.Equal(Encoding.UTF8.GetBytes(SizeOnlyReport(expected)), File.ReadAllBytes(output));
        Assert.False(Directory.Exists(cache));
    }

    private const string WhyGraph = """
        <DirectedGraph xmlns="http://schemas.microsoft.com/vs/2009/dgml">
          <Nodes>
            <Node Id="1" Label="Root: Main" />
            <Node Id="2" Label="Method: Startup" />
            <Node Id="3" Label="Reflectable type: Big.Generated.Context" />
            <Node Id="4" Label="Cycle: A" />
            <Node Id="5" Label="Cycle: B" />
            <Node Id="6" Label="Reflectable type: Big.Other" />
          </Nodes>
          <Links>
            <Link Source="1" Target="2" Reason="call" />
            <Link Source="4" Target="3" Reason="loop" />
            <Link Source="2" Target="3" Reason="typeof" />
            <Link Source="5" Target="4" />
            <Link Source="4" Target="5" />
            <Link Source="1" Target="6" Reason="reflection" />
          </Links>
        </DirectedGraph>
        """;

    [Fact]
    public async Task Cli_WhyPrintsDependentsAndShortestChainFromRoot()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string graph = temp.FilePath("app.scan.dgml.xml");
        File.WriteAllText(graph, WhyGraph);
        string cache = temp.FilePath("cache");
        var result = await CliProcess.RunWithCache(cache, "why", graph, "generated.context");
        Assert.Equal(0, result.ExitCode);
        string nl = Environment.NewLine;
        Assert.Equal("Node #3: Reflectable type: Big.Generated.Context" + nl + nl
            + "Direct dependents:" + nl
            + "  #4 Cycle: A  [loop]" + nl
            + "  #2 Method: Startup  [typeof]" + nl + nl
            + "Shortest chain from a root (2 steps, 5 nodes visited):" + nl
            + "  #1 Root: Main" + nl
            + "    -> #2 Method: Startup  [call]" + nl
            + "      -> #3 Reflectable type: Big.Generated.Context  [typeof]" + nl, result.Output);
        Assert.Contains("Index ready: 6 nodes", result.Error);

        // The second query reuses the cached index and resolves IDs directly.
        var byId = await CliProcess.RunWithCache(cache, "why", graph, "#1", "--incoming", "0");
        Assert.Equal(0, byId.ExitCode);
        Assert.Equal("Node #1: Root: Main" + nl + nl
            + "Shortest chain from a root (0 steps, 1 nodes visited):" + nl
            + "  #1 Root: Main" + nl, byId.Output);
        Assert.DoesNotContain("Reading XML", byId.Error);
    }

    [Fact]
    public async Task Cli_WhyListsAmbiguousMatchesAndRejectsMissingNodes()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string graph = temp.FilePath("app.scan.dgml.xml");
        File.WriteAllText(graph, WhyGraph);
        string cache = temp.FilePath("cache");
        string nl = Environment.NewLine;

        var many = await CliProcess.RunWithCache(cache, "why", graph, "REFLECTABLE");
        Assert.Equal(0, many.ExitCode);
        Assert.Equal("2 nodes match 'REFLECTABLE':" + nl
            + "  #3 Reflectable type: Big.Generated.Context" + nl
            + "  #6 Reflectable type: Big.Other" + nl + nl
            + "Refine the query, or pass #<id> to explain one node." + nl, many.Output);

        var capped = await CliProcess.RunWithCache(cache, "why", graph, "o", "--limit", "1");
        Assert.StartsWith("More than 1 nodes match 'o'. First 1:" + nl + "  #1 Root: Main" + nl, capped.Output);

        var exactMiss = await CliProcess.RunWithCache(cache, "why", graph, "Big.Other", "--exact");
        Assert.Equal(1, exactMiss.ExitCode);
        Assert.Equal("No node matches 'Big.Other'." + nl, exactMiss.Output);

        var badId = await CliProcess.RunWithCache(cache, "why", graph, "#99");
        Assert.Equal(1, badId.ExitCode);
        Assert.Equal("No node with ID 99." + nl, badId.Output);

        var cycleOnly = await CliProcess.RunWithCache(cache, "why", graph, "#5", "--incoming", "0");
        Assert.Equal(0, cycleOnly.ExitCode);
        Assert.Equal("Node #5: Cycle: B" + nl + nl + "No root reached within 2 visited nodes (raise --max-visit)." + nl, cycleOnly.Output);

        var badOption = await CliProcess.RunWithCache(cache, "why", graph, "x", "--limit", "0");
        Assert.Equal(1, badOption.ExitCode);
        Assert.StartsWith("Invalid --limit value: 0", badOption.Error);
    }

    private const string MetadataLog = """
        Handle, Kind, Name, Children
        01000000, ScopeDefinition, "App", "2000010"
        02000010, NamespaceDefinition, "App.Ns", "3000020"
        03000020, TypeDefinition, "App.Ns.Big+<>c (03000020)", "4000030 b000040 7000010"
        04000030, ConstantStringValue, "first line
        second, "quoted" line", ""
        0b000040, ConstantInt32Value, "42", ""
        05000080, ConstantStringValue, "OrphanMethodName", ""
        06f00000, MethodSignature, "sig", ""
        07000010, ConstantStringValue, "after wrap", ""
        """;

    [Fact]
    public async Task Cli_MetadataAttributesRecordsAndUnwrapsOffsets()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string log = temp.FilePath("app.metadata.csv");
        File.WriteAllText(log, MetadataLog);
        string nl = Environment.NewLine;

        var result = await CliProcess.RunWithCache(temp.FilePath("cache"), "metadata", log, "--top", "5");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        // 0x07000010 follows 0x06f00000, so its 24-bit offset wrapped: 0x1000010.
        Assert.StartsWith("Metadata blob: 16,777,232 bytes in 8 records (end of last record not logged)" + nl
            + "NativeAOT metadata limit: 33,554,431 bytes -- 16,777,199 bytes of headroom" + nl, result.Output);
        Assert.Contains("=== By record kind ===" + nl
            + "     15,728,528  93.7%           3  ConstantStringValue" + nl
            + "      1,048,592   6.3%           1  MethodSignature" + nl
            + "             64   0.0%           1  ConstantInt32Value" + nl, result.Output);
        Assert.Contains("=== By assembly ===" + nl + "            128   0.0%  App" + nl
            + "     16,777,104 100.0%  (not owned by any type definition)" + nl, result.Output);
        Assert.Contains("=== By namespace ===" + nl + "            112   0.0%  [App] App.Ns" + nl
            + "             16   0.0%  [App] <global>" + nl, result.Output);
        Assert.Contains("=== By type ===" + nl + "             96   0.0%  [App] App.Ns.Big+<>c" + nl + nl, result.Output);
        Assert.Contains("=== Largest strings ===" + nl
            + "     15,728,512  93.7%  (unowned): \"OrphanMethodName\"" + nl
            + "             16   0.0%  [App] App.Ns.Big+<>c: \"first line second, \"quoted\" line\"" + nl, result.Output);
        Assert.EndsWith("=== Not owned by any type definition ===" + nl
            + "  Typically method names/signatures kept only for stack traces (StackTraceSupport)." + nl
            + "     15,728,512  93.7%           1  ConstantStringValue" + nl
            + "      1,048,592   6.3%           1  MethodSignature" + nl, result.Output);

        var filtered = await CliProcess.RunWithCache(temp.FilePath("cache"), "metadata", log, "--assembly", "Other");
        Assert.Equal(0, filtered.ExitCode);
        Assert.Contains("Filtered to assembly: Other" + nl + nl + "=== By namespace ===" + nl + nl + "=== By type ===" + nl + nl
            + "=== Largest strings ===" + nl + nl, filtered.Output);
    }

    [Fact]
    public async Task Cli_MetadataRejectsInvalidInput()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string bogus = temp.FilePath("bogus.csv");
        File.WriteAllText(bogus, "not,a,metadata,log\n");

        var invalid = await CliProcess.RunWithCache(temp.FilePath("cache"), "metadata", bogus);
        Assert.Equal(1, invalid.ExitCode);
        Assert.StartsWith("Not a valid ILC metadata log: missing 'Handle, Kind, Name, Children' header.", invalid.Error);

        var missing = await CliProcess.RunWithCache(temp.FilePath("cache"), "metadata", temp.FilePath("none.csv"));
        Assert.Equal(1, missing.ExitCode);
        Assert.StartsWith("File not found:", missing.Error);

        var badTop = await CliProcess.RunWithCache(temp.FilePath("cache"), "metadata", bogus, "--top", "0");
        Assert.Equal(1, badTop.ExitCode);
        Assert.StartsWith("Invalid --top value: 0", badTop.Error);

        var help = await CliProcess.RunWithCache(temp.FilePath("cache"), "metadata", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.StartsWith("Usage: sizoscope-cli metadata <app.metadata.csv> [options]", help.Output);
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
