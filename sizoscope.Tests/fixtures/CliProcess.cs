using System.Diagnostics;

namespace Sizoscope.Tests.Fixtures;

internal static class CliProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> Run(params string[] arguments)
    {
        // Keep existing callers append-only while routing every child through an owned cache.
        using var temp = new OwnedTempDirectory(System.IO.Path.GetTempPath());
        return await RunWithCache(temp.FilePath("cache"), arguments);
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunWithCache(
        string absoluteCacheRoot, params string[] arguments)
    {
        if (!System.IO.Path.IsPathFullyQualified(absoluteCacheRoot))
            throw new ArgumentException("An absolute caller-owned cache path is required.", nameof(absoluteCacheRoot));
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent.Name;
        string dll = System.IO.Path.Combine(root, "sizoscope-cli", "bin", configuration, "net8.0", "sizoscope-cli.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException("The CLI must be built in the current test configuration.", dll);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(dll);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        start.Environment["SIZOSCOPE_GRAPH_CACHE"] = System.IO.Path.GetFullPath(absoluteCacheRoot);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The owned CLI process exceeded 30 seconds.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
