using System.Diagnostics;
using System.Globalization;
using System.Xml;

namespace SizoscopeCli
{
    internal static class WhyCommand
    {
        internal const int DefaultVisitLimit = 5_000_000;

        public static int Run(string[] args)
        {
            string graphPath = null, query = null, outputFile = null, cacheRoot = null;
            bool exact = false, rebuild = false;
            int limit = 20, incoming = 20, visitLimit = DefaultVisitLimit;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                bool hasValue = i + 1 < args.Length;
                if (arg is "--help" or "-h" or "-?" or "/?")
                {
                    PrintUsage(Console.Out);
                    return 0;
                }
                else if (arg.Equals("--exact", StringComparison.OrdinalIgnoreCase))
                    exact = true;
                else if (arg.Equals("--rebuild", StringComparison.OrdinalIgnoreCase))
                    rebuild = true;
                else if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) && hasValue)
                    outputFile = args[++i];
                else if (arg.Equals("--cache", StringComparison.OrdinalIgnoreCase) && hasValue)
                    cacheRoot = args[++i];
                else if (arg.Equals("--limit", StringComparison.OrdinalIgnoreCase) && hasValue)
                {
                    if (!TryParseCount(args[++i], 1, out limit))
                        return Fail($"Invalid --limit value: {args[i]}");
                }
                else if (arg.Equals("--incoming", StringComparison.OrdinalIgnoreCase) && hasValue)
                {
                    if (!TryParseCount(args[++i], 0, out incoming))
                        return Fail($"Invalid --incoming value: {args[i]}");
                }
                else if (arg.Equals("--max-visit", StringComparison.OrdinalIgnoreCase) && hasValue)
                {
                    if (!TryParseCount(args[++i], 1, out visitLimit))
                        return Fail($"Invalid --max-visit value: {args[i]}");
                }
                else if (arg.StartsWith("-", StringComparison.Ordinal) && !(graphPath != null && query == null))
                    return Fail($"Unknown option: {arg}");
                else if (graphPath == null)
                    graphPath = arg;
                else if (query == null)
                    query = arg;
                else
                    return Fail($"Unexpected argument: {arg}");
            }

            if (graphPath == null || string.IsNullOrEmpty(query))
                return Fail("Expected a DGML file and a node query.");
            if (!File.Exists(graphPath))
            {
                Console.Error.WriteLine($"File not found: {graphPath}");
                return 1;
            }

            try
            {
                using var store = OpenIndex(graphPath, rebuild, cacheRoot);
                if (outputFile != null)
                {
                    using var writer = new StreamWriter(outputFile);
                    return Explain(writer, store, query, exact, limit, incoming, visitLimit);
                }
                return Explain(Console.Out, store, query, exact, limit, incoming, visitLimit);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or XmlException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"Unable to analyze DGML or dependency graph cache: {ex.Message}");
                return 1;
            }
        }

        static bool TryParseCount(string value, int minimum, out int result)
            => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= minimum;

        static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            PrintUsage(Console.Error);
            return 1;
        }

        internal static void PrintUsage(TextWriter w)
        {
            w.WriteLine("Usage: sizoscope-cli why <graph.dgml.xml> <query> [options]");
            w.WriteLine();
            w.WriteLine("Explains why nodes are in an ILC dependency graph (scan or codegen DGML).");
            w.WriteLine("Works without an MSTAT, e.g. when ILC failed before writing one. The DGML is");
            w.WriteLine("indexed to a disk cache on first use; later queries reuse the cache.");
            w.WriteLine();
            w.WriteLine("<query> is a case-insensitive substring of the node label, an exact label");
            w.WriteLine("with --exact, or #<id> for a node ID. If one node matches, prints its direct");
            w.WriteLine("dependents and the shortest dependency chain from a root; otherwise lists matches.");
            w.WriteLine();
            w.WriteLine("Options:");
            w.WriteLine("  --exact           Match the full node label exactly (fast, uses the label index)");
            w.WriteLine("  --limit <n>       Maximum matches to list (default 20)");
            w.WriteLine("  --incoming <n>    Direct dependents to print (default 20, 0 to skip)");
            w.WriteLine($"  --max-visit <n>   Node budget for the root path search (default {DefaultVisitLimit:N0})");
            w.WriteLine("  --cache <dir>     Index cache directory (default %LOCALAPPDATA%\\Sizoscope\\DependencyGraphs");
            w.WriteLine("                    or SIZOSCOPE_GRAPH_CACHE)");
            w.WriteLine("  --rebuild         Rebuild the index even if a cached one exists");
            w.WriteLine("  --output <file>   Write output to a file instead of stdout");
        }

        static DependencyGraphIndex OpenIndex(string path, bool rebuild, string cacheRoot)
        {
            var stopwatch = Stopwatch.StartNew();
            long lastReport = -1;
            var progress = new SyncProgress(p =>
            {
                long elapsed = stopwatch.ElapsedMilliseconds;
                if (lastReport >= 0 && elapsed - lastReport < 5000 && p.Stage == "Reading XML")
                    return;
                lastReport = elapsed;
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[{stopwatch.Elapsed:hh\\:mm\\:ss}] {p.Stage}: {p.Nodes:N0} nodes, {p.Edges:N0} edges, {p.Bytes / (1024.0 * 1024 * 1024):0.00} GiB read"));
            });
            var input = new DependencyGraphInput(path);
            var store = DependencyGraphCache.OpenAsync(input, rebuild, CancellationToken.None, progress,
                cacheRoot == null ? null : Path.GetFullPath(cacheRoot)).GetAwaiter().GetResult();
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[{stopwatch.Elapsed:hh\\:mm\\:ss}] Index ready: {store.NodeCount:N0} nodes"));
            return store;
        }

        internal static int Explain(TextWriter writer, DependencyGraphIndex store, string query, bool exact,
            int limit, int incoming, int visitLimit)
        {
            IReadOnlyList<DependencyGraphNode> matches;
            if (query.Length > 1 && query[0] == '#' &&
                int.TryParse(query.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            {
                DependencyGraphNode node;
                try
                {
                    node = store.GetNode(id);
                }
                catch (InvalidDataException)
                {
                    writer.WriteLine($"No node with ID {id}.");
                    return 1;
                }
                matches = [node];
            }
            else if (exact)
            {
                var node = store.FindNode(query);
                matches = node == null ? [] : [node];
            }
            else
            {
                // Prefer an exact label over substring hits so a full name is unambiguous.
                var node = store.FindNode(query);
                matches = node != null ? [node] :
                    store.SearchNodes(name => name.Contains(query, StringComparison.OrdinalIgnoreCase), limit + 1);
            }

            if (matches.Count == 0)
            {
                writer.WriteLine($"No node matches '{query}'.");
                return 1;
            }
            if (matches.Count > 1)
            {
                writer.WriteLine(matches.Count > limit
                    ? $"More than {limit} nodes match '{query}'. First {limit}:"
                    : $"{matches.Count} nodes match '{query}':");
                foreach (var match in matches.Take(limit))
                    writer.WriteLine($"  #{match.Id} {match.Name}");
                writer.WriteLine();
                writer.WriteLine("Refine the query, or pass #<id> to explain one node.");
                return 0;
            }

            var target = matches[0];
            writer.WriteLine($"Node #{target.Id}: {target.Name}");
            writer.WriteLine();

            if (incoming > 0)
            {
                var page = store.ReadIncoming(target.Id, 0, Math.Min(incoming, 200));
                var edges = page.Edges.ToList();
                while (edges.Count < incoming && page.NextOffset is long next)
                {
                    page = store.ReadIncoming(target.Id, next, Math.Min(incoming - edges.Count, 200));
                    edges.AddRange(page.Edges);
                }
                writer.WriteLine(edges.Count == 0 ? "Direct dependents: none (this node is a root)" : "Direct dependents:");
                foreach (var edge in edges)
                    writer.WriteLine($"  #{edge.Node.Id} {edge.Node.Name}{FormatReason(edge.Reason)}");
                if (page.NextOffset != null)
                    writer.WriteLine("  ... more (raise --incoming)");
                writer.WriteLine();
            }

            var chain = FindRootPath(store, target.Id, visitLimit, out int visited);
            if (chain == null)
            {
                writer.WriteLine($"No root reached within {visited:N0} visited nodes (raise --max-visit).");
                return 0;
            }
            writer.WriteLine($"Shortest chain from a root ({chain.Count - 1} steps, {visited:N0} nodes visited):");
            for (int i = 0; i < chain.Count; i++)
            {
                var (node, reason) = chain[i];
                writer.WriteLine(i == 0
                    ? $"  #{node.Id} {node.Name}"
                    : $"{new string(' ', 2 + 2 * Math.Min(i, 20))}-> #{node.Id} {node.Name}{FormatReason(reason)}");
            }
            return 0;
        }

        static string FormatReason(string reason) => string.IsNullOrEmpty(reason) ? "" : $"  [{reason}]";

        // Breadth-first search over incoming edges; returns root-first (node, reason-it-depends-on-previous).
        internal static List<(DependencyGraphNode Node, string Reason)> FindRootPath(DependencyGraphIndex store, int targetId,
            int visitLimit, out int visited)
        {
            var parents = new Dictionary<int, (int Child, string Reason)> { [targetId] = (-1, null) };
            var queue = new Queue<int>();
            queue.Enqueue(targetId);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                long? offset = 0;
                bool hasIncoming = false;
                while (offset is long next)
                {
                    var page = store.ReadIncoming(current, next);
                    foreach (var edge in page.Edges)
                    {
                        hasIncoming = true;
                        if (parents.Count >= visitLimit)
                        {
                            visited = parents.Count;
                            return null;
                        }
                        if (parents.TryAdd(edge.Node.Id, (current, edge.Reason)))
                            queue.Enqueue(edge.Node.Id);
                    }
                    offset = page.NextOffset;
                }
                if (!hasIncoming)
                {
                    visited = parents.Count;
                    var chain = new List<(DependencyGraphNode, string)>();
                    string reason = null;
                    for (int id = current; id != -1;)
                    {
                        chain.Add((store.GetNode(id), reason));
                        (id, reason) = parents[id];
                    }
                    return chain;
                }
            }
            // Every reachable dependent is part of a cycle with no root.
            visited = parents.Count;
            return null;
        }

        private sealed class SyncProgress(Action<DependencyGraphProgress> report) : IProgress<DependencyGraphProgress>
        {
            public void Report(DependencyGraphProgress value) => report(value);
        }
    }
}
