using System.Diagnostics;

namespace DetailFrame.App.Tests;

internal static class PythonTestTools
{
    public static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DetailFrame.sln")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Could not locate the DetailFrame repository.");
        return Path.Combine(dir.FullName, relative);
    }

    public static string Python() => RequirePython(RepoFile(@"worker\.venv\Scripts\python.exe"));

    public static string RequirePython(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Python integration tests require {path} and the worker dev dependencies. See README.md.");
        return path;
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Python())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = RepoFile("worker"),
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture Python.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
