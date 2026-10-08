import { DestroyRef, effect, inject, Injectable, resource, signal } from '@angular/core';
import { OfferTrackingApi } from './offer-tracking-api';

export const shortlistKey = 'fluxus.shortlist.v1';
export const shortlistLimit = 4;
export function readShortlist(raw: string | null): string[] {
  try {
    const value: unknown = JSON.parse(raw ?? '[]');
    if (!Array.isArray(value)) return [];
    return [
      ...new Set(
        value.filter(
          (id): id is string => typeof id === 'string' && /^[a-zA-Z0-9-]{1,64}$/.test(id),
        ),
      ),
    ].slice(0, shortlistLimit);
  } catch {
    return [];
  }
}
@Injectable({ providedIn: 'root' })
export class ShortlistStore {
  private readonly api = inject(OfferTrackingApi);
  private readonly destroy = inject(DestroyRef);
  private readonly selected = signal(this.read());
  readonly ids = this.selected.asReadonly();
  readonly message = signal<string | null>(null);
  readonly offers = resource({
    params: () => (this.ids().length ? this.ids() : undefined),
    loader: ({ params, abortSignal }) => this.api.shortlist(params, abortSignal),
  });
  constructor() {
    effect(() => {
      const ids = this.ids();
      try {
        localStorage.setItem(shortlistKey, JSON.stringify(ids));
      } catch {
        this.message.set(
          'Naršyklė neleidžia išsaugoti sąrašo. Jis veiks, kol neuždarysite puslapio.',
        );
      }
    });
    const onStorage = (event: StorageEvent) => {
      if (event.storageArea === localStorage && (event.key === shortlistKey || event.key === null))
        this.selected.set(readShortlist(event.newValue));
    };
    window.addEventListener('storage', onStorage);
    this.destroy.onDestroy(() => window.removeEventListener('storage', onStorage));
  }
  contains(id: string): boolean {
    return this.ids().includes(id);
  }
  toggle(id: string): void {
    this.message.set(null);
    if (this.contains(id)) this.remove(id);
    else if (this.ids().length >= shortlistLimit)
      this.message.set(
        'Galite palyginti iki 4 sklypų. Pirmiausia pašalinkite vieną išsaugotą sklypą.',
      );
    else this.selected.update((ids) => [...ids, id]);
  }
  remove(id: string): void {
    this.message.set(null);
    this.selected.update((ids) => ids.filter((value) => value !== id));
  }
  private read(): string[] {
    try {
      return readShortlist(localStorage.getItem(shortlistKey));
    } catch {
      return [];
    }
  }
}
