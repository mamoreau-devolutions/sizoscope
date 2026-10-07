using System.Diagnostics;

namespace Sizoscope.Tests.Fixtures;

internal static class CliProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> Run(
        params string[] arguments)
    {
        string root = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string dll = Path.Combine(
            root,
            "sizoscope-cli",
            "bin",
            configuration,
            "net8.0",
            "sizoscope-cli.dll");

        if (!File.Exists(dll))
            throw new FileNotFoundException(
                "Build sizoscope-cli in the current test configuration before running this test.",
                dll);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(dll);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The CLI process did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await output, await error);
    }
}
