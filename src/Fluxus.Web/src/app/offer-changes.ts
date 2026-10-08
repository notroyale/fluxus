import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ItemCard } from './catalog-api';
import { isRecentlyAdded } from './offer-recency';
import { date, money, status } from './offer-format';

@Component({
  selector: 'app-offer-changes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="changes">
      @if (recent(card().firstSeenAt)) {
        <span class="new">Naujas · per 5 d.</span>
      }
      @if (recent(card().priceChangedAt)) {
        <span
          [title]="
            'Pradinė kaina: ' +
            money(card().previousInitialPriceEur) +
            ' → ' +
            money(card().initialPriceEur) +
            ' · ' +
            date(card().priceChangedAt)
          "
          >Kaina pasikeitė</span
        >
      }
      @if (recent(card().statusChangedAt)) {
        <span
          [title]="
            status(card().previousStatus ?? '') +
            ' → ' +
            status(card().status) +
            ' · ' +
            date(card().statusChangedAt)
          "
          >{{ card().status === 'ended' ? 'Aukcionas baigtas' : 'Būsena pasikeitė' }}</span
        >
      }
      @if (!card().isPresent) {
        <span>Šaltinio detalėje neberodomas</span>
      }
      @if (overdue()) {
        <span>Terminas praėjo · būsena netikrinta</span>
      }
    </div>
  `,
  styles: `
    .changes {
      display: flex;
      flex-wrap: wrap;
      gap: 5px;
      margin-top: 8px;
      font-size: 10px;
      color: #765d25;
    }
    span {
      padding: 4px 6px;
      border-radius: 4px;
      background: #fff4d9;
    }
    .new {
      color: #215b40;
      background: #e0f3e6;
    }
  `,
})
export class OfferChanges {
  readonly card = input.required<ItemCard>();
  readonly now = input.required<number>();
  protected readonly money = money;
  protected readonly date = date;
  protected readonly status = status;
  protected recent(value: string | null): boolean {
    return isRecentlyAdded(value, this.now());
  }
  protected readonly overdue = computed(() => {
    const card = this.card();
    return (
      ['announced', 'running'].includes(card.status) &&
      card.endsAt !== null &&
      Date.parse(card.endsAt) < this.now()
    );
  });
}
