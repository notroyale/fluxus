"""Export a bounded mesh from a prepared EPSG:3346 ground raster (GDAL runtime)."""
import argparse
import hashlib
from pathlib import Path
import numpy as np
from osgeo import gdal

parser = argparse.ArgumentParser()
parser.add_argument('ground')
parser.add_argument('output')
args = parser.parse_args()
gdal.UseExceptions()
ds = gdal.Open(args.ground)
if ds.RasterCount != 1 or ds.RasterXSize * ds.RasterYSize > 25_000_000:
    raise ValueError('Unsupported raster size/bands')
if ds.GetSpatialRef().GetAuthorityCode(None) != '3346':
    raise ValueError('Expected EPSG:3346')
t = ds.GetGeoTransform()
if t[1] <= 0 or t[5] >= 0 or t[2] != 0 or t[4] != 0 or t[1] > 2 or -t[5] > 2:
    raise ValueError('Expected north-up ground raster at <=2 m')
band = ds.GetRasterBand(1)
z = band.ReadAsArray()
valid = np.isfinite(z) & (z != band.GetNoDataValue())
step = max(1, int(np.ceil(max(z.shape) / 360)))
rows, cols = z.shape[0] // step, z.shape[1] // step
blocks = z[:rows*step, :cols*step].reshape(rows, step, cols, step)
complete = valid[:rows*step, :cols*step].reshape(rows, step, cols, step).all(axis=(1, 3))
mesh = blocks.mean(axis=(1, 3)).astype('float32')
mesh[~complete] = np.nan  # No invented surfaces over missing input cells.
x = t[0] + (np.arange(cols) + .5) * step * t[1]
y = t[3] + (np.arange(rows) + .5) * step * t[5]
with Path(args.ground).open('rb') as source:
    source_hash = hashlib.file_digest(source, 'sha256').hexdigest()
with Path(args.output).open('xb') as target:
    np.savez_compressed(target, x=x, y=y, ground=mesh, ground_sha256=source_hash,
                        resolution_m=step*t[1])
print(f'Exported {cols} x {rows} mesh, {step*t[1]:g} m; gaps preserved.')
