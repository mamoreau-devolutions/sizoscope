using System.Security.Cryptography;
using System.Text;

internal static class DependencyGraphCache
{
    private static readonly SemaphoreSlim BuildBudget = new(1, 1);
    private static readonly string[] DataFiles = ["counts.bin", "nodes.bin", "edges.bin", "labels.bin", "strings.bin"];
    public static string Root
    {
        get
        {
            string configured = Environment.GetEnvironmentVariable("SIZOSCOPE_GRAPH_CACHE");
            return string.IsNullOrEmpty(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sizoscope", "DependencyGraphs")
                : Path.GetFullPath(configured);
        }
    }

    public static async Task<DependencyGraphIndex> OpenAsync(DependencyGraphInput input, bool rebuild,
        CancellationToken token, IProgress<DependencyGraphProgress> progress = null, string cacheRoot = null)
    {
        await BuildBudget.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => OpenCore(input, rebuild, token, progress, cacheRoot ?? Root), token).ConfigureAwait(false);
        }
        finally
        {
            BuildBudget.Release();
        }
    }

    private static DependencyGraphIndex OpenCore(DependencyGraphInput input, bool rebuild,
        CancellationToken token, IProgress<DependencyGraphProgress> progress, string root)
    {
        input.VerifyUnchanged();
        string keyDirectory = Path.Combine(root, input.CacheKey);
        Directory.CreateDirectory(keyDirectory);
        using var buildLock = AcquireLock(Path.Combine(keyDirectory, "build.lock"));
        File.WriteAllText(Path.Combine(keyDirectory, "owner"), "Sizoscope dependency graph cache v1");
        string pointer = Path.Combine(keyDirectory, "current");
        if (!rebuild && File.Exists(pointer))
        {
            string generation = ReadGeneration(pointer);
            string directory = Path.Combine(keyDirectory, generation);
            VerifyManifest(directory, input, token, progress);
            input.VerifyUnchanged();
            return new DependencyGraphIndex(directory);
        }

        string id = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(keyDirectory, id + ".tmp");
        string complete = Path.Combine(keyDirectory, id);
        string pointerTemp = Path.Combine(keyDirectory, id + ".pointer");
        Directory.CreateDirectory(staging);
        try
        {
            string driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
            if (new DriveInfo(driveRoot).AvailableFreeSpace < 64L * 1024 * 1024)
                throw new IOException("Insufficient space to start dependency indexing. Free cache disk space and retry.");
            using (var stream = input.Open())
            using (var hashing = new HashingStream(stream))
            {
                DependencyGraphIndex.Build(hashing, staging, token, progress);
                input.VerifyUnchanged();
                WriteManifest(staging, input, hashing.GetHash(), token, progress);
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, complete);
            File.WriteAllText(pointerTemp, id);
            File.Move(pointerTemp, pointer, true);
            return new DependencyGraphIndex(complete);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            if (File.Exists(pointerTemp))
                File.Delete(pointerTemp);
        }
    }

    private static string ReadGeneration(string pointer)
    {
        if (new FileInfo(pointer).Length > 64)
            throw new InvalidDataException("Invalid dependency cache pointer. Rebuild dependencies.");
        string generation = File.ReadAllText(pointer).Trim();
        if (!Guid.TryParseExact(generation, "N", out _))
            throw new InvalidDataException("Invalid dependency cache pointer. Rebuild dependencies.");
        return generation;
    }

    private static FileStream AcquireLock(string path)
    {
        try
        {
            return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 or 11)
        {
            throw new IOException("This dependency cache is in use by another Sizoscope operation. Retry after it finishes.", exception);
        }
    }

    private static void WriteManifest(string directory, DependencyGraphInput input, byte[] sourceHash,
        CancellationToken token, IProgress<DependencyGraphProgress> progress)
    {
        using var file = new FileStream(Path.Combine(directory, "manifest.bin"), FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(file, Encoding.UTF8, true);
        writer.Write(1);
        writer.Write(input.Identity);
        writer.Write(sourceHash);
        foreach (string name in DataFiles)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Checksumming index", 0, 0, 0));
            string path = Path.Combine(directory, name);
            writer.Write(new FileInfo(path).Length);
            writer.Write(HashFile(path, token));
        }
        writer.Flush();
        file.Flush(true);
    }

    private static void VerifyManifest(string directory, DependencyGraphInput input, CancellationToken token,
        IProgress<DependencyGraphProgress> progress)
    {
        string path = Path.Combine(directory, "manifest.bin");
        if (!File.Exists(path) || new FileInfo(path).Length > 32768)
            throw new InvalidDataException("Missing or invalid dependency manifest. Rebuild dependencies.");
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadInt32() != 1)
            throw new InvalidDataException("Incompatible dependency index. Rebuild dependencies.");
        int identityLength = reader.Read7BitEncodedInt();
        if (identityLength < 0 || identityLength > 16384 || identityLength > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid dependency manifest identity. Rebuild dependencies.");
        string identity = new UTF8Encoding(false, true).GetString(reader.ReadBytes(identityLength));
        if (identity != input.Identity || reader.ReadBytes(32).Length != 32)
            throw new InvalidDataException("Stale or incompatible dependency index. Rebuild dependencies.");
        foreach (string name in DataFiles)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Verifying cached index", 0, 0, 0));
            long length = reader.ReadInt64();
            byte[] expected = reader.ReadBytes(32);
            string dataPath = Path.Combine(directory, name);
            if (!File.Exists(dataPath) || new FileInfo(dataPath).Length != length ||
                !CryptographicOperations.FixedTimeEquals(expected, HashFile(dataPath, token)))
                throw new InvalidDataException($"Corrupt dependency index ({name}). Rebuild dependencies.");
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("Invalid dependency manifest. Rebuild dependencies.");
    }

    private static byte[] HashFile(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return hash.GetHashAndReset();
    }

    public static async Task<(int Removed, int InUse)> ClearInactiveAsync(CancellationToken token, string cacheRoot = null)
    {
        await BuildBudget.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                string root = cacheRoot ?? Root;
                int removed = 0, inUse = 0;
                if (!Directory.Exists(root))
                    return (removed, inUse);
                foreach (string key in Directory.EnumerateDirectories(root))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(key);
                    if (name.Length != 64 || name.Any(c => !Uri.IsHexDigit(c)) ||
                        (File.GetAttributes(key) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    string owner = Path.Combine(key, "owner");
                    if (!File.Exists(owner) || new FileInfo(owner).Length > 128 ||
                        File.ReadAllText(owner) != "Sizoscope dependency graph cache v1")
                        continue;
                    using var keyLock = AcquireLock(Path.Combine(key, "build.lock"));
                    foreach (string generation in Directory.EnumerateDirectories(key))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Guid.TryParseExact(Path.GetFileName(generation), "N", out _) ||
                            (File.GetAttributes(generation) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        FileStream lease;
                        try
                        {
                            lease = File.Open(Path.Combine(generation, "lease"), FileMode.OpenOrCreate,
                                FileAccess.ReadWrite, FileShare.None);
                        }
                        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 or 11)
                        {
                            inUse++;
                            continue;
                        }
                        lease.Dispose();
                        Directory.Delete(generation, true);
                        removed++;
                        string pointer = Path.Combine(key, "current");
                        if (File.Exists(pointer) && ReadGeneration(pointer) == Path.GetFileName(generation))
                            File.Delete(pointer);
                    }
                }
                return (removed, inUse);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            BuildBudget.Release();
        }
    }

    private sealed class HashingStream(Stream stream) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public byte[] GetHash() => _hash.GetHashAndReset();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = stream.Read(buffer);
            _hash.AppendData(buffer[..read]);
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _hash.Dispose();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
