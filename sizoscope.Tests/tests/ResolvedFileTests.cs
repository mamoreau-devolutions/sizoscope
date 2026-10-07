using System.Text;
using sizoscope;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class ResolvedFileTests
{
    [Fact]
    public void ZipInput_SelectsFirstNonemptyMstatAndMatchingCompanion()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] expectedMstat = { 7, 0, 9 };
        byte[] expectedGraph = Encoding.UTF8.GetBytes("<DirectedGraph />");
        string path = InputFixtureBuilder.Zip(
            temp,
            "input.ZIP",
            ("notes.txt", new byte[] { 1 }),
            ("empty.mstat", Array.Empty<byte>()),
            ("chosen/app.MSTAT", expectedMstat),
            ("chosen/app.scan.dgml.xml", expectedGraph),
            ("later/app.mstat", new byte[] { 8 }));

        using ResolvedFile resolved = ResolvedFile.Open(path);
        using Stream mstat = resolved.OpenMstat();
        using Stream dgml = resolved.OpenDgml();

        Assert.Equal(path, resolved.OriginalPath);
        Assert.Equal(expectedMstat.LongLength, resolved.MstatLength);
        Assert.Equal(expectedMstat, InputFixtureBuilder.Bytes(mstat));
        Assert.True(resolved.HasDgml);
        Assert.NotNull(dgml);
        Assert.Equal(expectedGraph, InputFixtureBuilder.Bytes(dgml));
    }

    [Fact]
    public void Dispose_ReleasesArchiveHandleAfterStreamsAreDisposed()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = InputFixtureBuilder.Zip(
            temp,
            "app.zip",
            ("app.mstat", new byte[] { 1, 2, 3 }),
            ("app.scan.dgml.xml", new byte[] { 4, 5 }));

        using (ResolvedFile resolved = ResolvedFile.Open(path))
        {
            using Stream mstat = resolved.OpenMstat();
            using Stream dgml = resolved.OpenDgml();
            Assert.Equal(new byte[] { 1, 2, 3 }, InputFixtureBuilder.Bytes(mstat));
            Assert.NotNull(dgml);
            Assert.Equal(new byte[] { 4, 5 }, InputFixtureBuilder.Bytes(dgml));
        }

        using (FileStream exclusive =
               File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(new FileInfo(path).Length, exclusive.Length);
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
