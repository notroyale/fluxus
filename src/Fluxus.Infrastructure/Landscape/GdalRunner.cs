using System.Diagnostics;

namespace Fluxus.Infrastructure.Landscape;

public sealed class GdalRunner(string? binaryDirectory = null)
{
    public async Task<string> RunAsync(string tool, IEnumerable<string> args, CancellationToken ct)
    {
        if (tool is not ("gdalinfo" or "gdalwarp" or "gdal_translate" or "gdal_viewshed"))
            throw new ArgumentException("Unsupported terrain tool.");
        var executable = string.IsNullOrWhiteSpace(binaryDirectory) ? tool : Path.Combine(binaryDirectory, tool);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new LandscapeInputException("gdal_not_installed"); }
        var stdout = DrainAsync(process.StandardOutput, ct);
        var stderr = DrainAsync(process.StandardError, ct);
        try { await process.WaitForExitAsync(ct); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await stdout;
        _ = await stderr; // Raw tool errors/paths never enter catalogue responses.
        if (process.ExitCode != 0) throw new LandscapeInputException("gdal_processing_failed");
        return output;
    }
    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var result = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            if (result.Length < 1_000_000) result.Append(buffer, 0, Math.Min(count, 1_000_000 - result.Length));
        return result.ToString();
    }
}
