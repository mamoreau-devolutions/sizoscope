using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Sizoscope.Tests.Fixtures;

namespace Sizoscope.Tests.Stress;

internal static class StressProgram
{
    public static int Main(string[] args) => Execute(args, Console.Out, Console.Error);

    internal static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var options = Parse(args); // No filesystem activity before all explicit arguments validate.
            using var temp = new OwnedTempDirectory(options.Temp);
            var results = new OwnedTempDirectory(options.Output);
            try
            {
                var clock = Stopwatch.StartNew();
                string graph = temp.FilePath("generated.dgml");
                using (var stream = new FileStream(graph, FileMode.CreateNew, FileAccess.Write))
                    DgmlFixtureBuilder.Write(stream, StreamingGraphGenerator.Nodes(options.Nodes),
                        StreamingGraphGenerator.Links(options.Nodes, options.Edges, options.Seed));
                string directory = temp.FilePath("index");
                using (var stream = File.OpenRead(graph))
                    DependencyGraphIndex.Build(stream, directory, sortBufferRecords: options.Buffer);
                Require(new FileInfo(Path.Combine(directory, "nodes.bin")).Length == 32L * options.Nodes, "Node count mismatch.");
                Require(new FileInfo(Path.Combine(directory, "edges.bin")).Length == 32L * options.Edges, "Edge count mismatch.");
                long verifiedEdges = 0;
                using (var index = new DependencyGraphIndex(directory))
                {
                    foreach (int ordinal in new[] { 0, options.Nodes - 1 }.Distinct())
                    {
                        int id = checked(ordinal * 17);
                        string name = "label-" + ordinal % 11;
                        Require(index.GetNode(id) == new DependencyGraphNode(id, name), "Node identity mismatch.");
                        // Reversed declarations: lowest ordinal with this label wins, not largest ID.
                        Require(index.FindNode(name) == new DependencyGraphNode(ordinal % 11 * 17, name), "Last-label mismatch.");
                        verifiedEdges += VerifyIncoming(index, ordinal, options);
                    }
                }
                clock.Stop();
                long logicalBytes = 0;
                long? allocatedBytes = OperatingSystem.IsWindows() ? 0L : null;
                foreach (string file in Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories))
                {
                    logicalBytes += new FileInfo(file).Length;
                    if (allocatedBytes.HasValue)
                    {
                        using var stream = File.OpenRead(file);
                        allocatedBytes += SparseFileSupport.AllocatedBytes(stream);
                    }
                }
                using var process = Process.GetCurrentProcess();
                var report = new
                {
                    options.Nodes, options.Edges, options.Seed, SortBufferRecords = options.Buffer,
                    SentinelEdgesVerified = verifiedEdges, LogicalBytes = logicalBytes, AllocatedBytes = allocatedBytes,
                    ElapsedMilliseconds = clock.ElapsedMilliseconds, ManagedBytes = GC.GetTotalMemory(false),
                    PeakWorkingSetBytes = process.PeakWorkingSet64
                };
                string reportPath = results.FilePath("stress-result.json");
                using (var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write))
                    JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
                output.WriteLine(JsonSerializer.Serialize(report));
                output.WriteLine(options.KeepOutput ? $"Preserved report: {reportPath}" : "Owned report removed (use --keep-output to preserve).");
                return 0;
            }
            finally
            {
                if (!options.KeepOutput) results.Dispose();
            }
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine("Required: --stress --temp <absolute root> --output <absolute root> --nodes <count> --edges <count> --seed <nonnegative int>");
            error.WriteLine("Optional: --sort-buffer <1..1048576> --keep-output");
            return 2;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.ToString());
            return 1; // Failure is observable; no successful/ignored stress Fact wraps this entry point.
        }
    }

    private static long VerifyIncoming(DependencyGraphIndex index, int ordinal, Options options)
    {
        using var expected = StreamingGraphGenerator.ExpectedIncoming(ordinal, options.Nodes, options.Edges, options.Seed).GetEnumerator();
        bool more = expected.MoveNext();
        long offset = 0;
        while (true)
        {
            var page = index.ReadIncoming(checked(ordinal * 17), offset, 137);
            Require(page.Edges.Count <= 137, "Page capacity exceeded.");
            foreach (var actual in page.Edges)
            {
                Require(more, "An extra dependency was returned.");
                Require((actual.Node.Id, actual.Node.Name, actual.Reason) == expected.Current, "Dependency order/value mismatch.");
                more = expected.MoveNext();
            }
            offset += page.Edges.Count;
            Require(page.NextOffset == (more ? (long?)offset : null), "Continuation mismatch.");
            if (!more) return offset;
            Require(page.Edges.Count > 0, "Pagination did not advance.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record Options(string Temp, string Output, int Nodes, long Edges, int Seed, int Buffer, bool KeepOutput);

    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool stress = false, keep = false;
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (key == "--stress")
            {
                if (stress) throw new ArgumentException("Duplicate --stress.");
                stress = true;
            }
            else if (key == "--keep-output")
            {
                if (keep) throw new ArgumentException("Duplicate --keep-output.");
                keep = true;
            }
            else
            {
                if (key is not ("--temp" or "--output" or "--nodes" or "--edges" or "--seed" or "--sort-buffer")
                    || ++i == args.Length || !values.TryAdd(key, args[i]))
                    throw new ArgumentException("Unknown, duplicate or missing-value argument: " + key);
            }
        }
        string Value(string name) => values.TryGetValue(name, out string value) ? value
            : throw new ArgumentException("Missing required argument: " + name);
        long Number(string name) => long.TryParse(Value(name), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            ? value : throw new ArgumentException("Invalid nonnegative integer: " + name);
        if (!stress) throw new ArgumentException("Explicit --stress opt-in is required.");
        string temp = Value("--temp"), output = Value("--output");
        if (!Path.IsPathFullyQualified(temp) || !Path.IsPathFullyQualified(output))
            throw new ArgumentException("Temporary and output roots must be absolute.");
        long nodes = Number("--nodes"), edges = Number("--edges"), seed = Number("--seed");
        long buffer = values.ContainsKey("--sort-buffer") ? Number("--sort-buffer") : 262144;
        if (nodes < 1 || nodes > int.MaxValue / 17L + 1 || edges > long.MaxValue / 32
            || seed > int.MaxValue || buffer is < 1 or > 1048576)
            throw new ArgumentException("Nodes/edges/seed/sort-buffer exceed the supported bounds.");
        return new Options(Path.GetFullPath(temp), Path.GetFullPath(output), (int)nodes, edges, (int)seed, (int)buffer, keep);
    }
}
