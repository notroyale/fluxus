using System.Globalization;

namespace Fluxus.Infrastructure.Landscape;

public sealed record AsciiGrid(int Width, int Height, double MinX, double MinY, double CellSize,
    double NoData, double[] Values)
{
    public double MaxY => MinY + Height * CellSize;
    public (double X, double Y) Center(int index) =>
        (MinX + (index % Width + .5) * CellSize, MaxY - (index / Width + .5) * CellSize);
    public int Index(double x, double y)
    {
        var col = (int)Math.Floor((x - MinX) / CellSize);
        var row = (int)Math.Floor((MaxY - y) / CellSize);
        return col < 0 || col >= Width || row < 0 || row >= Height ? -1 : row * Width + col;
    }
    public bool Valid(int index) => index >= 0 && index < Values.Length &&
        double.IsFinite(Values[index]) && Values[index] != NoData;
    public bool CompleteDisk(double x, double y, double radius)
    {
        if (x - radius < MinX || x + radius > MinX + Width * CellSize ||
            y - radius < MinY || y + radius > MaxY) return false;
        var squared = radius * radius;
        for (var i = 0; i < Values.Length; i++)
        {
            var p = Center(i);
            if (Math.Pow(p.X - x, 2) + Math.Pow(p.Y - y, 2) <= squared && !Valid(i)) return false;
        }
        return true;
    }

    public static async Task<AsciiGrid> ReadAsync(string path, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        var header = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 6; i++)
        {
            var line = await reader.ReadLineAsync(ct) ?? throw new LandscapeInputException("invalid_grid");
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || !double.TryParse(fields[1], CultureInfo.InvariantCulture, out var value))
                throw new LandscapeInputException("invalid_grid");
            header.Add(fields[0], value);
        }
        if (!header.TryGetValue("ncols", out var cols) || !header.TryGetValue("nrows", out var rows) ||
            !header.TryGetValue("cellsize", out var cell) || !header.TryGetValue("NODATA_value", out var nodata) ||
            !header.Values.All(double.IsFinite) || cols is < 1 or > 4000 || rows is < 1 or > 4000 ||
            cols != Math.Truncate(cols) || rows != Math.Truncate(rows) || cols * rows > 4_000_000 || cell <= 0)
            throw new LandscapeInputException("grid_budget_exceeded");
        var x = header.TryGetValue("xllcorner", out var xc) ? xc : header.GetValueOrDefault("xllcenter", double.NaN) - cell / 2;
        var y = header.TryGetValue("yllcorner", out var yc) ? yc : header.GetValueOrDefault("yllcenter", double.NaN) - cell / 2;
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new LandscapeInputException("invalid_grid");
        var values = new double[(int)(cols * rows)];
        var n = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
            foreach (var field in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (n >= values.Length || !double.TryParse(field, CultureInfo.InvariantCulture, out values[n]))
                    throw new LandscapeInputException("invalid_grid");
                n++;
            }
        if (n != values.Length) throw new LandscapeInputException("invalid_grid");
        return new((int)cols, (int)rows, x, y, cell, nodata, values);
    }
}
