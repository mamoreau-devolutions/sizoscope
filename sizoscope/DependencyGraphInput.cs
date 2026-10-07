using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

internal sealed class DependencyGraphInput
{
    public string Path { get; }
    public string EntryName { get; }
    public long Length { get; }
    private readonly long _fileLength, _lastWriteTicks;
    public string Identity => $"{Path}\n{_fileLength}\n{_lastWriteTicks}\n{EntryName}\n{Length}";
    public string CacheKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("sizoscope-graph-v1\n" + Identity)));

    public DependencyGraphInput(string path, string entryName = null, long? entryLength = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        EntryName = entryName;
        var info = new FileInfo(Path);
        _fileLength = info.Length;
        _lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        Length = entryLength ?? _fileLength;
    }

    public void VerifyUnchanged()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length != _fileLength || info.LastWriteTimeUtc.Ticks != _lastWriteTicks)
            throw new InvalidDataException("The dependency input changed. Reload the MSTAT to analyze the matching build.");
    }

    public Stream Open()
    {
        VerifyUnchanged();
        if (EntryName == null)
            return new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var archive = ZipFile.OpenRead(Path);
        try
        {
            var entry = archive.GetEntry(EntryName);
            if (entry == null || entry.Length != Length)
                throw new InvalidDataException("The dependency ZIP entry changed. Reload the MSTAT.");
            return new ArchiveStream(entry.Open(), archive);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private sealed class ArchiveStream(Stream stream, ZipArchive archive) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => stream.Read(buffer);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stream.Dispose();
                archive.Dispose();
            }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
