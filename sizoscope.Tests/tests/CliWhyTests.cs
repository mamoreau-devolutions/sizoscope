using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class CliWhyTests
{
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
        Assert.Equal("Node #5: Cycle: B" + nl + nl
            + "No root reached within 2 visited nodes (raise --max-visit)." + nl, cycleOnly.Output);

        var badOption = await CliProcess.RunWithCache(cache, "why", graph, "x", "--limit", "0");
        Assert.Equal(1, badOption.ExitCode);
        Assert.StartsWith("Invalid --limit value: 0", badOption.Error);
    }

    [Fact]
    public async Task Cli_WhyMalformedDgmlReturnsConciseFailureWithoutStackTrace()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string graph = temp.FilePath("malformed.dgml.xml");
        File.WriteAllText(graph, "<DirectedGraph><Nodes><Node Id=\"1\"");

        var result = await CliProcess.RunWithCache(temp.FilePath("cache"), "why", graph, "node");

        AssertCliFailureIsConcise(result, "DGML");
    }

    [Fact]
    public async Task Cli_WhyEmptyCachePathReturnsConciseFailureWithoutStackTrace()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string graph = temp.FilePath("app.dgml.xml");
        File.WriteAllText(graph, WhyGraph);

        var result = await CliProcess.RunWithCache(temp.FilePath("fallback-cache"),
            "why", graph, "root", "--cache", "");

        AssertCliFailureIsConcise(result, "cache");
    }

    [Fact]
    public async Task Cli_WhyCachePathThatIsAFileReturnsConciseFailureWithoutStackTrace()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string graph = temp.FilePath("app.dgml.xml");
        File.WriteAllText(graph, WhyGraph);
        string cacheFile = temp.FilePath("not-a-directory");
        File.WriteAllText(cacheFile, "occupied");

        var result = await CliProcess.RunWithCache(temp.FilePath("fallback-cache"),
            "why", graph, "root", "--cache", cacheFile);

        AssertCliFailureIsConcise(result, cacheFile);
    }

    private static void AssertCliFailureIsConcise(
        (int ExitCode, string Output, string Error) result,
        string expectedDiagnostic)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains(expectedDiagnostic, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Unhandled exception",
            result.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", result.Error, StringComparison.Ordinal);
        Assert.InRange(result.Error.Length, 1, 500);
    }
}
