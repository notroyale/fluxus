import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { LandscapeDetail, landscapeLabel } from './landscape-api';

@Component({
  selector: 'app-landscape-details',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="landscape" aria-label="Kraštovaizdžio potencialas">
      <p class="eyebrow">KRAŠTOVAIZDŽIO POTENCIALAS</p>
      <h3>{{ label() }}</h3>
      @if (detail().preliminaryReport; as report) {
        <div class="preliminary-report">
          <p>
            Turimų duomenų apžvalga su reljefo vaizdu ir aprėpties patikromis. Bendras
            kraštovaizdžio balas dar neskaičiuojamas.
          </p>
          <a class="report-link" [href]="report.url" target="_blank" rel="noopener"
            >Atverti preliminarią ataskaitą (PDF) ↗</a
          >
          <p class="provenance">
            Parengta {{ reportDate(report.createdAt) }} · skelbimas fiksuotas
            {{ reportDate(report.offerObservedAt) }}. Ataskaita nepatvirtina dabartinės aukciono
            būsenos.
          </p>
        </div>
      }
      @if (detail().terrainPreview; as preview) {
        <figure class="terrain-preview">
          <h4>3D reljefo peržiūra</h4>
          @if (failedImage() !== preview.url) {
            <a
              [href]="preview.url"
              target="_blank"
              rel="noopener"
              aria-label="Atverti didelį 3D reljefo vaizdą"
            >
              <img
                [src]="preview.url"
                [width]="preview.width"
                [height]="preview.height"
                loading="lazy"
                alt="LiDAR žemės reljefo modelis. Pasirinktas sklypas pažymėtas raudonai."
                (error)="failedImage.set(preview.url)"
              />
            </a>
          } @else {
            <p role="status">Reljefo vaizdas šiuo metu nepasiekiamas.</p>
          }
          <figcaption>
            Raudonas kontūras – pasirinktas sklypas. Aukščių mastelis padidintas
            {{ preview.verticalExaggeration }}×. Spalvos rodo santykinį aukštį; tušti plotai –
            duomenų spragos. <br />Žemės reljefo modelis · {{ preview.meshResolutionM }} m tinklelis
            · {{ preview.surveyYearFrom }}–{{ preview.surveyYearTo }} m. atrinkti matavimai.
            @if (!preview.heightReferenceVerified) {
              <br />Aukščių atskaitos sistema dar nepatvirtinta. Peržiūra nesuteikia kraštovaizdžio
              balo.
            }
            <small>{{ preview.attribution }}</small>
          </figcaption>
        </figure>
      }
      @if (!detail().summary.analysisId) {
        <p>
          {{
            detail().parcelId
              ? 'Šiam sklypui dar neįkelti patikrinti reljefo, augmenijos ir aplinkos duomenys.'
              : 'Prieš vertinant aplinką reikia patvirtinti sklypo vietą.'
          }}
        </p>
      }
      @if (detail().summary.isStale) {
        <p class="note">
          Žemiau pateiktas ankstesnis vertinimas. Jis netaikomas kraštovaizdžio filtrams.
        </p>
      }
      @if (detail().components; as components) {
        <dl class="components">
          @for (component of componentRows(); track component.label) {
            <div>
              <dt>
                {{ component.label }} <small>{{ component.weight }} %</small>
              </dt>
              <dd>{{ component.value === null ? 'Neįvertinta' : component.value + '/100' }}</dd>
              <progress
                [value]="component.value ?? 0"
                max="100"
                [attr.aria-label]="component.label"
              ></progress>
            </div>
          }
        </dl>
      }
      @if (detail().metrics; as metrics) {
        <dl class="facts">
          <div>
            <dt>Tipinis matomas plotas</dt>
            <dd>{{ measurement(metrics.visibleAreaKm2, 'km²') }}</dd>
          </div>
          <div>
            <dt>Geriausias taškas</dt>
            <dd>{{ measurement(metrics.bestVisibleAreaKm2, 'km²') }}</dd>
          </div>
          <div>
            <dt>Patikrinti stebėjimo taškai</dt>
            <dd>{{ metrics.validViewpoints }}</dd>
          </div>
          <div>
            <dt>Reljefo aukščių skirtumas</dt>
            <dd>{{ measurement(metrics.reliefP90P10M, 'm') }}</dd>
          </div>
          <div>
            <dt>Natūrali danga aplink</dt>
            <dd>
              {{
                metrics.naturalCoverFraction === null
                  ? 'Neįvertinta'
                  : number(metrics.naturalCoverFraction * 100, 0) + ' %'
              }}
            </dd>
          </div>
          <div>
            <dt>Artimiausias vanduo</dt>
            <dd>{{ distance(metrics.waterDistanceM, 1000) }}</dd>
          </div>
          <div>
            <dt>Artimiausias pastatas</dt>
            <dd>{{ distance(metrics.nearestBuildingDistanceM, 1000) }}</dd>
          </div>
          <div>
            <dt>Artimiausias pagrindinis kelias</dt>
            <dd>{{ distance(metrics.majorRoadDistanceM, 1500) }}</dd>
          </div>
          <div>
            <dt>Lėkštas plotas (iki 8°)</dt>
            <dd>{{ measurement(metrics.gentleGroundAreaHa, 'ha', 4) }}</dd>
          </div>
          <div>
            <dt>Didžiausias vientisas lėkštas plotas</dt>
            <dd>{{ measurement(metrics.largestGentlePatchHa, 'ha', 4) }}</dd>
          </div>
        </dl>
        <p>
          Vaizdų komponentas remiasi tipiniais, o ne geriausiais taškais. Stebėjimo aukštis – 1,7 m,
          aplinkos spindulys – 3 km.
        </p>
      }
      @if (detail().source; as source) {
        <p class="provenance">
          {{ source.provider }} · {{ source.release }}<br />
          {{ detail().summary.surveyYearFrom }}–{{ detail().summary.surveyYearTo }} m. duomenys ·
          {{ quality() }} duomenų kokybė<br />Reljefas {{ source.groundResolutionM }} m · paviršius
          {{ source.surfaceResolutionM }} m
        </p>
        <p class="attribution">{{ source.attribution }}</p>
        <a [href]="source.sourceUrl" target="_blank" rel="noopener noreferrer">Šaltinis ↗</a> ·
        <a [href]="source.licenseUrl" target="_blank" rel="noopener noreferrer">Licencija ↗</a>
      }
      @if (
        detail().limitations.includes('insufficient_complete_viewpoints') ||
        detail().limitations.includes('ground_context_incomplete') ||
        detail().limitations.includes('land_cover_incomplete') ||
        detail().limitations.includes('visible_cover_incomplete')
      ) {
        <p class="note">Duomenų aprėptis nepakankama. Bendras balas neskaičiuojamas.</p>
      }
      <p class="note">
        Tai pirminis atrankos indeksas pagal reljefą, vaizdus, gamtą ir nuošalumą. Jis dar
        nekalibruotas pagal žmonių vertinimus. Privažiavimo teisės ir galimybė statyti
        nepatikrintos.
      </p>
    </section>
  `,
  styles: `
    .landscape {
      border: 1px solid #cbd8ce;
      border-radius: 12px;
      padding: 18px;
      margin: 24px 0;
      background: #f2f6f0;
    }
    .eyebrow {
      font-size: 10px;
      letter-spacing: 0.15em;
      font-weight: 700;
      color: #326550;
    }
    .terrain-preview {
      margin: 18px 0;
    }
    .report-link {
      display: inline-block;
      padding: 9px 11px;
      border: 1px solid #326550;
      border-radius: 6px;
      font-weight: 600;
    }
    .terrain-preview h4 {
      margin: 0 0 10px;
      color: #174f3e;
      font-size: 15px;
    }
    .terrain-preview img {
      display: block;
      width: 100%;
      height: auto;
      border-radius: 8px;
      background: #f5f6f0;
    }
    .terrain-preview figcaption {
      margin-top: 10px;
      font-size: 11px;
      line-height: 1.6;
      color: #516054;
    }
    .terrain-preview small {
      display: block;
      margin-top: 6px;
      font-size: 10px;
    }
    h3 {
      margin: 8px 0 12px;
      color: #174f3e;
      font-size: 22px;
    }
    p {
      font-size: 12px;
      line-height: 1.6;
      color: #516054;
    }
    .components {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 12px;
    }
    .components dt {
      font-size: 12px;
    }
    .components dd {
      margin: 5px 0;
      font-weight: 700;
    }
    small {
      color: #67796c;
    }
    progress {
      width: 100%;
      height: 6px;
      accent-color: #237e5e;
    }
    .facts div {
      display: flex;
      justify-content: space-between;
      gap: 12px;
      margin: 9px 0;
      font-size: 11px;
    }
    .facts dd {
      margin: 0;
      text-align: right;
      white-space: nowrap;
    }
    .facts dt {
      color: #56685b;
    }
    .note {
      border-top: 1px solid #d3dfd5;
      padding-top: 12px;
    }
    a {
      color: #145d43;
      font-size: 12px;
    }
    .provenance,
    .attribution {
      font-size: 10px;
    }
  `,
})
export class LandscapeDetails {
  readonly detail = input.required<LandscapeDetail>();
  protected readonly failedImage = signal<string | null>(null);
  protected readonly label = computed(() =>
    this.detail().preliminaryReport
      ? 'Preliminari ataskaita'
      : landscapeLabel(this.detail().summary),
  );
  protected reportDate(value: string) {
    return new Intl.DateTimeFormat('lt-LT', {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    }).format(new Date(value));
  }
  protected readonly quality = computed(
    () =>
      ({ high: 'aukšta', medium: 'vidutinė', low: 'ribota', unknown: 'nežinoma' })[
        this.detail().summary.quality
      ],
  );
  protected readonly componentRows = computed(() => [
    { label: 'Vaizdai', weight: 35, value: this.detail().components?.views ?? null },
    { label: 'Reljefas', weight: 20, value: this.detail().components?.terrain ?? null },
    { label: 'Gamta', weight: 30, value: this.detail().components?.nature ?? null },
    { label: 'Nuošalumas', weight: 15, value: this.detail().components?.seclusion ?? null },
  ]);
  protected number(value: number | null, decimals = 2) {
    return value === null
      ? 'Neįvertinta'
      : new Intl.NumberFormat('lt-LT', { maximumFractionDigits: decimals }).format(value);
  }
  protected distance(value: number | null, saturation: number) {
    return value === null
      ? 'Neįvertinta'
      : `${value >= saturation ? '≥ ' : ''}${this.number(value, 0)} m`;
  }
  protected measurement(value: number | null, unit: string, decimals = 2) {
    return value === null ? 'Neįvertinta' : `${this.number(value, decimals)} ${unit}`;
  }
}
