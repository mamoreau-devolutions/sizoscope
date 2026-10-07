using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class CliMetadataTests
{
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
    public async Task MetadataAttributesRecordsUnwrapsOffsetsAndReportsUnknownHeadroomBelowLimit()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string log = temp.FilePath("app.metadata.csv");
        File.WriteAllText(log, MetadataLog);
        string nl = Environment.NewLine;

        var result = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", log, "--top", "5");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.StartsWith(
            "Metadata blob: at least 16,777,232 bytes in 8 records (final record length unknown)" + nl
            + "NativeAOT metadata limit: 33,554,431 bytes -- headroom unknown" + nl,
            result.Output);
        Assert.DoesNotContain("bytes of headroom", result.Output);
        Assert.Contains("=== By record kind ===" + nl
            + "     15,728,528  93.7%           3  ConstantStringValue" + nl
            + "      1,048,592   6.3%           1  MethodSignature" + nl
            + "             64   0.0%           1  ConstantInt32Value" + nl,
            result.Output);
        Assert.Contains("=== By assembly ===" + nl
            + "            128   0.0%  App" + nl
            + "     16,777,104 100.0%  (not owned by any type definition)" + nl,
            result.Output);
        Assert.Contains("=== By namespace ===" + nl
            + "            112   0.0%  [App] App.Ns" + nl
            + "             16   0.0%  [App] <global>" + nl,
            result.Output);
        Assert.Contains("=== By type ===" + nl
            + "             96   0.0%  [App] App.Ns.Big+<>c" + nl + nl,
            result.Output);
        Assert.Contains("=== Largest strings ===" + nl
            + "     15,728,512  93.7%  (unowned): \"OrphanMethodName\"" + nl
            + "             16   0.0%  [App] App.Ns.Big+<>c: \"first line second, \"quoted\" line\"" + nl,
            result.Output);
        Assert.EndsWith("=== Not owned by any type definition ===" + nl
            + "  Typically method names/signatures kept only for stack traces (StackTraceSupport)." + nl
            + "     15,728,512  93.7%           1  ConstantStringValue" + nl
            + "      1,048,592   6.3%           1  MethodSignature" + nl,
            result.Output);

        var filtered = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", log, "--assembly", "Other");
        Assert.Equal(0, filtered.ExitCode);
        Assert.Contains("Filtered to assembly: Other" + nl + nl
            + "=== By namespace ===" + nl + nl
            + "=== By type ===" + nl + nl
            + "=== Largest strings ===" + nl + nl,
            filtered.Output);
    }

    [Fact]
    public async Task MetadataDuplicateHandlesResolveToNearestOwnerAcrossOffsetWrap()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string log = temp.FilePath("duplicates.metadata.csv");
        File.WriteAllText(log, """
            Handle, Kind, Name, Children
            01000000, ScopeDefinition, "First", "3000020"
            03000020, TypeDefinition, "First.Type (03000020)", "4000030"
            04000030, ConstantStringValue, "first", ""
            06f00000, MethodSignature, "padding", ""
            01000000, ScopeDefinition, "Second", "3000020"
            03000020, TypeDefinition, "Second.Type (03000020)", "4000030"
            04000030, ConstantStringValue, "second", ""
            """);

        var result = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", log, "--top", "10");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Contains("[First] First.Type: \"first\"", result.Output);
        Assert.Contains("[Second] Second.Type: \"second\"", result.Output);
    }

    [Fact]
    public async Task MetadataFinalOffsetAtLimitKeepsHeadroomUnknown()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string log = temp.FilePath("at-limit.metadata.csv");
        File.WriteAllText(log, """
            Handle, Kind, Name, Children
            01f00000, ConstantInt32Value, "1", ""
            02000010, ConstantInt32Value, "2", ""
            03ffffff, ConstantInt32Value, "3", ""
            """);

        var result = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", log);

        string nl = Environment.NewLine;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.StartsWith(
            "Metadata blob: at least 33,554,431 bytes in 3 records (final record length unknown)" + nl
            + "NativeAOT metadata limit: 33,554,431 bytes -- headroom unknown" + nl,
            result.Output);
        Assert.DoesNotContain("OVER", result.Output);
        Assert.DoesNotContain("bytes of headroom", result.Output);
    }

    [Fact]
    public async Task MetadataFinalOffsetOverLimitReportsDefinitiveLowerBound()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string log = temp.FilePath("over.metadata.csv");
        File.WriteAllText(log, """
            Handle, Kind, Name, Children
            01f00000, ConstantInt32Value, "1", ""
            02000010, ConstantInt32Value, "2", ""
            03f00000, ConstantInt32Value, "3", ""
            04000020, ConstantInt32Value, "4", ""
            """);

        var result = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", log);

        string nl = Environment.NewLine;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.StartsWith(
            "Metadata blob: at least 33,554,464 bytes in 4 records (final record length unknown)" + nl
            + "NativeAOT metadata limit: 33,554,431 bytes -- OVER by at least 33" + nl,
            result.Output);
        Assert.DoesNotContain("headroom unknown", result.Output);
    }

    [Fact]
    public async Task MetadataRejectsMalformedInputAndInvalidOptions()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string bogus = temp.FilePath("bogus.csv");
        File.WriteAllText(bogus, "not,a,metadata,log\n");

        var invalid = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", bogus);
        Assert.Equal(1, invalid.ExitCode);
        Assert.StartsWith(
            "Not a valid ILC metadata log: missing 'Handle, Kind, Name, Children' header.",
            invalid.Error);

        string malformed = temp.FilePath("malformed.csv");
        File.WriteAllText(malformed, """
            Handle, Kind, Name, Children
            malformed record
            """);
        var malformedResult = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", malformed);
        Assert.Equal(1, malformedResult.ExitCode);
        Assert.StartsWith(
            "Not a valid ILC metadata log: first record does not start with '<handle>, <kind>, '.",
            malformedResult.Error);

        var missing = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", temp.FilePath("none.csv"));
        Assert.Equal(1, missing.ExitCode);
        Assert.StartsWith("File not found:", missing.Error);

        var badTop = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", bogus, "--top", "0");
        Assert.Equal(1, badTop.ExitCode);
        Assert.StartsWith("Invalid --top value: 0", badTop.Error);

        var help = await CliProcess.RunWithCache(
            temp.FilePath("cache"), "metadata", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.StartsWith(
            "Usage: sizoscope-cli metadata <app.metadata.csv> [options]",
            help.Output);
    }
}
