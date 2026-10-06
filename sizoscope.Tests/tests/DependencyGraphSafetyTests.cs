using System.Text;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class DependencyGraphSafetyTests
{
    [Theory]
    [InlineData(127, false)]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public void Build_XmlDepthLimitHasExactBoundary(int depth, bool rejected)
    {
        string xml = "<DirectedGraph>" + string.Concat(Enumerable.Repeat("<Layer>", depth))
            + string.Concat(Enumerable.Repeat("</Layer>", depth)) + "</DirectedGraph>";
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var input = DgmlFixtureBuilder.Open(xml);
        string directory = temp.FilePath("index");

        if (rejected)
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                DependencyGraphIndex.Build(input, directory, sortBufferRecords: 1));
            Assert.Contains("128-level limit", error.Message);
            Assert.False(File.Exists(Path.Combine(directory, "counts.bin")));
        }
        else
        {
            DependencyGraphIndex.Build(input, directory, sortBufferRecords: 1);
            using var index = new DependencyGraphIndex(directory);
            Assert.Null(index.FindNode("absent"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_BoundsNameTableEntriesAndCharacters(bool oversizedName)
    {
        var xml = new StringBuilder("<DirectedGraph>");
        if (oversizedName)
            xml.Append('<').Append('n', 1_048_576).Append("/>");
        else
            for (int i = 0; i < 4097; i++)
                xml.Append("<Name").Append(i).Append("/>");
        xml.Append("</DirectedGraph>");
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var input = DgmlFixtureBuilder.Open(xml.ToString());
        string directory = temp.FilePath("index");

        var error = Assert.Throws<InvalidDataException>(() =>
            DependencyGraphIndex.Build(input, directory, sortBufferRecords: 1));

        Assert.Contains("name-table limit", error.Message);
        Assert.False(File.Exists(Path.Combine(directory, "counts.bin")));
    }

    [Fact]
    public void Build_RejectsOversizedXmlRecordBeforePublishing()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        using var input = DgmlFixtureBuilder.Open(
            "<DirectedGraph><Nodes><Node Id=\"1\" Label=\"" + new string('x', 8 * 1024 * 1024)
            + "\"/></Nodes></DirectedGraph>");
        string directory = temp.FilePath("index");

        var error = Assert.Throws<InvalidDataException>(() =>
            DependencyGraphIndex.Build(input, directory, sortBufferRecords: 1));

        Assert.Contains("XML element/text record", error.Message);
        Assert.False(File.Exists(Path.Combine(directory, "counts.bin")));
    }
}
