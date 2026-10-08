"""Render an offline terrain PNG and sidecar for the report; never calculate scores."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import uuid

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.colors import LightSource, LinearSegmentedColormap
import numpy as np

parser = argparse.ArgumentParser()
parser.add_argument('mesh', help='NPZ from export-terrain-mesh.py')
parser.add_argument('pilot', help='Canonical parcel pilot-area.json')
parser.add_argument('output', help='New output folder beneath configured preview root')
parser.add_argument('--exaggeration', type=float, default=20)
parser.add_argument('--year-from', type=int, required=True)
parser.add_argument('--year-to', type=int, required=True)
parser.add_argument('--attribution', required=True)
args = parser.parse_args()
if not 1 <= args.exaggeration <= 50 or not 2000 <= args.year_from <= args.year_to <= 2100:
    raise ValueError('Invalid exaggeration/years')
pilot = json.loads(Path(args.pilot).read_text(encoding='utf-8-sig'))
parcel_id = str(uuid.UUID(pilot['parcelId']))
if not re.fullmatch('[0-9a-f]{32}', pilot['geometryHash']):
    raise ValueError('Invalid geometry hash')
if Path(args.mesh).stat().st_size > 16 * 1024 * 1024:
    raise ValueError('Mesh budget exceeded')
data = np.load(args.mesh, allow_pickle=False)
x, y, ground = data['x'], data['y'], data['ground']
if ground.shape != (len(y), len(x)) or max(ground.shape) > 360 or not np.isfinite(ground).any():
    raise ValueError('Invalid mesh')
output = Path(args.output)
output.mkdir(parents=True, exist_ok=False)
cx, cy = (x.min()+x.max())/2, (y.min()+y.max())/2
X, Y = np.meshgrid((x-cx)/1000, (y-cy)/1000)
base = float(np.nanmin(ground))
Z = (ground-base)*args.exaggeration/1000
cmap = LinearSegmentedColormap.from_list('relief', ['#517965', '#acb77e', '#e1d6ae'])
lo, hi = np.nanpercentile(ground, [2, 98])
colors = cmap(np.clip((ground-lo)/max(.001, hi-lo), 0, 1))
light = LightSource(315, 40).hillshade(np.ma.masked_invalid(ground), vert_exag=args.exaggeration,
                                    dx=float(data['resolution_m']), dy=float(data['resolution_m']))
colors[:, :, :3] *= .65 + .35*np.ma.filled(light[:, :, None], .6)
fig = plt.figure(figsize=(12, 8), dpi=120, facecolor='#f5f6f0')
ax = fig.add_axes([.01, .15, .98, .78], projection='3d', computed_zorder=False)
ax.set_facecolor('#f5f6f0')
surface = ax.plot_surface(X, Y, Z, facecolors=colors, rstride=1, cstride=1,
                          linewidth=0, antialiased=False, shade=False, zorder=1)
surface.set_clip_on(False)
# Densify parcel rings and drape each segment over a valid mesh cell. Invalid cells stay gaps.
for polygon in pilot['parcelGeometry']['coordinates']:
    for ring in polygon:
        points = []
        for start, end in zip(ring[:-1], ring[1:]):
            count = max(2, int(np.linalg.norm(np.subtract(end, start))/5))
            points.extend(np.linspace(start, end, count, endpoint=False))
        points.append(ring[-1])
        points = np.asarray(points)
        cols = np.abs(points[:, 0, None]-x).argmin(axis=1)
        rows = np.abs(points[:, 1, None]-y).argmin(axis=1)
        heights = Z[rows, cols] + .003
        ax.plot((points[:, 0]-cx)/1000, (points[:, 1]-cy)/1000, heights,
                color='#ea314c', linewidth=2.5, zorder=5)
ax.view_init(elev=38, azim=-125)
ax.set_proj_type('ortho')
ax.set_box_aspect((np.ptp(x)/1000, np.ptp(y)/1000,
                   max(.02, float(np.nanmax(Z))-float(np.nanmin(Z)))), zoom=1.2)
ax.set_axis_off()
fig.text(.055, .94, '3D terrain · selected parcel', fontsize=22, weight='bold', color='#173f32')
fig.text(.055, .902, f'Real LiDAR ground · red outline: selected parcel · vertical exaggeration {args.exaggeration:g}×',
         fontsize=12, color='#4e6455')
fig.text(.055, .107, 'Colors show relative elevation. Blank patches retain missing source data.', fontsize=11, color='#4e6455')
fig.text(.055, .075, f'Sampled surveys {args.year_from}–{args.year_to} · height reference unverified · inspection preview',
         fontsize=11, color='#4e6455')
fig.text(.055, .038, args.attribution, fontsize=9, color='#4e6455')
image = output/'terrain.png'
fig.savefig(image, facecolor=fig.get_facecolor())
plt.close(fig)
manifest = {'parcelId': parcel_id, 'geometryHash': pilot['geometryHash'],
            'imageSha256': hashlib.sha256(image.read_bytes()).hexdigest(),
            'width': 1440, 'height': 960, 'verticalExaggeration': args.exaggeration,
            'meshResolutionM': float(data['resolution_m']),
            'surveyYearFrom': args.year_from, 'surveyYearTo': args.year_to,
            'heightReferenceVerified': False, 'attribution': args.attribution,
            'groundSha256': str(data['ground_sha256']),
            'renderVersion': 'matplotlib-terrain-v1', 'matplotlibVersion': matplotlib.__version__}
(output/'preview.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print(image)
