import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { ItemCard } from './catalog-api';
import { ShortlistStore } from './shortlist-store';

@Component({
  selector: 'app-save-offer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<button
    type="button"
    [attr.aria-pressed]="store.contains(card().id)"
    (click)="store.toggle(card().id)"
    [attr.aria-label]="
      (store.contains(card().id) ? 'Pašalinti iš išsaugotų' : 'Išsaugoti sklypą') +
      ' · aukcionas ' +
      card().number +
      (label() ? ' · ' + label() : '')
    "
  >
    {{ store.contains(card().id) ? '✓ Išsaugotas' : '☆ Išsaugoti' }}
    @if (label()) {
      <span>{{ label() }}</span>
    }
  </button>`,
  styles: `
    :host {
      display: block;
      margin: 8px 0 14px;
    }
    button {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      width: 100%;
      border: 1px solid #c9dacf;
      border-radius: 6px;
      padding: 9px 10px;
      background: white;
      color: #245f46;
      font: inherit;
      font-size: 11px;
      cursor: pointer;
    }
    button[aria-pressed='true'] {
      background: #e8f3e9;
    }
    span {
      font-size: 10px;
      color: #587361;
    }
    button:focus-visible {
      outline: 2px solid #236347;
      outline-offset: 2px;
    }
  `,
})
export class SaveOffer {
  protected readonly store = inject(ShortlistStore);
  readonly card = input.required<ItemCard>();
  readonly label = input('');
}
