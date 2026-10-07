using System.Globalization;

namespace SizoscopeCli
{
    // Reports the native metadata blob composition from an ILC metadata log
    // (<IlcGenerateMetadataLog>true</IlcGenerateMetadataLog>, "Handle, Kind, Name, Children").
    // The log exists even when ILC fails with an oversized metadata blob, so no MSTAT is needed.
    internal static class MetadataCommand
    {
        // Largest metadata blob NativeAOT can address.
        internal const long MetadataLimit = 33_554_431;
        const int OffsetBits = 24;
        const uint OffsetMask = (1u << OffsetBits) - 1;
        const int PreviewLength = 160;

        public static int Run(string[] args)
        {
            string path = null, outputFile = null, assemblyFilter = null;
            int top = 25;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                bool hasValue = i + 1 < args.Length;
                if (arg is "--help" or "-h" or "-?" or "/?")
                {
                    PrintUsage(Console.Out);
                    return 0;
                }
                else if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) && hasValue)
                    outputFile = args[++i];
                else if (arg.Equals("--assembly", StringComparison.OrdinalIgnoreCase) && hasValue)
                    assemblyFilter = args[++i];
                else if (arg.Equals("--top", StringComparison.OrdinalIgnoreCase) && hasValue)
                {
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out top) || top < 1)
                        return Fail($"Invalid --top value: {args[i]}");
                }
                else if (arg.StartsWith("-", StringComparison.Ordinal))
                    return Fail($"Unknown option: {arg}");
                else if (path == null)
                    path = arg;
                else
                    return Fail($"Unexpected argument: {arg}");
            }

            if (path == null)
                return Fail("Expected an ILC metadata log (*.metadata.csv).");
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"File not found: {path}");
                return 1;
            }

            MetadataLog log;
            try
            {
                log = MetadataLog.Read(path);
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"Not a valid ILC metadata log: {ex.Message}");
                return 1;
            }

            if (outputFile != null)
            {
                using var writer = new StreamWriter(outputFile);
                Report(writer, log, top, assemblyFilter);
            }
            else
                Report(Console.Out, log, top, assemblyFilter);
            return 0;
        }

        static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            PrintUsage(Console.Error);
            return 1;
        }

        internal static void PrintUsage(TextWriter w)
        {
            w.WriteLine("Usage: sizoscope-cli metadata <app.metadata.csv> [options]");
            w.WriteLine();
            w.WriteLine("Breaks down the NativeAOT reflection metadata blob from an ILC metadata log");
            w.WriteLine("(<IlcGenerateMetadataLog>true</IlcGenerateMetadataLog>). Works when ILC failed");
            w.WriteLine("because the metadata blob exceeded its limit and no MSTAT was written.");
            w.WriteLine();
            w.WriteLine("Each record is charged to the first assembly/namespace/type that owns it.");
            w.WriteLine("Records no type definition owns (e.g. stack-trace method names) are listed separately.");
            w.WriteLine();
            w.WriteLine("Options:");
            w.WriteLine("  --top <n>          Rows per section (default 25)");
            w.WriteLine("  --assembly <name>  Limit namespace/type/string sections to one assembly");
            w.WriteLine("  --output <file>    Write output to a file instead of stdout");
            w.WriteLine("  --help             Show this help");
        }

        static void Report(TextWriter w, MetadataLog log, int top, string assemblyFilter)
        {
            int count = log.Count;
            long total = count == 0 ? 0 : log.Offsets[count - 1];
            w.WriteLine($"Metadata blob: {total:N0} bytes in {count:N0} records (end of last record not logged)");
            long delta = total - MetadataLimit;
            w.WriteLine(delta > 0
                ? $"NativeAOT metadata limit: {MetadataLimit:N0} bytes -- OVER by {delta:N0}"
                : $"NativeAOT metadata limit: {MetadataLimit:N0} bytes -- {-delta:N0} bytes of headroom");

            var owners = log.ComputeOwners();
            var kinds = new Dictionary<string, (long Size, int Count)>();
            var unowned = new Dictionary<string, (long Size, int Count)>();
            var assemblies = new Dictionary<string, long>();
            var namespaces = new Dictionary<(string, string), long>();
            var types = new Dictionary<(string, string), long>();
            long unownedTotal = 0;
            for (int i = 0; i < count; i++)
            {
                long size = log.SizeOf(i);
                string kind = log.Kinds[i];
                Add(kinds, kind, size);
                Owner owner = owners[i];
                if (owner == null)
                {
                    Add(unowned, kind, size);
                    unownedTotal += size;
                    continue;
                }
                assemblies[owner.Assembly] = assemblies.GetValueOrDefault(owner.Assembly) + size;
                if (assemblyFilter != null && !owner.Assembly.Equals(assemblyFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                namespaces[(owner.Assembly, owner.Namespace)] = namespaces.GetValueOrDefault((owner.Assembly, owner.Namespace)) + size;
                if (owner.Type != null)
                    types[(owner.Assembly, owner.Type)] = types.GetValueOrDefault((owner.Assembly, owner.Type)) + size;
            }

            w.WriteLine();
            w.WriteLine("=== By record kind ===");
            foreach (var (kind, value) in kinds.OrderByDescending(p => p.Value.Size).ThenBy(p => p.Key, StringComparer.Ordinal).Take(top))
                w.WriteLine($"  {Bytes(value.Size, total)}  {value.Count,10:N0}  {kind}");

            w.WriteLine();
            w.WriteLine("=== By assembly ===");
            foreach (var (name, size) in Sorted(assemblies, top))
                w.WriteLine($"  {Bytes(size, total)}  {name}");
            w.WriteLine($"  {Bytes(unownedTotal, total)}  (not owned by any type definition)");

            if (assemblyFilter != null)
            {
                w.WriteLine();
                w.WriteLine($"Filtered to assembly: {assemblyFilter}");
            }

            w.WriteLine();
            w.WriteLine("=== By namespace ===");
            foreach (var ((asm, ns), size) in Sorted(namespaces, top))
                w.WriteLine($"  {Bytes(size, total)}  [{asm}] {(ns.Length == 0 ? "<global>" : ns)}");

            w.WriteLine();
            w.WriteLine("=== By type ===");
            foreach (var ((asm, type), size) in Sorted(types, top))
                w.WriteLine($"  {Bytes(size, total)}  [{asm}] {type}");

            w.WriteLine();
            w.WriteLine("=== Largest strings ===");
            var strings = Enumerable.Range(0, count)
                .Where(i => log.Kinds[i] == "ConstantStringValue")
                .Where(i => assemblyFilter == null || (owners[i] != null && owners[i].Assembly.Equals(assemblyFilter, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(log.SizeOf).ThenBy(i => i).Take(top);
            foreach (int i in strings)
            {
                Owner owner = owners[i];
                string where = owner == null ? "(unowned)" : owner.Type != null ? $"[{owner.Assembly}] {owner.Type}" : $"[{owner.Assembly}]";
                w.WriteLine($"  {Bytes(log.SizeOf(i), total)}  {where}: {log.Previews[i]}");
            }

            if (unownedTotal > 0)
            {
                w.WriteLine();
                w.WriteLine("=== Not owned by any type definition ===");
                w.WriteLine("  Typically method names/signatures kept only for stack traces (StackTraceSupport).");
                foreach (var (kind, value) in unowned.OrderByDescending(p => p.Value.Size).ThenBy(p => p.Key, StringComparer.Ordinal).Take(top))
                    w.WriteLine($"  {Bytes(value.Size, total)}  {value.Count,10:N0}  {kind}");
            }
        }

        static void Add(Dictionary<string, (long Size, int Count)> map, string key, long size)
        {
            var current = map.GetValueOrDefault(key);
            map[key] = (current.Size + size, current.Count + 1);
        }

        static IEnumerable<KeyValuePair<TKey, long>> Sorted<TKey>(Dictionary<TKey, long> map, int top)
            => map.OrderByDescending(p => p.Value).ThenBy(p => p.Key.ToString(), StringComparer.Ordinal).Take(top);

        static string Bytes(long size, long total)
            => $"{size,13:N0} {(total == 0 ? 0 : 100.0 * size / total),5:0.0}%";

        internal sealed record Owner(string Assembly, string Namespace, string Type);

        internal sealed class MetadataLog
        {
            public int Count => Handles.Count;
            public readonly List<uint> Handles = new();
            public readonly List<string> Kinds = new();
            public readonly List<long> Offsets = new();
            // Display names are kept only for owners and strings to bound memory on huge logs.
            public readonly List<string> Previews = new();
            readonly List<int> _childStart = new();
            readonly List<uint> _children = new();
            Dictionary<uint, int> _byHandle;
            Dictionary<uint, List<int>> _duplicates;

            public long SizeOf(int index)
                => index + 1 < Count ? Offsets[index + 1] - Offsets[index] : 0;

            public static MetadataLog Read(string path)
            {
                var log = new MetadataLog();
                var kindPool = new Dictionary<string, string>(StringComparer.Ordinal);
                using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);
                string header = reader.ReadLine();
                if (header == null || !header.StartsWith("Handle, Kind, Name", StringComparison.Ordinal))
                    throw new InvalidDataException("missing 'Handle, Kind, Name, Children' header.");

                var pending = new System.Text.StringBuilder();
                uint handle = 0;
                string kind = null;
                long wrapBase = 0, previous = -1;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (TryParseStart(line, out uint nextHandle, out string nextKind, out int restStart))
                    {
                        if (kind != null)
                            log.Add(handle, kind, pending.ToString());
                        handle = nextHandle;
                        kind = kindPool.TryGetValue(nextKind, out string pooled) ? pooled : kindPool[nextKind] = nextKind;
                        pending.Clear().Append(line, restStart, line.Length - restStart);

                        // Handles carry only 24 offset bits; the log is in emission order, so a large
                        // backwards jump means the offset wrapped past another 16 MiB.
                        long offset = handle & OffsetMask;
                        if (previous >= 0 && offset < previous && previous - offset > (1L << (OffsetBits - 1)))
                            wrapBase += 1L << OffsetBits;
                        previous = offset;
                        log.Offsets.Add(wrapBase + offset);
                    }
                    else if (kind != null)
                        pending.Append('\n').Append(line);
                    else
                        throw new InvalidDataException("first record does not start with '<handle>, <kind>, '.");
                }
                if (kind != null)
                    log.Add(handle, kind, pending.ToString());
                return log;
            }

            static bool TryParseStart(string line, out uint handle, out string kind, out int restStart)
            {
                handle = 0; kind = null; restStart = 0;
                if (line.Length < 12 || line[8] != ',' || line[9] != ' ')
                    return false;
                if (!uint.TryParse(line.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out handle))
                    return false;
                int end = line.IndexOf(", ", 10, StringComparison.Ordinal);
                if (end <= 10)
                    return false;
                for (int i = 10; i < end; i++)
                    if (!char.IsAsciiLetterOrDigit(line[i]))
                        return false;
                kind = line.Substring(10, end - 10);
                restStart = end + 2;
                return true;
            }

            void Add(uint handle, string kind, string rest)
            {
                // rest = <name>, "<hex children separated by spaces>"
                string name = rest;
                _childStart.Add(_children.Count);
                int close = rest.Length - 1;
                if (close > 0 && rest[close] == '"')
                {
                    int open = close - 1;
                    while (open >= 0 && (char.IsAsciiHexDigitLower(rest[open]) || rest[open] == ' '))
                        open--;
                    if (open >= 2 && rest[open] == '"' && rest[open - 1] == ' ' && rest[open - 2] == ',')
                    {
                        var list = rest.AsSpan(open + 1, close - open - 1);
                        while (!list.IsEmpty)
                        {
                            int space = list.IndexOf(' ');
                            var token = space < 0 ? list : list[..space];
                            list = space < 0 ? default : list[(space + 1)..];
                            if (token.Length > 0 && uint.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint child))
                                _children.Add(child);
                        }
                        name = rest.Substring(0, open - 2);
                    }
                }

                Handles.Add(handle);
                Kinds.Add(kind);
                Previews.Add(kind switch
                {
                    "ScopeDefinition" or "NamespaceDefinition" => Unquote(name),
                    "TypeDefinition" => StripHandleSuffix(Unquote(name)),
                    "ConstantStringValue" => Preview(name),
                    _ => null,
                });
            }

            static string Unquote(string s)
                => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

            static string StripHandleSuffix(string s)
            {
                // "<Module> (74019435)"
                int open = s.LastIndexOf(" (", StringComparison.Ordinal);
                if (open <= 0 || !s.EndsWith(')'))
                    return s;
                var digits = s.AsSpan(open + 2, s.Length - open - 3);
                return digits.Length > 0 && !digits.ContainsAnyExcept("0123456789abcdefABCDEF") ? s[..open] : s;
            }

            static string Preview(string s)
            {
                s = s.Replace("\r", "").Replace('\n', ' ');
                return s.Length <= PreviewLength ? s : s[..PreviewLength] + "...";
            }

            ReadOnlySpan<uint> ChildrenOf(int index)
            {
                int start = _childStart[index];
                int end = index + 1 < Count ? _childStart[index + 1] : _children.Count;
                return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_children).Slice(start, end - start);
            }

            int Resolve(uint handle, int from)
            {
                if (_duplicates.TryGetValue(handle, out var candidates))
                {
                    // A wrapped 24-bit offset is ambiguous; records are emitted near their referrers,
                    // so prefer the nearest candidate at or after the referencing record.
                    int best = -1;
                    foreach (int c in candidates)
                        if (Offsets[c] >= Offsets[from] && (best < 0 || Offsets[c] < Offsets[best]))
                            best = c;
                    return best >= 0 ? best : candidates[^1];
                }
                return _byHandle.TryGetValue(handle, out int index) ? index : -1;
            }

            public Owner[] ComputeOwners()
            {
                _byHandle = new Dictionary<uint, int>(Count);
                _duplicates = new Dictionary<uint, List<int>>();
                for (int i = 0; i < Count; i++)
                {
                    if (_byHandle.TryAdd(Handles[i], i))
                        continue;
                    if (!_duplicates.TryGetValue(Handles[i], out var list))
                        _duplicates[Handles[i]] = list = new List<int> { _byHandle[Handles[i]] };
                    list.Add(i);
                }

                var owners = new Owner[Count];
                var queue = new Queue<int>();
                for (int i = 0; i < Count; i++)
                {
                    if (Kinds[i] != "ScopeDefinition")
                        continue;
                    owners[i] = new Owner(Previews[i], "", null);
                    queue.Enqueue(i);
                }

                while (queue.Count > 0)
                {
                    int parent = queue.Dequeue();
                    Owner owner = owners[parent];
                    foreach (uint child in ChildrenOf(parent))
                    {
                        int index = Resolve(child, parent);
                        if (index < 0 || owners[index] != null)
                            continue;
                        owners[index] = Kinds[index] switch
                        {
                            "NamespaceDefinition" => owner with { Namespace = Previews[index].Length > 0 ? Previews[index] : owner.Namespace, Type = null },
                            "TypeDefinition" => owner with { Type = Previews[index] },
                            _ => owner,
                        };
                        queue.Enqueue(index);
                    }
                }
                return owners;
            }
        }
    }
}
