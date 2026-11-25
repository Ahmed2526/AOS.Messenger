using System.Diagnostics;
using System.Text;

namespace AOS.Messenger.Tests;

internal static class NodeTestProcess
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "--input-type=module -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Node.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(WriteScriptAsync(), process.WaitForExitAsync(), stdout, stderr)
                .WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            throw new TimeoutException("Node test timed out.");
        }

        return (process.ExitCode, await stdout, await stderr);

        async Task WriteScriptAsync()
        {
            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();
        }
    }
}
