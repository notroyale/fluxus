"""Create a source-grounded pilot PDF from captured offer and preparation reports."""
import argparse
from datetime import datetime, timezone
import hashlib
from html import escape
import json
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import SimpleDocTemplate, Paragraph, Table, TableStyle, Image, PageBreak, Spacer

parser = argparse.ArgumentParser()
parser.add_argument('--pilot', required=True)
parser.add_argument('--auction', required=True)
parser.add_argument('--offer-id', required=True)
parser.add_argument('--terrain-quality', required=True)
parser.add_argument('--context', required=True)
parser.add_argument('--cover-quality', required=True)
parser.add_argument('--preparation', required=True)
parser.add_argument('--preview-directory', required=True)
parser.add_argument('--output', required=True)
parser.add_argument('--font-directory', default='C:/Windows/Fonts')
args = parser.parse_args()
def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))
pilot, auction = load(args.pilot), load(args.auction)
quality, context = load(args.terrain_quality), load(args.context)
cover, preparation = load(args.cover_quality), load(args.preparation)
preview_dir = Path(args.preview_directory)
preview = load(preview_dir/'preview.json')
offer = next(item for item in auction['items'] if item['card']['id'] == args.offer_id)
card = offer['card']
if card['resolutionStatus'] != 'resolved_exact' or card['landscape']['score'] is not None:
    raise ValueError('Expected an accepted, unscored pilot offer')
for evidence in (context, preview):
    if evidence['parcelId'] != pilot['parcelId'] or evidence['geometryHash'] != pilot['geometryHash']:
        raise ValueError('Mismatched parcel evidence')
if preparation['datumVerified'] or not preparation['previewOnly'] or preview['heightReferenceVerified']:
    raise ValueError('This report template describes an unverified inspection preview')
png = preview_dir/'terrain.png'
if hashlib.sha256(png.read_bytes()).hexdigest() != preview['imageSha256']:
    raise ValueError('Preview checksum mismatch')
if offer['cadastralNumber'] != pilot['cadastralNumber']:
    raise ValueError('Mismatched offer identifier')

pdfmetrics.registerFont(TTFont('Report', str(Path(args.font_directory)/'segoeui.ttf')))
pdfmetrics.registerFont(TTFont('ReportBold', str(Path(args.font_directory)/'segoeuib.ttf')))
ink, muted, green = colors.HexColor('#203d31'), colors.HexColor('#53665a'), colors.HexColor('#185d45')
styles = {
    'title': ParagraphStyle('title', fontName='ReportBold', fontSize=22, leading=28, textColor=ink, spaceAfter=8),
    'heading': ParagraphStyle('heading', fontName='ReportBold', fontSize=13, leading=18, textColor=green, spaceBefore=12, spaceAfter=7),
    'body': ParagraphStyle('body', fontName='Report', fontSize=9.5, leading=14, textColor=ink, spaceAfter=7),
    'small': ParagraphStyle('small', fontName='Report', fontSize=8, leading=11.5, textColor=muted, spaceAfter=6),
    'cell': ParagraphStyle('cell', fontName='Report', fontSize=9, leading=12, textColor=ink),
}
def p(text, kind='body'):
    return Paragraph(text, styles[kind])
def table(rows, widths=(163, 352)):
    result = Table([[p(escape(str(a)), 'cell'), p(escape(str(b)), 'cell')] for a, b in rows],
                   colWidths=list(widths), hAlign='LEFT')
    result.setStyle(TableStyle([
        ('VALIGN', (0, 0), (-1, -1), 'TOP'), ('LEFTPADDING', (0, 0), (-1, -1), 8),
        ('RIGHTPADDING', (0, 0), (-1, -1), 8), ('TOPPADDING', (0, 0), (-1, -1), 5),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 5),
        ('ROWBACKGROUNDS', (0, 0), (-1, -1), [colors.HexColor('#f0f5ed'), colors.white]),
        ('LINEBELOW', (0, 0), (-1, -1), .3, colors.HexColor('#dbe4da')),
    ]))
    return result
def stamp(value):
    return datetime.fromisoformat(value).strftime('%d %b %Y, %H:%M UTC%z')
now = datetime.now(timezone.utc)
ground, surface = quality['rasters']['ground'], quality['rasters']['surface']
story = [p('Preliminary landscape report', 'title'),
         p(f"{escape(card['address'])}<br/>Parcel {escape(pilot['cadastralNumber'])} | Report date {now:%d %B %Y}", 'small'),
         p('<b>INSPECTION ONLY - OVERALL SCORE NOT CALCULATED</b>'),
         p('Source data and a terrain preview are available. A complete landscape score remains pending because heights are unverified and local inputs have gaps.'),
         table([
             ('Auction / offer', f"{auction['number']} / sale of {card['share']} ownership"),
             ('Cadastral / unique number', f"{offer['cadastralNumber']} / {offer['uniqueNumber']}"),
             ('Whole parcel / offered area', f"{card['totalAreaHa']:g} ha / {card['soldAreaHa']:g} ha"),
             ('Captured starting price', f"EUR {auction['initialPriceEur']:,.0f} for the auction"),
             ('Listing observed', stamp(auction['observedAt'])),
             ('Scheduled auction end', stamp(auction['endsAt'])),
         ]),
         Spacer(1, 9),
         p('The scheduled end predates this report. The stored status was "running" when captured; current availability and auction outcome have not been refreshed. The red outline represents the whole cadastral parcel, not a separately located ownership share.', 'small'),
         Image(str(png), width=470, height=470*preview['height']/preview['width']),
         p(f"Ground model only. Display mesh {preview['meshResolutionM']:g} m, averaged from a 2 m ground raster; vertical exaggeration {preview['verticalExaggeration']:g}x. Colors indicate relative elevation. Missing mesh blocks remain blank. Trees and buildings are not modelled in this image.", 'small'),
         PageBreak(), p('Evidence and outstanding checks', 'title'),
         p('Coverage measurements below refer to the full buffered raster envelope, not a score for the parcel.', 'small'),
         table([
             ('Ground valid coverage', f"{ground['validPercent']:.4f}% at 2 m"),
             ('Surface valid coverage', f"{surface['validPercent']:.4f}% at 2 m"),
             ('Classified land cover', f"{cover['validPercent']:.2f}% at 5 m; unknown cells retained"),
             ('Nearest ground gap', f"{ground['nearestMissingCellToParcelCentroidM']:.0f} m from the parcel centroid"),
             ('Nearest surface gap', f"{surface['nearestMissingCellToParcelCentroidM']:.0f} m from the parcel centroid"),
             ('Source area', '41.91 km² envelope in LKS-94 / EPSG:3346'),
             ('LiDAR files / header points', f"{len(preparation['tiles'])} tiles / {sum(t['points'] for t in preparation['tiles']):,} points"),
             ('Sampled survey dates', '2019-09-23 to 2022-09-25; 1,000 sampled points per tile'),
             ('Height reference', 'Unverified; horizontal EPSG:3346 does not identify a height datum'),
         ]),
         p('Mapped surroundings', 'heading'),
         p(f"GRPK snapshot: {stamp(context['fetchedAt'])}. {context['buildingCount']} unique building footprints were fetched in the parcel envelope buffered by 1.5 km. All 73 queried road features were reviewed; none belongs to the selected TIPAS 1/2 major-road classes. This does not establish access, quietness, distance to the nearest road or absence of other roads."),
         p('Unknown cover includes sd15 (trees/shrubs), sd4 (unused land) and geometric gaps. Source classes need documented mapping and local coverage review. The current context snapshot and LiDAR surveys represent different dates.'),
         p('Assessment status', 'heading'),
         table([
             ('Views', 'Pending: compatible heights and complete obstruction data near observation points'),
             ('Terrain character', 'Visual preview available; parcel-scale relief and gentle-ground metrics pending'),
             ('Natural surroundings', 'Pending: unknown land cover and local coverage checks'),
             ('Seclusion', 'Mapped context available; distances and component score not calculated'),
             ('Access / dwelling / utilities', 'Not assessed by this landscape report'),
         ]),
         p('Next steps', 'heading'),
         p('1. Confirm height-reference compatibility and inspect survey seams/classification.<br/>2. Resolve local ground/surface gaps using documented source evidence.<br/>3. Complete land-cover mapping and coverage checks.<br/>4. Run and review the actual parcel analysis, then publish supported results. Scoring weights still require preference calibration.'),
         p('Sources and scope', 'heading'),
         p(f'<link href="{escape(auction["sourceUrl"], quote=True)}" color="#185d45">Captured official auction</link> | '
           '<link href="https://www.geoportal.lt/metadata-catalog/catalog/search/resource/details.page?uuid=%7B3C0D5E47-49CA-4132-88C1-EC3D1E4610B4%7D" color="#185d45">Geoportal LiDAR catalogue</link> | '
           '<link href="https://www.geoportal.lt/arcgis/rest/services/NZT/GRPK/MapServer" color="#185d45">GRPK map service</link>', 'small'),
         p(escape(preview['attribution']) + '. Local inspection from ordered source data; derivative/publication conditions remain to be checked before wider distribution. The image and data-quality percentages are not calibrated landscape scores.', 'small')]
output = Path(args.output)
output.parent.mkdir(parents=True, exist_ok=True)
if output.exists():
    raise FileExistsError('Report output must be new')
def footer(canvas, doc):
    canvas.setStrokeColor(colors.HexColor('#dbe4da'))
    canvas.line(40, 36, A4[0]-40, 36)
    canvas.setFont('Report', 8); canvas.setFillColor(muted)
    canvas.drawString(40, 23, f"Fluxus | {pilot['cadastralNumber']} | Preliminary inspection")
    canvas.drawRightString(A4[0]-40, 23, f'Page {doc.page}')
doc = SimpleDocTemplate(str(output), pagesize=A4, leftMargin=40, rightMargin=40,
                        topMargin=36, bottomMargin=48, title='Varpučiai preliminary landscape report',
                        author='Fluxus', pageCompression=1)
doc.build(story, onFirstPage=footer, onLaterPages=footer)
metadata = {'offerId': args.offer_id, 'sha256': hashlib.sha256(output.read_bytes()).hexdigest(),
            'createdAt': now.isoformat(), 'offerObservedAt': auction['observedAt']}
output.with_suffix('.json').write_text(json.dumps(metadata, indent=2), encoding='utf-8')
print(output)
