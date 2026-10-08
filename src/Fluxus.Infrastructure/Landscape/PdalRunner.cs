using System.Diagnostics;
using System.Text;

namespace Fluxus.Infrastructure.Landscape;

public sealed class PdalRunner(string? binaryDirectory = null)
{
    public async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(binaryDirectory) ? "pdal" : Path.Combine(binaryDirectory, "pdal"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new LandscapeInputException("pdal_not_installed"); }
        var stdout = DrainAsync(process.StandardOutput, ct);
        var stderr = DrainAsync(process.StandardError, ct);
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout;
        var toolError = await stderr;
        if (process.ExitCode != 0)
        {
            var error = new LandscapeInputException("pdal_processing_failed");
            error.Data["toolError"] = toolError; // Operator diagnostics only; never returned by catalogue endpoints.
            throw error;
        }
        return output;
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096]; var result = new StringBuilder(); var overflow = false; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (result.Length + count > 1_000_000) overflow = true;
            if (result.Length < 1_000_000) result.Append(buffer, 0, Math.Min(count, 1_000_000 - result.Length));
        }
        if (overflow) throw new LandscapeInputException("pdal_metadata_budget_exceeded");
        return result.ToString();
    }
}
