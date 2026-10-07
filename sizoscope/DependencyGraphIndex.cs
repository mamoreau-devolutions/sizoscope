using System.Globalization;
using System.Text;
using System.Xml;

internal sealed record DependencyGraphNode(int Id, string Name);
internal sealed record DependencyGraphEdge(DependencyGraphNode Node, string Reason);
internal sealed record DependencyGraphPage(IReadOnlyList<DependencyGraphEdge> Edges, long? NextOffset);
internal sealed record DependencyGraphProgress(string Stage, long Nodes, long Edges, long Bytes);

internal sealed class DependencyGraphIndex : IDisposable
{
    internal const int RecordSize = 32;
    internal const int MaxStringBytes = 8 * 1024 * 1024;
    private const long PageByteBudget = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly BinaryReader _nodes, _edges, _labels, _strings;
    private readonly long _nodeCount, _edgeCount;
    private readonly FileStream _lease;
    private bool _disposed;

    internal readonly record struct Record(long Key, long Value, long Data, long Sequence)
    {
        internal static Record Read(BinaryReader reader) =>
            new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
        internal void Write(BinaryWriter writer)
        {
            writer.Write(Key);
            writer.Write(Value);
            writer.Write(Data);
            writer.Write(Sequence);
        }
    }

    public DependencyGraphIndex(string directory)
    {
        var opened = new List<BinaryReader>();
        try
        {
            _lease = File.Open(Path.Combine(directory, "lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            BinaryReader Open(string name)
            {
                var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, name)), Encoding.UTF8);
                opened.Add(reader);
                return reader;
            }
            using var counts = Open("counts.bin");
            if (counts.ReadInt32() != 1)
                throw new InvalidDataException("Unsupported dependency index version. Rebuild dependencies.");
            _nodeCount = counts.ReadInt64();
            _edgeCount = counts.ReadInt64();
            _nodes = Open("nodes.bin");
            _edges = Open("edges.bin");
            _labels = Open("labels.bin");
            _strings = Open("strings.bin");
            if (_nodeCount < 0 || _edgeCount < 0 ||
                _nodes.BaseStream.Length != checked(_nodeCount * RecordSize) ||
                _labels.BaseStream.Length != checked(_nodeCount * RecordSize) ||
                _edges.BaseStream.Length != checked(_edgeCount * RecordSize))
                throw new InvalidDataException("Incomplete dependency index. Rebuild dependencies.");
        }
        catch
        {
            _lease?.Dispose();
            foreach (var reader in opened)
                reader.Dispose();
            throw;
        }
    }

    public DependencyGraphNode FindNode(string name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long hash = LabelHash(name);
            long position = hash == long.MaxValue ? _nodeCount : LowerBound(_labels, _nodeCount, hash + 1);
            while (position > 0)
            {
                Record record = At(_labels, --position);
                if (record.Key != hash)
                    break;
                if (string.Equals(ReadString(record.Data), name, StringComparison.Ordinal))
                    return new DependencyGraphNode(checked((int)record.Value), name);
            }
            return null;
        }
    }

    public long NodeCount => _nodeCount;

    public IReadOnlyList<DependencyGraphNode> SearchNodes(Func<string, bool> predicate, int limit, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var result = new List<DependencyGraphNode>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (long position = 0; position < _nodeCount && result.Count < limit; position++)
            {
                if ((position & 0xFFF) == 0)
                    token.ThrowIfCancellationRequested();
                Record record = At(_nodes, position);
                string name = ReadString(record.Value);
                if (predicate(name))
                    result.Add(new DependencyGraphNode(checked((int)record.Key), name));
            }
        }
        return result;
    }

    public DependencyGraphNode GetNode(int id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GetNodeCore(id);
        }
    }

    private DependencyGraphNode GetNodeCore(int id)
    {
        long position = LowerBound(_nodes, _nodeCount, id);
        if (position >= _nodeCount)
            throw new InvalidDataException($"Dependency index has no node {id}. Rebuild dependencies.");
        Record record = At(_nodes, position);
        if (record.Key != id)
            throw new InvalidDataException($"Dependency index has no node {id}. Rebuild dependencies.");
        return new DependencyGraphNode(id, ReadString(record.Value));
    }

    public DependencyGraphPage ReadIncoming(int id, long offset = 0, int pageSize = 200, CancellationToken token = default)
    {
        if (offset < 0 || pageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(offset), "Use a nonnegative offset and a page size between 1 and 200.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            GetNodeCore(id);
            long start = LowerBound(_edges, _edgeCount, id);
            long end = id == int.MaxValue ? _edgeCount : LowerBound(_edges, _edgeCount, (long)id + 1);
            if (offset > end - start)
                throw new ArgumentOutOfRangeException(nameof(offset));
            long position = start + offset;
            var result = new List<DependencyGraphEdge>();
            long bytes = 0;
            while (position < end && result.Count < pageSize)
            {
                token.ThrowIfCancellationRequested();
                Record edge = At(_edges, position);
                var source = GetNodeCore(checked((int)edge.Value));
                string reason = edge.Data < 0 ? null : ReadString(edge.Data);
                long size = 2L * (source.Name.Length + (reason?.Length ?? 0)) + 128;
                if (bytes + size > PageByteBudget)
                {
                    if (result.Count == 0)
                        throw new InvalidDataException("A dependency exceeds the root page memory budget.");
                    break;
                }
                result.Add(new DependencyGraphEdge(source, reason));
                bytes += size;
                position++;
            }
            return new DependencyGraphPage(result, position < end ? position - start : null);
        }
    }

    private string ReadString(long offset)
    {
        if (offset < 0 || offset > _strings.BaseStream.Length - sizeof(int))
            throw new InvalidDataException("Invalid dependency string offset. Rebuild dependencies.");
        _strings.BaseStream.Position = offset;
        int length = _strings.ReadInt32();
        if (length < 0 || length > MaxStringBytes || length > _strings.BaseStream.Length - _strings.BaseStream.Position)
            throw new InvalidDataException("Invalid dependency string length. Rebuild dependencies.");
        byte[] bytes = _strings.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Incomplete dependency string.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static Record At(BinaryReader reader, long index)
    {
        reader.BaseStream.Position = checked(index * RecordSize);
        return Record.Read(reader);
    }

    private static long LowerBound(BinaryReader reader, long count, long key)
    {
        long lo = 0, hi = count;
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            if (At(reader, mid).Key < key)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static long LabelHash(string name)
    {
        // Hash UTF-16 code units so lookup does not need a second encoded string.
        ulong hash = 14695981039346656037;
        foreach (char character in name)
            hash = unchecked((hash ^ character) * 1099511628211);
        return unchecked((long)hash);
    }

    public static void Build(Stream xml, string directory, CancellationToken token = default,
        IProgress<DependencyGraphProgress> progress = null, int sortBufferRecords = 262144)
    {
        if (sortBufferRecords is < 1 or > 1048576)
            throw new ArgumentOutOfRangeException(nameof(sortBufferRecords));
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Dependency index output must be an empty directory.");
        Directory.CreateDirectory(directory);
        using var bounded = new BoundedXmlStream(xml, token);
        using var nodes = new ExternalSorter(directory, "node-runs", sortBufferRecords, token);
        using var edges = new ExternalSorter(directory, "edge-runs", sortBufferRecords, token);
        using var labels = new ExternalSorter(directory, "label-runs", sortBufferRecords, token);
        using var references = new ExternalSorter(directory, "reference-runs", sortBufferRecords, token);
        using var strings = new BinaryWriter(CreateSequential(Path.Combine(directory, "strings.bin")), Encoding.UTF8);
        using var reader = XmlReader.Create(bounded, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
            IgnoreComments = true,
            NameTable = new BoundedNameTable()
        });
        long nodeCount = 0, edgeCount = 0;
        long stringOffset = 0;
        bool foundGraph = false;
        int nodesDepth = -1, linksDepth = -1;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.Depth > 128)
                throw new InvalidDataException("Dependency XML nesting exceeds the 128-level limit.");
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "Nodes")
                    nodesDepth = -1;
                if (reader.LocalName == "Links")
                    linksDepth = -1;
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            if (reader.LocalName == "DirectedGraph" && reader.Depth == 0)
                foundGraph = true;
            if (!foundGraph)
                throw new InvalidDataException("Expected a DGML DirectedGraph document.");
            if (reader.LocalName == "Nodes" && !reader.IsEmptyElement)
                nodesDepth = reader.Depth;
            if (reader.LocalName == "Links" && !reader.IsEmptyElement)
                linksDepth = reader.Depth;
            if (reader.LocalName == "Node" && nodesDepth >= 0 && reader.Depth == nodesDepth + 1)
            {
                int id = ReadId(reader, "Id");
                string name = reader.GetAttribute("Label") ??
                    throw new InvalidDataException($"Node {id} is missing Label.");
                long location = WriteString(strings, name, ref stringOffset);
                nodes.Add(new Record(id, location, 0, nodeCount));
                labels.Add(new Record(LabelHash(name), id, location, nodeCount));
                nodeCount++;
            }
            else if (reader.LocalName == "Link" && linksDepth >= 0 && reader.Depth == linksDepth + 1)
            {
                int source = ReadId(reader, "Source"), target = ReadId(reader, "Target");
                string reason = reader.GetAttribute("Reason");
                edges.Add(new Record(target, source, reason == null ? -1 : WriteString(strings, reason, ref stringOffset), edgeCount));
                references.Add(new Record(source, 0, 0, edgeCount));
                references.Add(new Record(target, 0, 0, edgeCount));
                edgeCount++;
            }
            if ((nodeCount + edgeCount) % 16384 == 0)
                progress?.Report(new("Reading XML", nodeCount, edgeCount, bounded.BytesRead));
        }
        if (!foundGraph)
            throw new InvalidDataException("Expected a DGML DirectedGraph document.");
        strings.Flush();
        progress?.Report(new("Sorting node directory", nodeCount, edgeCount, bounded.BytesRead));
        nodes.Finish(Path.Combine(directory, "nodes.bin"));
        progress?.Report(new("Sorting dependencies", nodeCount, edgeCount, bounded.BytesRead));
        edges.Finish(Path.Combine(directory, "edges.bin"));
        progress?.Report(new("Sorting labels", nodeCount, edgeCount, bounded.BytesRead));
        labels.Finish(Path.Combine(directory, "labels.bin"));
        string referencePath = Path.Combine(directory, "references.bin");
        references.Finish(referencePath);
        progress?.Report(new("Validating references", nodeCount, edgeCount, bounded.BytesRead));
        ValidateReferences(Path.Combine(directory, "nodes.bin"), referencePath, token);
        File.Delete(referencePath);
        token.ThrowIfCancellationRequested();
        using var counts = new BinaryWriter(File.Create(Path.Combine(directory, "counts.bin")));
        counts.Write(1);
        counts.Write(nodeCount);
        counts.Write(edgeCount);
        counts.Flush();
        progress?.Report(new("Index complete", nodeCount, edgeCount, bounded.BytesRead));
    }

    private static int ReadId(XmlReader reader, string attribute)
    {
        string value = reader.GetAttribute(attribute);
        if (value == null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            throw new InvalidDataException($"Invalid or missing {attribute} on {reader.LocalName}; expected a nonnegative 32-bit node ID.");
        return id;
    }

    private static long WriteString(BinaryWriter writer, string value, ref long position)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        if (length > MaxStringBytes)
            throw new InvalidDataException($"A dependency label/reason exceeds the {MaxStringBytes}-byte record limit.");
        // BinaryWriter.BaseStream flushes its stream; track offsets to preserve batched I/O.
        long offset = position;
        writer.Write(length);
        writer.Write(Encoding.UTF8.GetBytes(value));
        position = checked(position + sizeof(int) + length);
        return offset;
    }

    private static FileStream CreateSequential(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);

    private static FileStream ReadSequential(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);

    private static void ValidateReferences(string nodePath, string referencePath, CancellationToken token)
    {
        using var nodes = new BinaryReader(ReadSequential(nodePath));
        using var references = new BinaryReader(ReadSequential(referencePath));
        long previous = -1;
        Record? current = null;
        void Advance()
        {
            token.ThrowIfCancellationRequested();
            if (nodes.BaseStream.Position == nodes.BaseStream.Length)
            {
                current = null;
                return;
            }
            Record next = Record.Read(nodes);
            if (next.Key <= previous)
                throw new InvalidDataException($"Duplicate node ID {next.Key}.");
            previous = next.Key;
            current = next;
        }
        Advance();
        while (references.BaseStream.Position < references.BaseStream.Length)
        {
            token.ThrowIfCancellationRequested();
            Record reference = Record.Read(references);
            while (current.HasValue && current.Value.Key < reference.Key)
                Advance();
            if (!current.HasValue || current.Value.Key != reference.Key)
                throw new InvalidDataException($"Dependency refers to missing node {reference.Key}.");
        }
        while (current.HasValue)
            Advance();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _nodes.Dispose();
            _edges.Dispose();
            _labels.Dispose();
            _strings.Dispose();
            _lease.Dispose();
        }
    }

    private sealed class ExternalSorter : IDisposable
    {
        private readonly string _directory;
        private readonly Record[] _buffer;
        private readonly CancellationToken _token;
        private int _used;
        private long _runCount;
        private readonly DriveInfo _drive;
        private static readonly IComparer<Record> Comparer = System.Collections.Generic.Comparer<Record>.Create((a, b) =>
        {
            int result = a.Key.CompareTo(b.Key);
            return result == 0 ? a.Sequence.CompareTo(b.Sequence) : result;
        });

        public ExternalSorter(string parent, string name, int capacity, CancellationToken token)
        {
            _directory = Path.Combine(parent, name);
            Directory.CreateDirectory(_directory);
            _buffer = new Record[capacity];
            _token = token;
            _drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(parent)));
        }

        public void Add(Record record)
        {
            _buffer[_used++] = record;
            if (_used == _buffer.Length)
                Flush();
        }

        private string Run(int pass, long number) => Path.Combine(_directory, $"{pass}-{number}.bin");

        private void Flush()
        {
            if (_used == 0)
                return;
            _token.ThrowIfCancellationRequested();
            CheckSpace();
            Array.Sort(_buffer, 0, _used, Comparer);
            using var writer = new BinaryWriter(CreateSequential(Run(0, _runCount++)));
            for (int i = 0; i < _used; i++)
                _buffer[i].Write(writer);
            _used = 0;
        }

        public void Finish(string destination)
        {
            Flush();
            if (_runCount == 0)
            {
                using var empty = File.Create(destination);
                return;
            }
            int pass = 0;
            long count = _runCount;
            while (count > 1)
            {
                long nextCount = 0;
                for (long first = 0; first < count; first += 16)
                {
                    _token.ThrowIfCancellationRequested();
                    CheckSpace();
                    Merge(pass, first, Math.Min(16, count - first), Run(pass + 1, nextCount++));
                }
                pass++;
                count = nextCount;
            }
            File.Move(Run(pass, 0), destination);
        }

        private void Merge(int pass, long first, long count, string destination)
        {
            var readers = new List<BinaryReader>();
            try
            {
                var queue = new PriorityQueue<int, Record>(Comparer);
                for (long i = 0; i < count; i++)
                {
                    var reader = new BinaryReader(ReadSequential(Run(pass, first + i)));
                    readers.Add(reader);
                    queue.Enqueue(readers.Count - 1, Record.Read(reader));
                }
                using var writer = new BinaryWriter(CreateSequential(destination));
                while (queue.TryDequeue(out int index, out Record record))
                {
                    _token.ThrowIfCancellationRequested();
                    record.Write(writer);
                    var reader = readers[index];
                    if (reader.BaseStream.Position < reader.BaseStream.Length)
                        queue.Enqueue(index, Record.Read(reader));
                }
            }
            finally
            {
                foreach (var reader in readers)
                    reader.Dispose();
            }
            for (long i = 0; i < count; i++)
                File.Delete(Run(pass, first + i));
        }

        public void Dispose() => Directory.Delete(_directory, true);

        private void CheckSpace()
        {
            if (_drive.AvailableFreeSpace < 32L * 1024 * 1024)
                throw new IOException("Dependency indexing ran low on cache disk space. Free space and retry.");
        }
    }

    private sealed class BoundedXmlStream(Stream source, CancellationToken token) : Stream
    {
        private long _sinceMarkup;
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            int count = source.Read(buffer);
            BytesRead += count;
            ReadOnlySpan<byte> remaining = buffer[..count];
            while (!remaining.IsEmpty)
            {
                int next = remaining.IndexOf((byte)'<');
                int length = next < 0 ? remaining.Length : next;
                _sinceMarkup += length;
                if (_sinceMarkup > MaxStringBytes)
                    throw new InvalidDataException("An XML element/text record exceeds the graph memory limit.");
                if (next < 0)
                    break;
                _sinceMarkup = 0;
                remaining = remaining[(next + 1)..];
            }
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BoundedNameTable : XmlNameTable
    {
        private readonly NameTable _names = new();
        private int _count, _characters;
        public override string Get(string value) => _names.Get(value);
        public override string Get(char[] value, int start, int length) => _names.Get(value, start, length);
        public override string Add(string value)
        {
            string existing = _names.Get(value);
            if (existing != null)
                return existing;
            Reserve(value.Length);
            return _names.Add(value);
        }
        public override string Add(char[] value, int start, int length)
        {
            string existing = _names.Get(value, start, length);
            if (existing != null)
                return existing;
            Reserve(length);
            return _names.Add(value, start, length);
        }
        private void Reserve(int length)
        {
            if (_count >= 4096 || length > 1024 * 1024 - _characters)
                throw new InvalidDataException("Dependency XML exceeds the bounded XML name-table limit.");
            _count++;
            _characters += length;
        }
    }
}
