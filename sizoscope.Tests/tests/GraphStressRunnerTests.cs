using System.Text.Json;
using Sizoscope.Tests.Fixtures;
using Sizoscope.Tests.Stress;
using Xunit;

namespace Sizoscope.Tests;

public sealed class GraphStressRunnerTests
{
    [Theory]
    [InlineData("opt-in", "")]
    [InlineData("--temp", null)]
    [InlineData("--output", null)]
    [InlineData("--nodes", null)]
    [InlineData("--edges", null)]
    [InlineData("--seed", null)]
    [InlineData("--temp", "relative")]
    [InlineData("--output", "relative")]
    [InlineData("--nodes", "0")]
    [InlineData("--nodes", "126322569")]
    [InlineData("--edges", "-1")]
    [InlineData("--seed", "-1")]
    [InlineData("--sort-buffer", "0")]
    [InlineData("--sort-buffer", "1048577")]
    public void Stress_RequiresExplicitOptInAbsolutePathsAndValidBoundsBeforeCreatingFiles(string key, string value)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var args = new List<string> { "--stress", "--temp", temp.FilePath("work"), "--output", temp.FilePath("output"),
            "--nodes", "17", "--edges", "48", "--seed", "7" };
        if (key == "opt-in") args.Remove("--stress");
        else
        {
            int position = args.IndexOf(key);
            if (position >= 0)
            {
                args.RemoveRange(position, 2);
                if (value != null) args.InsertRange(position, new[] { key, value });
            }
            else args.AddRange(new[] { key, value });
        }
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, StressProgram.Execute(args.ToArray(), output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("Required: --stress", error.ToString());
        Assert.Equal(new[] { temp.FilePath(".test-owner") }, Directory.GetFiles(temp.Path));
        Assert.Empty(Directory.GetDirectories(temp.Path));
    }

    [Fact]
    public void Stress_SmallExplicitRunStreamsValidatesSentinelsAndCleansOnlyOwnedSubdirectories()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string work = temp.FilePath("work"), reports = temp.FilePath("reports");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(reports);
        string sentinel = Path.Combine(work, "keep.txt");
        File.WriteAllText(sentinel, "not owned by runner");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, StressProgram.Execute(new[] { "--stress", "--temp", work, "--output", reports,
            "--nodes", "17", "--edges", "153", "--seed", "7", "--sort-buffer", "2" }, output, error));
        Assert.Equal("", error.ToString());
        using var report = JsonDocument.Parse(output.ToString().Split(Environment.NewLine)[0]);
        Assert.Equal(17, report.RootElement.GetProperty("Nodes").GetInt32());
        Assert.Equal(153, report.RootElement.GetProperty("Edges").GetInt64());
        // Sentinel target 0 owns blocks 10 and target 16 owns block 9: eight edges each.
        Assert.Equal(16, report.RootElement.GetProperty("SentinelEdgesVerified").GetInt64());
        Assert.True(report.RootElement.GetProperty("LogicalBytes").GetInt64() > 0);
        Assert.Equal("not owned by runner", File.ReadAllText(sentinel));
        Assert.Equal(new[] { sentinel }, Directory.GetFiles(work));
        Assert.Empty(Directory.GetDirectories(work));
        Assert.Empty(Directory.GetFileSystemEntries(reports));
    }
}
