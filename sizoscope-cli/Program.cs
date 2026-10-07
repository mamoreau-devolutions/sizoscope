using sizoscope;

namespace SizoscopeCli
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            string outputFile = null;
            var filePaths = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    outputFile = args[++i];
                else if (args[i].StartsWith("-"))
                {
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    PrintUsage();
                    return 1;
                }
                else
                    filePaths.Add(args[i]);
            }

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

            MstatData right;
            using (var ms = resolvedRight.OpenMstat())
                right = MstatData.Read(ms, resolvedRight.MstatLength);

            (MstatData leftDiff, MstatData rightDiff) = MstatData.Diff(left, right);
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

        static void PrintUsage()
        {
            Console.Error.WriteLine("Usage: sizoscope-cli <baseline.mstat|.zip> <compare.mstat|.zip> [--output <file>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Compares two .mstat files (or .zip archives containing .mstat files)");
            Console.Error.WriteLine("and prints a summary of size differences.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --output <file>  Write output to a file instead of stdout");
        }

        static string AsFileSize(long size) => FileSizeFormatter.Format(size);

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
            if (rightDiff.BlobsSize > 0)
                writer.WriteLine($"  New blobs: +{AsFileSize(rightDiff.BlobsSize)}");
            if (leftDiff.BlobsSize > 0)
                writer.WriteLine($"  Removed blobs: -{AsFileSize(leftDiff.BlobsSize)}");

            if (left.DeduplicatedAliasCount != right.DeduplicatedAliasCount
                || left.DeduplicatedSavedSize != right.DeduplicatedSavedSize)
            {
                writer.WriteLine();
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
            }
        }
    }
}
