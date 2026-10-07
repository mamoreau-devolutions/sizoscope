using sizoscope;

namespace SizoscopeCli
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("why", StringComparison.OrdinalIgnoreCase) && !File.Exists(args[0]))
                return WhyCommand.Run(args[1..]);

            string outputFile = null;
            int top = 25;
            var filePaths = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    outputFile = args[++i];
                else if (string.Equals(args[i], "--top", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out top) || top < 0)
                    {
                        Console.Error.WriteLine($"Invalid --top value: {args[i]}");
                        PrintUsage();
                        return 1;
                    }
                }
                else if (args[i] is "--help" or "-h" or "-?" or "/?")
                {
                    PrintUsage(Console.Out);
                    return 0;
                }
                else if (args[i].StartsWith("-"))
                {
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    PrintUsage();
                    return 1;
                }
                else
                    filePaths.Add(args[i]);
            }

            if (filePaths.Count == 1)
                return RunSummary(filePaths[0], outputFile, top);

            if (filePaths.Count != 2)
            {
                PrintUsage();
                return filePaths.Count == 0 ? 0 : 1;
            }

            if (!File.Exists(filePaths[0]))
            {
                Console.Error.WriteLine($"File not found: {filePaths[0]}");
                return 1;
            }
            if (!File.Exists(filePaths[1]))
            {
                Console.Error.WriteLine($"File not found: {filePaths[1]}");
                return 1;
            }

            using var resolvedLeft = ResolvedFile.Open(filePaths[0]);
            using var resolvedRight = ResolvedFile.Open(filePaths[1]);

            MstatData left;
            using (var ms = resolvedLeft.OpenMstat())
                left = MstatData.Read(ms, resolvedLeft.MstatLength);
            using var leftLifetime = left;

            MstatData right;
            using (var ms = resolvedRight.OpenMstat())
                right = MstatData.Read(ms, resolvedRight.MstatLength);
            using var rightLifetime = right;

            (MstatData leftDiff, MstatData rightDiff) = MstatData.Diff(left, right);
            using var leftDiffLifetime = leftDiff;
            using var rightDiffLifetime = rightDiff;
            long diffSize = checked(right.Size - left.Size);

            if (outputFile != null)
            {
                using var writer = new StreamWriter(outputFile);
                PrintDiff(writer, leftDiff, rightDiff, diffSize);
            }
            else
            {
                PrintDiff(Console.Out, leftDiff, rightDiff, diffSize);
            }

            return 0;
        }

        static void PrintUsage() => PrintUsage(Console.Error);

        static void PrintUsage(TextWriter w)
        {
            w.WriteLine("Usage: sizoscope-cli <file.mstat|.zip> [--top <n>] [--output <file>]");
            w.WriteLine("       sizoscope-cli <baseline.mstat|.zip> <compare.mstat|.zip> [--output <file>]");
            w.WriteLine("       sizoscope-cli why <graph.dgml.xml> <query> [options]   (see 'why --help')");
            w.WriteLine();
            w.WriteLine("With one file, prints the total size, all assemblies ranked by size, and the");
            w.WriteLine("largest types. With two files, prints a summary of size differences.");
            w.WriteLine("'why' explains why nodes are in an ILC dependency graph, without needing an MSTAT.");
            w.WriteLine();
            w.WriteLine("Options:");
            w.WriteLine("  --top <n>        Number of largest types to list in summary mode (default 25)");
            w.WriteLine("  --output <file>  Write output to a file instead of stdout");
            w.WriteLine("  --help           Show this help");
        }

        static int RunSummary(string path, string outputFile, int top)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"File not found: {path}");
                return 1;
            }

            using var resolved = ResolvedFile.Open(path);
            MstatData data;
            using (var ms = resolved.OpenMstat())
                data = MstatData.Read(ms, resolved.MstatLength);
            using var lifetime = data;

            if (outputFile != null)
            {
                using var writer = new StreamWriter(outputFile);
                PrintSummary(writer, data, top);
            }
            else
            {
                PrintSummary(Console.Out, data, top);
            }
            return 0;
        }

        static void PrintSummary(TextWriter writer, MstatData data, int top)
        {
            writer.WriteLine($"Total accounted size: {AsFileSize(data.Size)}");
            writer.WriteLine();

            var assemblies = new List<(string Name, long Size)>();
            var types = new List<(string Name, string Assembly, long Size)>();
            foreach (var asm in data.GetScopes())
            {
                if (asm.AggregateSize == 0)
                    continue;
                assemblies.Add((asm.Name, asm.AggregateSize));
                if (top > 0)
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.AggregateSize > 0)
                            types.Add((type.ToString(), asm.Name, type.AggregateSize));
                    }
                }
            }

            assemblies.Sort((a, b) => b.Size.CompareTo(a.Size));
            writer.WriteLine("=== Assemblies ===");
            foreach (var (name, size) in assemblies)
                writer.WriteLine($"  {AsFileSize(size),-12} {Percent(size, data.Size),7} {name}");
            writer.WriteLine();

            if (top > 0 && types.Count > 0)
            {
                types.Sort((a, b) => b.Size.CompareTo(a.Size));
                writer.WriteLine($"=== Top {Math.Min(top, types.Count)} types ===");
                foreach (var (name, asm, size) in types.Take(top))
                    writer.WriteLine($"  {AsFileSize(size),-12} {Percent(size, data.Size),7} [{asm}] {name}");
                writer.WriteLine();
            }

            if (data.UnownedFrozenObjectSize > 0)
                writer.WriteLine($"  Unowned frozen objects: {AsFileSize(data.UnownedFrozenObjectSize)}");
            if (data.BlobsSize > 0)
                writer.WriteLine($"  Blobs: {AsFileSize(data.BlobsSize)}");
        }

        static string Percent(long part, long total)
            => total <= 0 ? "-" : (100.0 * part / total).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

        static string AsFileSize(long size) => FileSizeFormatter.Format(size);

        static void PrintDiff(TextWriter writer, MstatData leftDiff, MstatData rightDiff, long totalDiffSize)
        {
            writer.WriteLine($"Total accounted size difference: {AsFileSize(totalDiffSize)}");
            writer.WriteLine();

            // Collect assemblies from the right diff (new/grown items)
            var grown = new List<(string Name, long Size)>();
            foreach (var asm in rightDiff.GetScopes())
            {
                if (asm.Name == "System.Private.CompilerGenerated")
                    continue;
                if (asm.AggregateSize == 0)
                    continue;
                grown.Add((asm.Name, asm.AggregateSize));
            }

            // Collect assemblies from the left diff (removed/shrunk items)
            var shrunk = new List<(string Name, long Size)>();
            foreach (var asm in leftDiff.GetScopes())
            {
                if (asm.Name == "System.Private.CompilerGenerated")
                    continue;
                if (asm.AggregateSize == 0)
                    continue;
                shrunk.Add((asm.Name, asm.AggregateSize));
            }

            // Sort by absolute size descending
            grown.Sort((a, b) => b.Size.CompareTo(a.Size));
            shrunk.Sort((a, b) => b.Size.CompareTo(a.Size));

            if (grown.Count > 0)
            {
                writer.WriteLine("=== New / Grown ===");
                foreach (var (name, size) in grown)
                {
                    writer.WriteLine($"  +{AsFileSize(size),-12} {name}");
                }
                writer.WriteLine();
            }

            if (shrunk.Count > 0)
            {
                writer.WriteLine("=== Removed / Shrunk ===");
                foreach (var (name, size) in shrunk)
                {
                    writer.WriteLine($"  -{AsFileSize(size),-12} {name}");
                }
                writer.WriteLine();
            }

            // Also show frozen objects and blobs if they contribute
            if (rightDiff.UnownedFrozenObjectSize > 0)
                writer.WriteLine($"  New frozen objects: +{AsFileSize(rightDiff.UnownedFrozenObjectSize)}");
            if (leftDiff.UnownedFrozenObjectSize > 0)
                writer.WriteLine($"  Removed frozen objects: -{AsFileSize(leftDiff.UnownedFrozenObjectSize)}");
            if (rightDiff.BlobsSize > 0)
                writer.WriteLine($"  New blobs: +{AsFileSize(rightDiff.BlobsSize)}");
            if (leftDiff.BlobsSize > 0)
                writer.WriteLine($"  Removed blobs: -{AsFileSize(leftDiff.BlobsSize)}");
        }
    }
}
