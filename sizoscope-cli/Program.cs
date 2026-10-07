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
                PrintDiff(writer, left, right, leftDiff, rightDiff, diffSize);
            }
            else
            {
                PrintDiff(Console.Out, left, right, leftDiff, rightDiff, diffSize);
            }

            return 0;
        }

        static void PrintUsage() => PrintUsage(Console.Error);

        static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("Usage: sizoscope-cli <file.mstat|.zip> [--top <n>] [--output <file>]");
            writer.WriteLine("       sizoscope-cli <baseline.mstat|.zip> <compare.mstat|.zip> [--output <file>]");
            writer.WriteLine("       sizoscope-cli why <graph.dgml.xml> <query> [options]   (see 'why --help')");
            writer.WriteLine();
            writer.WriteLine("With one file, prints the total size, all assemblies ranked by size, and the");
            writer.WriteLine("largest types. With two files, prints a summary of size differences.");
            writer.WriteLine("'why' explains why nodes are in an ILC dependency graph, without needing an MSTAT.");
            writer.WriteLine();
            writer.WriteLine("Options:");
            writer.WriteLine("  --top <n>        Number of largest types to list in summary mode (default 25)");
            writer.WriteLine("  --output <file>  Write output to a file instead of stdout");
            writer.WriteLine("  --help           Show this help");
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
            foreach (var assembly in data.GetScopes())
            {
                if (assembly.AggregateSize == 0)
                    continue;

                assemblies.Add((assembly.DisplayName, assembly.AggregateSize));
                if (top > 0)
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.AggregateSize > 0)
                            types.Add((type.ToString(), assembly.DisplayName, type.AggregateSize));
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
                foreach (var (name, assembly, size) in types.Take(top))
                    writer.WriteLine($"  {AsFileSize(size),-12} {Percent(size, data.Size),7} [{assembly}] {name}");
                writer.WriteLine();
            }

            if (data.UnownedFrozenObjectSize > 0)
                writer.WriteLine($"  Unowned frozen objects: {AsFileSize(data.UnownedFrozenObjectSize)}");
            if (data.BlobsSize > 0)
                writer.WriteLine($"  Blobs: {AsFileSize(data.BlobsSize)}");

            if (data.DeduplicatedAliasCount > 0)
            {
                writer.WriteLine();
                writer.WriteLine("=== Method body folding (identical bodies; not part of the total) ===");
                writer.WriteLine($"  {data.DeduplicatedAliasCount:N0} methods folded into {data.DeduplicatedBodyCount:N0} kept bodies, estimated {AsFileSize(data.DeduplicatedSavedSize)} saved");
                int foldTop = Math.Min(top, 10);
                if (foldTop > 0)
                {
                    foreach (var body in data.GetDeduplicatedBodies().OrderByDescending(b => b.SavedSize).Take(foldTop))
                        writer.WriteLine($"  {AsFileSize(body.SavedSize),-12} {body.AliasCount,8:N0} x {AsFileSize(body.Size),-10} {body.Name}");
                }
            }
        }

        static string Percent(long part, long total)
            => total <= 0 ? "-" : (100.0 * part / total).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

        static string AsFileSize(long size) => FileSizeFormatter.Format(size);

        static string SignedFileSize(long size) => size switch
        {
            > 0 => "+" + AsFileSize(size),
            < 0 => "-" + AsFileSize(-size),
            _ => "0 B",
        };

        static void PrintDiff(
            TextWriter writer,
            MstatData left,
            MstatData right,
            MstatData leftDiff,
            MstatData rightDiff,
            long totalDiffSize)
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
                grown.Add((asm.DisplayName, asm.AggregateSize));
            }

            // Collect assemblies from the left diff (removed/shrunk items)
            var shrunk = new List<(string Name, long Size)>();
            foreach (var asm in leftDiff.GetScopes())
            {
                if (asm.Name == "System.Private.CompilerGenerated")
                    continue;
                if (asm.AggregateSize == 0)
                    continue;
                shrunk.Add((asm.DisplayName, asm.AggregateSize));
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
            long frozenNet = right.UnownedFrozenObjectSize - left.UnownedFrozenObjectSize;
            long frozenPresence = rightDiff.UnownedFrozenObjectSize - leftDiff.UnownedFrozenObjectSize;
            long retainedFrozen = frozenNet - frozenPresence;
            if (frozenNet != 0)
                writer.WriteLine($"  Unowned frozen objects: {AsFileSize(left.UnownedFrozenObjectSize)} -> {AsFileSize(right.UnownedFrozenObjectSize)} ({SignedFileSize(frozenNet)})");
            if (retainedFrozen != 0)
                writer.WriteLine($"  Retained frozen objects: {SignedFileSize(retainedFrozen)}");
            if (rightDiff.BlobsSize > 0)
                writer.WriteLine($"  New blobs: +{AsFileSize(rightDiff.BlobsSize)}");
            if (leftDiff.BlobsSize > 0)
                writer.WriteLine($"  Removed blobs: -{AsFileSize(leftDiff.BlobsSize)}");
            if (left.BlobsSize != right.BlobsSize)
                writer.WriteLine($"  Blobs: {AsFileSize(left.BlobsSize)} -> {AsFileSize(right.BlobsSize)} ({SignedFileSize(right.BlobsSize - left.BlobsSize)})");
            if (rightDiff.UnownedFrozenObjectSize > 0 || leftDiff.UnownedFrozenObjectSize > 0
                || frozenNet != 0 || rightDiff.BlobsSize > 0 || leftDiff.BlobsSize > 0 || left.BlobsSize != right.BlobsSize)
                writer.WriteLine();

            // Presence-only sections omit size changes to identities retained on both sides.
            var presence = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (name, size) in grown)
                presence[name] = presence.GetValueOrDefault(name) + size;
            foreach (var (name, size) in shrunk)
                presence[name] = presence.GetValueOrDefault(name) - size;

            var net = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var assembly in right.GetScopes())
                net[assembly.DisplayName] = net.GetValueOrDefault(assembly.DisplayName) + assembly.AggregateSize;
            foreach (var assembly in left.GetScopes())
                net[assembly.DisplayName] = net.GetValueOrDefault(assembly.DisplayName) - assembly.AggregateSize;

            var retained = net
                .Select(kv => (Name: kv.Key, Net: kv.Value, Retained: kv.Value - presence.GetValueOrDefault(kv.Key)))
                .Where(x => x.Retained != 0)
                .OrderBy(x => x.Net)
                .ToList();
            if (retained.Count > 0)
            {
                writer.WriteLine("=== Net change by assembly (includes size changes of items present in both inputs) ===");
                writer.WriteLine($"  {"Net",-12} {"Retained items",-14} Assembly");
                foreach (var (name, netSize, retainedSize) in retained)
                    writer.WriteLine($"  {SignedFileSize(netSize),-12} {SignedFileSize(retainedSize),-14} {name}");
                writer.WriteLine();
            }

            if (left.DeduplicatedAliasCount != right.DeduplicatedAliasCount
                || left.DeduplicatedSavedSize != right.DeduplicatedSavedSize)
            {
                writer.WriteLine(
                    "=== Method body folding (not part of the total) ===");
                writer.WriteLine(
                    $"  Folded methods: {left.DeduplicatedAliasCount:N0} -> {right.DeduplicatedAliasCount:N0}");
                writer.WriteLine(
                    $"  Estimated folding savings: {AsFileSize(left.DeduplicatedSavedSize)} -> {AsFileSize(right.DeduplicatedSavedSize)}");
                if ((left.DeduplicatedAliasCount == 0)
                    != (right.DeduplicatedAliasCount == 0))
                {
                    writer.WriteLine(
                        "  Note: one input records no folded methods; ILC may have run without method body folding data (e.g. different build options).");
                }
                writer.WriteLine();
            }
        }
    }
}
