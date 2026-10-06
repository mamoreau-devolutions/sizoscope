using System.Text;
using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class DependencyGraphInputTests
{
    private const string SourceChanged =
        "The dependency input changed. Reload the MSTAT to analyze the matching build.";
    private const string EntryChanged = "The dependency ZIP entry changed. Reload the MSTAT.";
    private static readonly DateTime FirstTimestamp = new(2001, 1, 2, 3, 4, 6, DateTimeKind.Utc);
    private static readonly DateTime SecondTimestamp = new(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);

    [Theory]
    [InlineData("empty")]
    [InlineData("singleton")]
    [InlineData("graph")]
    public void PlainDescriptor_CapturesMetadataAndReopensExactBytes(string kind)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] bytes = Payload(kind);
        string path = InputFixtureBuilder.Plain(temp, "source.dgml", bytes);
        SetTimestamp(path, FirstTimestamp);
        var input = new DependencyGraphInput(path);
        // The input remains absolute, but normalization must eliminate the owned parent segment.
        var equivalent = new DependencyGraphInput(Path.Combine(temp.Path, "unused", "..", "source.dgml"));
        Assert.Equal(path, input.Path);
        Assert.Null(input.EntryName);
        Assert.Equal((long)bytes.Length, input.Length);
        Assert.Equal(input.Identity, equivalent.Identity);
        Assert.Equal(input.CacheKey, equivalent.CacheKey);
        input.VerifyUnchanged();
        input.VerifyUnchanged();
        using (var first = input.Open())
            Assert.Equal(bytes, InputFixtureBuilder.Bytes(first));
        using (var second = input.Open())
        {
            var buffer = new byte[bytes.Length];
            second.ReadExactly(buffer.AsSpan());
            Assert.Equal(bytes, buffer);
            Assert.Equal(-1, second.ReadByte());
        }
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal((long)bytes.Length, exclusive.Length);
    }

    [Fact]
    public void DescriptorIdentity_DistinguishesPathMetadataAndFullZipEntry()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] bytes = { 7, 0, 255 };
        string path = InputFixtureBuilder.Plain(temp, "one.dgml", bytes);
        string otherPath = InputFixtureBuilder.Plain(temp, "two.dgml", bytes);
        SetTimestamp(path, FirstTimestamp);
        SetTimestamp(otherPath, FirstTimestamp);
        var original = new DependencyGraphInput(path);
        var identical = new DependencyGraphInput(path);
        var differentPath = new DependencyGraphInput(otherPath);
        Assert.Equal(original.Identity, identical.Identity);
        Assert.Equal(original.CacheKey, identical.CacheKey);
        AssertDistinct(original, differentPath);
        string capturedIdentity = original.Identity;
        string capturedKey = original.CacheKey;
        File.WriteAllBytes(path, new byte[] { 7, 0, 255, 9 });
        SetTimestamp(path, FirstTimestamp); // Length changes independently of timestamp.
        var differentLength = new DependencyGraphInput(path);
        Assert.Equal(4L, differentLength.Length);
        AssertDistinct(original, differentLength);
        SetTimestamp(path, SecondTimestamp); // Timestamp changes independently of length.
        var differentTimestamp = new DependencyGraphInput(path);
        Assert.Equal(4L, differentTimestamp.Length);
        AssertDistinct(differentLength, differentTimestamp);
        Assert.Equal(3L, original.Length);
        Assert.Equal(capturedIdentity, original.Identity);
        Assert.Equal(capturedKey, original.CacheKey);

        string archive = InputFixtureBuilder.Zip(temp, "entries.zip",
            ("alpha/shared.dgml", bytes), ("beta/shared.dgml", bytes));
        var alpha = new DependencyGraphInput(archive, "alpha/shared.dgml", 3L);
        var alphaAgain = new DependencyGraphInput(archive, "alpha/shared.dgml", 3L);
        var beta = new DependencyGraphInput(archive, "beta/shared.dgml", 3L);
        Assert.Equal("alpha/shared.dgml", alpha.EntryName);
        Assert.Equal("beta/shared.dgml", beta.EntryName);
        Assert.Equal(alpha.Identity, alphaAgain.Identity);
        Assert.Equal(alpha.CacheKey, alphaAgain.CacheKey);
        AssertDistinct(alpha, beta);
        AssertDistinct(alpha, new DependencyGraphInput(archive, "alpha/shared.dgml", 4L));
        using (var stream = alpha.Open()) Assert.Equal(bytes, InputFixtureBuilder.Bytes(stream));
        using (var stream = beta.Open()) Assert.Equal(bytes, InputFixtureBuilder.Bytes(stream));
    }

    [Theory]
    [InlineData("append")]
    [InlineData("timestamp")]
    [InlineData("delete")]
    public void ChangedSource_VerifyUnchangedAndOpenReject(string change)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = InputFixtureBuilder.Plain(temp, "changed.dgml", new byte[] { 1, 2, 3 });
        SetTimestamp(path, FirstTimestamp);
        var input = new DependencyGraphInput(path);
        string identity = input.Identity;
        string key = input.CacheKey;
        switch (change)
        {
            case "append":
                using (var writer = File.Open(path, FileMode.Append, FileAccess.Write)) writer.WriteByte(4);
                SetTimestamp(path, FirstTimestamp);
                Assert.Equal(4L, new FileInfo(path).Length);
                break;
            case "timestamp":
                SetTimestamp(path, SecondTimestamp);
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
                break;
            case "delete":
                File.Delete(path);
                Assert.False(File.Exists(path));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
        Assert.Equal(SourceChanged, Assert.Throws<InvalidDataException>(input.VerifyUnchanged).Message);
        Assert.Equal(SourceChanged, Assert.Throws<InvalidDataException>(() =>
        {
            using var unexpected = input.Open();
        }).Message);
        Assert.Equal(3L, input.Length);
        Assert.Equal(identity, input.Identity);
        Assert.Equal(key, input.CacheKey);
        if (change != "delete")
        {
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(change == "append" ? 4L : 3L, exclusive.Length);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("graph")]
    public void ZipDescriptor_OpenReturnsExactSelectedEntryAndReleasesArchive(string kind)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        byte[] bytes = Payload(kind);
        string path = InputFixtureBuilder.Zip(temp, "selected.zip",
            ("decoy/graph.dgml", new byte[] { 99 }),
            ("nested/graph.dgml", bytes));
        var input = new DependencyGraphInput(path, "nested/graph.dgml", bytes.LongLength);
        Assert.Equal(path, input.Path);
        Assert.Equal("nested/graph.dgml", input.EntryName);
        Assert.Equal(bytes.LongLength, input.Length);
        input.VerifyUnchanged();
        using (var first = input.Open())
            Assert.Equal(bytes, InputFixtureBuilder.Bytes(first));
        using (var second = input.Open())
        {
            var buffer = new byte[bytes.Length];
            second.ReadExactly(buffer.AsSpan());
            Assert.Equal(bytes, buffer);
            Assert.Equal(-1, second.ReadByte());
        }
        input.VerifyUnchanged();
        AssertExclusiveAndDelete(path);
    }

    [Theory]
    [InlineData("missing-entry", "absent/graph.dgml", 3L)]
    [InlineData("wrong-declared-length", "nested/graph.dgml", 2L)]
    public void ZipDescriptor_OpenFailureReleasesArchive(string kind, string entryName, long declaredLength)
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = InputFixtureBuilder.Zip(temp, kind + ".zip",
            ("nested/graph.dgml", new byte[] { 1, 0, 3 }));
        var input = new DependencyGraphInput(path, entryName, declaredLength);
        Assert.Equal(path, input.Path);
        Assert.Equal(entryName, input.EntryName);
        Assert.Equal(declaredLength, input.Length);
        input.VerifyUnchanged(); // Failure must reach entry validation, not source validation.
        Assert.Equal(EntryChanged, Assert.Throws<InvalidDataException>(() =>
        {
            using var unexpected = input.Open();
        }).Message);
        input.VerifyUnchanged();
        AssertExclusiveAndDelete(path);
    }

    [Fact]
    public void Constructor_MissingOwnedSourceThrowsFileNotFound()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = temp.FilePath("absent.dgml");
        var error = Assert.Throws<FileNotFoundException>(() => new DependencyGraphInput(path));
        Assert.Equal(path, error.FileName);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ZipDescriptor_CorruptArchiveOpenReleasesSource()
    {
        using var temp = new OwnedTempDirectory(Path.GetTempPath());
        string path = InputFixtureBuilder.Plain(temp, "corrupt.zip", new byte[] { 1, 2, 3, 4 });
        var input = new DependencyGraphInput(path, "nested/graph.dgml", 1L);
        input.VerifyUnchanged();
        Assert.Throws<InvalidDataException>(() =>
        {
            using var unexpected = input.Open();
        });
        AssertExclusiveAndDelete(path);
    }

    private static byte[] Payload(string kind) => kind switch
    {
        "empty" => Array.Empty<byte>(),
        "singleton" => new byte[] { 165 },
        "graph" => Encoding.UTF8.GetBytes(
            "<DirectedGraph><Nodes><Node Id=\"41\" Label=\"Nœud.二\"/></Nodes></DirectedGraph>"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void SetTimestamp(string path, DateTime timestamp)
    {
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    private static void AssertDistinct(DependencyGraphInput first, DependencyGraphInput second)
    {
        Assert.NotEqual(first.Identity, second.Identity);
        Assert.NotEqual(first.CacheKey, second.CacheKey);
    }

    private static void AssertExclusiveAndDelete(string path)
    {
        using (var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(exclusive.Length > 0); // Archives/corrupt fixtures all contain concrete bytes.
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
