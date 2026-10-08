import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  output,
  resource,
  signal,
} from '@angular/core';
import { CatalogRefreshApi } from './catalog-refresh-api';

@Component({
  selector: 'app-catalog-refresh',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      class="refresh-button"
      (click)="start()"
      [disabled]="
        pending() || busy() || coolingDown() || !status.hasValue() || !status.value()?.enabled
      "
    >
      {{ busy() || pending() ? 'Atnaujinama…' : 'Atnaujinti duomenis' }}
    </button>
    @if (error() || status.error(); as problem) {
      <div class="refresh-panel" role="alert">
        {{ message(problem) }}
        <button type="button" class="retry" (click)="retryStatus()">Tikrinti būseną</button>
      </div>
    } @else if (status.hasValue()) {
      @let value = status.value();
      @if (value.status !== 'idle' && (busy() || dismissedId() !== value.id)) {
        <div class="refresh-panel" role="status" aria-live="polite">
          @if (busy()) {
            <strong>Gaunami nauji aukcionų duomenys</strong>
            <p>{{ phase(value.phase) }} · išsaugota {{ value.auctions }} aukcionų.</p>
            <small
              >Tikrinama iki {{ value.maxAuctions }} aukcionų. Tai gali užtrukti kelias
              minutes.</small
            >
          } @else {
            <button
              type="button"
              class="dismiss"
              aria-label="Uždaryti atnaujinimo rezultatą"
              (click)="dismissedId.set(value.id)"
            >
              ×
            </button>
            <strong>{{
              value.status === 'failed' ? 'Atnaujinimas nebaigtas' : 'Duomenys atnaujinti'
            }}</strong>
            <p>
              {{ value.auctions }} aukcionų · {{ value.items }} pasiūlymų ·
              {{ value.mapped }} žemėlapyje.
            </p>
            @if (value.failedAuctions > 0) {
              <p>{{ value.failedAuctions }} aukcionų atnaujinti nepavyko.</p>
            }
            @if (value.errorCode) {
              <p>{{ failure(value.errorCode) }}</p>
            }
            @if (value.discoveryComplete !== true) {
              <small
                >Atnaujinta katalogo dalis. Ankstesni įrašai išlaikyti; jų būsena galėjo
                nepasikeisti.</small
              >
            }
            @if (coolingDown()) {
              <p class="cooldown">Naujas atnaujinimas galimas po minutės.</p>
            }
          }
        </div>
      }
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .refresh-button {
      border: 1px solid #165c47;
      border-radius: 7px;
      padding: 9px 13px;
      background: #f1f7f2;
      color: #165c47;
      font: inherit;
      font-size: 12px;
      font-weight: 650;
      cursor: pointer;
    }
    .refresh-button:disabled {
      cursor: default;
      opacity: 0.65;
    }
    .refresh-button:focus-visible,
    .retry:focus-visible {
      outline: 2px solid #165c47;
      outline-offset: 3px;
    }
    .refresh-panel {
      position: absolute;
      top: 86px;
      right: 24px;
      width: min(310px, calc(100vw - 48px));
      box-sizing: border-box;
      padding: 15px;
      border: 1px solid #d3dfd5;
      border-radius: 10px;
      background: #fff;
      box-shadow: 0 4px 18px #18392e18;
      z-index: 6;
      color: #243f33;
      font-size: 12px;
    }
    .refresh-panel p {
      margin: 7px 0;
      line-height: 1.5;
    }
    .dismiss {
      position: absolute;
      top: 8px;
      right: 9px;
      border: 0;
      background: none;
      font-size: 19px;
      color: #65796d;
      cursor: pointer;
    }
    .refresh-panel strong {
      display: block;
      padding-right: 16px;
    }
    small,
    .cooldown {
      color: #65796d;
      line-height: 1.5;
    }
    .retry {
      display: block;
      margin-top: 8px;
      border: 0;
      padding: 0;
      background: none;
      color: #165c47;
      text-decoration: underline;
      cursor: pointer;
    }
    @media (max-width: 760px) {
      :host {
        margin-left: auto;
      }
      .refresh-panel {
        top: 74px;
      }
      .refresh-button {
        padding: 8px;
        font-size: 11px;
      }
    }
  `,
})
export class CatalogRefresh {
  private readonly api = inject(CatalogRefreshApi);
  private readonly destroy = inject(DestroyRef);
  private readonly startAbort = new AbortController();
  private observedJob: string | null = null;
  readonly refreshed = output();
  protected readonly pending = signal(false);
  protected readonly error = signal<unknown>(null);
  protected readonly dismissedId = signal<string | null>(null);
  protected readonly now = signal(Date.now());
  protected readonly status = resource({
    loader: ({ abortSignal }) => this.api.status(abortSignal),
  });
  protected readonly busy = computed(
    () => this.status.hasValue() && ['queued', 'running'].includes(this.status.value().status),
  );
  protected readonly coolingDown = computed(
    () =>
      this.status.hasValue() &&
      this.status.value().retryAt !== null &&
      Date.parse(this.status.value().retryAt!) > this.now(),
  );

  constructor() {
    effect((onCleanup) => {
      if (!this.status.hasValue()) return;
      const value = this.status.value();
      if (value.status === 'queued' || value.status === 'running') this.observedJob = value.id;
      else if (value.id !== null && value.id === this.observedJob) {
        this.observedJob = null;
        // Reload catalogue/detail even after a partial failure: accepted records may have changed.
        this.refreshed.emit();
      }
      if (
        ['queued', 'running'].includes(value.status) ||
        (value.retryAt && Date.parse(value.retryAt) > Date.now())
      ) {
        const timer = setTimeout(() => {
          this.now.set(Date.now());
          this.status.reload();
        }, 2_000);
        onCleanup(() => clearTimeout(timer));
      }
    });
    this.destroy.onDestroy(() => this.startAbort.abort());
  }

  protected async start() {
    if (this.pending() || this.busy()) return;
    this.pending.set(true);
    this.error.set(null);
    try {
      const latest = await this.api.start(this.startAbort.signal);
      // Track the accepted job even if it finishes before the next read.
      this.observedJob = latest.id;
      this.status.reload();
    } catch (error) {
      if (!this.startAbort.signal.aborted) {
        this.error.set(error);
        this.status.reload();
      }
    } finally {
      this.pending.set(false);
    }
  }

  protected retryStatus() {
    this.error.set(null);
    this.status.reload();
  }
  protected message(error: unknown) {
    return error instanceof Error ? error.message : 'Atnaujinimo būsena nepasiekiama.';
  }
  protected phase(value: string | null) {
    return (
      (
        {
          listing: 'Ieškomi aukcionai',
          details: 'Tikrinami skelbimai',
          geometry: 'Tikrinamos sklypų ribos',
          saving: 'Išsaugomi pasiūlymai',
        } as Record<string, string>
      )[value ?? ''] ?? 'Laukiama'
    );
  }
  protected failure(value: string) {
    return (
      (
        {
          import_busy: 'Kitas importas jau vyksta. Bandykite vėliau.',
          timed_out: 'Viršyta atnaujinimo trukmė.',
          interrupted: 'Atnaujinimas nutrauktas.',
          no_auctions: 'Naujų aukcionų įrašų išsaugoti nepavyko.',
          source_failed: 'Šaltinio arba išsaugojimo klaida. Bandykite vėliau.',
        } as Record<string, string>
      )[value] ?? 'Atnaujinimas nebaigtas.'
    );
  }
}
