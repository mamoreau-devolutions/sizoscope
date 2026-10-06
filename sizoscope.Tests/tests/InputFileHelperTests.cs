using System.Text;
using sizoscope;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class InputFileHelperTests
{
    [Fact]
    public void PlainInput_ReturnsOriginalPathLengthAndExactMstatBytes()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] bytes = { 1, 8, 3, 0, 255 };
        string path = InputFixtureBuilder.Plain(temp, "ordinary.mstat", bytes);
        using var resolved = ResolvedFile.Open(path);
        using var stream = resolved.OpenMstat();
        Assert.Equal(path, resolved.OriginalPath);
        Assert.Equal(5L, resolved.MstatLength);
        Assert.Equal(bytes, InputFixtureBuilder.Bytes(stream));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    public void PlainCompanion_PresentAbsentAndEmptyHaveDocumentedSemantics(int length)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = InputFixtureBuilder.Plain(temp, "app.mstat", new byte[] { 3 });
        byte[] bytes = length < 0 ? Array.Empty<byte>() : new byte[] { 1, 2, 3 }.Take(length).ToArray();
        if (length >= 0) File.WriteAllBytes(Path.ChangeExtension(path, "scan.dgml.xml"), bytes);
        using var resolved = ResolvedFile.Open(path);
        using var graph = resolved.OpenDgml();
        Assert.Equal(length >= 0, resolved.HasDgml);
        if (length < 0) Assert.Null(graph);
        else Assert.Equal(bytes, InputFixtureBuilder.Bytes(graph));
    }

    [Fact]
    public void ZipInput_SelectsFirstNonemptyMstatAndMatchingCompanion()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] expected = { 7, 0, 9 };
        byte[] graph = Encoding.UTF8.GetBytes("<DirectedGraph />");
        string path = InputFixtureBuilder.Zip(temp, "input.ZIP",
            ("notes.txt", new byte[] { 0 }),
            ("empty.mstat", Array.Empty<byte>()),
            ("chosen/app.MSTAT", expected),
            ("later/app.mstat", new byte[] { 8 }),
            ("later/app.scan.dgml.xml", new byte[] { 4 }),
            ("chosen/app.scan.dgml.xml", Array.Empty<byte>()),
            ("CHOSEN/APP.SCAN.DGML.XML", graph),
            ("chosen/app.scan.dgml.xml", new byte[] { 5 }));
        using var resolved = ResolvedFile.Open(path);
        using var mstat = resolved.OpenMstat();
        using var dgml = resolved.OpenDgml();
        Assert.Equal(path, resolved.OriginalPath);
        Assert.Equal(3L, resolved.MstatLength);
        Assert.Equal(expected, InputFixtureBuilder.Bytes(mstat));
        Assert.True(resolved.HasDgml);
        Assert.Equal(graph, InputFixtureBuilder.Bytes(dgml));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZipInput_MissingOrEmptyCompanionReturnsNoDgml(bool emptyCompanion)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        var entries = new List<(string, byte[])> { ("app.mstat", new byte[] { 3 }) };
        if (emptyCompanion) entries.Add(("app.scan.dgml.xml", Array.Empty<byte>()));
        string path = InputFixtureBuilder.Zip(temp, "app.zip", entries.ToArray());
        using var resolved = ResolvedFile.Open(path);
        Assert.False(resolved.HasDgml);
        Assert.Null(resolved.OpenDgml());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("empty")]
    public void InputErrors_UseKnownExceptions(string kind)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        if (kind == "missing")
            Assert.Throws<FileNotFoundException>(() => ResolvedFile.Open(temp.FilePath("absent.mstat")));
        else if (kind == "corrupt")
        {
            string path = InputFixtureBuilder.Plain(temp, "corrupt.zip", new byte[] { 1, 2, 3, 4 });
            Assert.Throws<InvalidDataException>(() => ResolvedFile.Open(path));
        }
        else
        {
            string path = InputFixtureBuilder.Zip(temp, "empty.zip",
                ("notes.txt", new byte[] { 3 }), ("empty.mstat", Array.Empty<byte>()));
            Assert.Throws<InvalidOperationException>(() => ResolvedFile.Open(path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispose_ReleasesPlainAndArchiveHandlesForExactOwnedCleanup(bool zip)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = zip
            ? InputFixtureBuilder.Zip(temp, "app.zip", ("app.mstat", new byte[] { 1 }),
                ("app.scan.dgml.xml", new byte[] { 2 }))
            : InputFixtureBuilder.Plain(temp, "app.mstat", new byte[] { 1 });
        if (!zip) File.WriteAllBytes(Path.ChangeExtension(path, "scan.dgml.xml"), new byte[] { 2 });
        using (var resolved = ResolvedFile.Open(path))
        {
            using var mstat = resolved.OpenMstat();
            using var graph = resolved.OpenDgml();
            Assert.Equal(1, mstat.ReadByte());
            Assert.Equal(2, graph.ReadByte());
        }
        File.Delete(path);
        Assert.False(File.Exists(path));
        if (!zip)
        {
            string graph = Path.ChangeExtension(path, "scan.dgml.xml");
            File.Delete(graph);
            Assert.False(File.Exists(graph));
        }
    }

    [Fact]
    public void ResolvedFile_GraphInputMatchesPlainCompanion()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] mstatBytes = { 1, 0, 8 };
        byte[] graphBytes = Encoding.UTF8.GetBytes("<DirectedGraph><Nodes /></DirectedGraph>");
        string path = InputFixtureBuilder.Plain(temp, "app.mstat", mstatBytes);
        string graphPath = Path.ChangeExtension(path, "scan.dgml.xml");
        File.WriteAllBytes(graphPath, graphBytes);
        DependencyGraphInput input;
        using (var resolved = ResolvedFile.Open(path))
        {
            Assert.Equal(path, resolved.OriginalPath);
            Assert.Equal(3L, resolved.MstatLength);
            using (var mstat = resolved.OpenMstat())
                Assert.Equal(mstatBytes, InputFixtureBuilder.Bytes(mstat));
            Assert.True(resolved.HasDgml);
            input = Assert.IsType<DependencyGraphInput>(resolved.GraphInput);
            Assert.Equal(graphPath, input.Path);
            Assert.Null(input.EntryName);
            Assert.Equal(graphBytes.LongLength, input.Length);
            var independent = new DependencyGraphInput(graphPath);
            Assert.Equal(independent.Identity, input.Identity);
            Assert.Equal(independent.CacheKey, input.CacheKey);
        }
        input.VerifyUnchanged();
        using (var reopened = input.Open())
            Assert.Equal(graphBytes, InputFixtureBuilder.Bytes(reopened));
        using (var exclusive = File.Open(graphPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(graphBytes.LongLength, exclusive.Length);
        File.Delete(graphPath);
        Assert.False(File.Exists(graphPath));
    }

    [Fact]
    public void ResolvedFile_GraphInputReopensFullZipEntryAfterResolverDisposal()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] mstatBytes = { 7, 0, 9 };
        byte[] graphBytes = Encoding.UTF8.GetBytes(
            "<DirectedGraph><Nodes><Node Id=\"73\" Label=\"Nœud.二\"/></Nodes></DirectedGraph>");
        const string fullGraphName = "NESTED/APP.SCAN.DGML.XML";
        string path = InputFixtureBuilder.Zip(temp, "nested.ZIP",
            ("empty.mstat", Array.Empty<byte>()),
            ("nested/app.MSTAT", mstatBytes),
            ("decoy/app.scan.dgml.xml", new byte[] { 99 }),
            ("nested/app.scan.dgml.xml", Array.Empty<byte>()),
            (fullGraphName, graphBytes),
            ("nested/app.scan.dgml.xml", new byte[] { 88 }),
            ("later/app.mstat", new byte[] { 77 }));
        DependencyGraphInput input;
        using (var resolved = ResolvedFile.Open(path))
        {
            Assert.Equal(path, resolved.OriginalPath);
            Assert.Equal(3L, resolved.MstatLength);
            using (var mstat = resolved.OpenMstat())
                Assert.Equal(mstatBytes, InputFixtureBuilder.Bytes(mstat));
            using (var graph = resolved.OpenDgml())
                Assert.Equal(graphBytes, InputFixtureBuilder.Bytes(graph));
            Assert.True(resolved.HasDgml);
            input = Assert.IsType<DependencyGraphInput>(resolved.GraphInput);
            Assert.Equal(path, input.Path);
            Assert.Equal(fullGraphName, input.EntryName);
            Assert.Equal(graphBytes.LongLength, input.Length);
            var independent = new DependencyGraphInput(path, fullGraphName, graphBytes.LongLength);
            Assert.Equal(independent.Identity, input.Identity);
            Assert.Equal(independent.CacheKey, input.CacheKey);
        }
        input.VerifyUnchanged();
        using (var reopened = input.Open())
            Assert.Equal(graphBytes, InputFixtureBuilder.Bytes(reopened));
        using (var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(new FileInfo(path).Length, exclusive.Length);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResolvedFile_EmptyCompanionPlainPresentZipAbsent(bool zip, bool companionExists)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] mstatBytes = { 3, 0, 5 };
        string path;
        if (zip)
        {
            var entries = new List<(string Name, byte[] Bytes)>
            {
                ("empty.mstat", Array.Empty<byte>()),
                ("chosen/app.mstat", mstatBytes),
                ("later/app.mstat", new byte[] { 99 })
            };
            if (companionExists) entries.Add(("chosen/app.scan.dgml.xml", Array.Empty<byte>()));
            path = InputFixtureBuilder.Zip(temp, "empty-companion.zip", entries.ToArray());
        }
        else
        {
            path = InputFixtureBuilder.Plain(temp, "app.mstat", mstatBytes);
            if (companionExists) File.WriteAllBytes(Path.ChangeExtension(path, "scan.dgml.xml"), Array.Empty<byte>());
        }
        DependencyGraphInput input;
        using (var resolved = ResolvedFile.Open(path))
        {
            Assert.Equal(3L, resolved.MstatLength);
            using (var mstat = resolved.OpenMstat())
                Assert.Equal(mstatBytes, InputFixtureBuilder.Bytes(mstat));
            bool present = !zip && companionExists;
            Assert.Equal(present, resolved.HasDgml);
            input = resolved.GraphInput;
            using var graph = resolved.OpenDgml();
            if (present)
            {
                Assert.IsType<DependencyGraphInput>(input);
                Assert.Equal(Path.ChangeExtension(path, "scan.dgml.xml"), input.Path);
                Assert.Null(input.EntryName);
                Assert.Equal(0L, input.Length);
                Assert.Empty(InputFixtureBuilder.Bytes(graph));
            }
            else
            {
                Assert.Null(input);
                Assert.Null(graph);
            }
        }
        if (input != null)
        {
            input.VerifyUnchanged();
            using (var reopened = input.Open()) Assert.Equal(-1, reopened.ReadByte());
            using var exclusive = File.Open(input.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(0L, exclusive.Length);
        }
        using var exclusiveInput = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(zip ? new FileInfo(path).Length : 3L, exclusiveInput.Length);
    }
}
